using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OsuTracker.Web.Data;
using OsuTracker.Web.Data.Entities;
using OsuTracker.Web.OsuApi;

namespace OsuTracker.Web.Sync;

/// <summary>
/// Fetches your score for every map in the backfill queue — the intersection of the
/// catalogue and your playcounts. Anything outside that intersection is Untouched by
/// definition and is never requested, which is what turns a 215,000-request scan into
/// a 15,854-request one.
/// </summary>
public sealed class ScoreBackfillJob(
    OsuApiClient api,
    IDbContextFactory<TrackerDbContext> dbFactory,
    ILogger<ScoreBackfillJob> log)
{
    public const string JobName = "ScoreBackfill";

    private static string ModeSlug(GameMode m) => m switch
    {
        GameMode.Osu => "osu",
        GameMode.Taiko => "taiko",
        GameMode.Fruits => "fruits",
        GameMode.Mania => "mania",
        _ => "osu"
    };

    public async Task RunAsync(bool resume, GameMode? onlyMode, int? limit, CancellationToken ct)
    {
        var jobName = onlyMode is null ? JobName : $"{JobName}:{onlyMode}";

        long cursor = 0;
        await using (var seed = await dbFactory.CreateDbContextAsync(ct))
        {
            var j = await seed.SyncJobs.FindAsync([jobName], ct);
            if (j is null) { j = new SyncJob { Name = jobName }; seed.SyncJobs.Add(j); }
            if (resume && long.TryParse(j.Cursor, out var stored)) cursor = stored;
            else { j.ItemsDone = 0; j.Cursor = null; }

            j.State = SyncJobState.Running;
            j.LastRunAt = DateTimeOffset.UtcNow;
            j.RunStartedAt = DateTimeOffset.UtcNow;
            j.Error = null;
            await seed.SaveChangesAsync(ct);
        }

        var queue = await LoadQueueAsync(onlyMode, cursor, limit, ct);
        log.LogInformation("Backfill queue: {Count} maps{Mode}{Resume}",
            queue.Count,
            onlyMode is null ? "" : $" ({onlyMode})",
            cursor > 0 ? $", resuming after beatmap {cursor}" : "");

        var done = 0; var passes = 0; var preRank = 0; var noScore = 0; var errors = 0;
        var startedAt = DateTimeOffset.UtcNow;

        try
        {
            foreach (var item in queue)
            {
                if (ct.IsCancellationRequested) break;

                Outcome outcome;
                try
                {
                    outcome = await FetchAndStoreAsync(item, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    // A single unlucky map must not end a multi-thousand-map run. The
                    // cursor still advances; the map simply stays unfetched and will be
                    // picked up by the next pass, since the queue excludes only maps
                    // that already have a score row.
                    errors++;
                    log.LogWarning("Beatmap {Id} failed ({Kind}: {Message}) — skipping",
                        item.BeatmapId, ex.GetType().Name, ex.Message);
                    outcome = Outcome.None;
                    noScore--; // do not count a failure as "never passed"
                }

                switch (outcome)
                {
                    case Outcome.Pass: passes++; break;
                    case Outcome.PreRankOnly: preRank++; break;
                    default: noScore++; break;
                }

                done++;
                cursor = item.BeatmapId;

                if (done % 25 == 0 || done == queue.Count)
                {
                    await using var jdb = await dbFactory.CreateDbContextAsync(ct);
                    var j = await jdb.SyncJobs.FindAsync([jobName], ct);
                    if (j is not null)
                    {
                        j.Cursor = cursor.ToString();
                        j.ItemsDone = done;
                        j.ItemsTotal = queue.Count;
                        await jdb.SaveChangesAsync(ct);
                    }
                }

                if (done % 200 == 0)
                {
                    var rate = done / Math.Max(1, (DateTimeOffset.UtcNow - startedAt).TotalMinutes);
                    var eta = TimeSpan.FromMinutes((queue.Count - done) / Math.Max(1, rate));
                    log.LogInformation("  {Done}/{Total} · {Passes} passes · {PreRank} pre-rank · {NoScore} none · eta {Eta:hh\\:mm}",
                        done, queue.Count, passes, preRank, noScore, eta);
                }
            }

            if (ct.IsCancellationRequested)
            {
                await MarkAsync(jobName, SyncJobState.Paused, null, ct);
                log.LogWarning("Backfill cancelled after {Done} maps — rerun with --resume", done);
                return;
            }

            await MarkAsync(jobName, SyncJobState.Completed, null, ct);
            log.LogInformation(
                "Backfill complete: {Done} maps · {Passes} qualifying passes · {PreRank} pre-rank only · {NoScore} never passed · {Errors} errors",
                done, passes, preRank, noScore, errors);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Backfill failed after {Done} maps", done);
            await MarkAsync(jobName, SyncJobState.Failed, ex.Message, ct);
            throw;
        }
    }

    private enum Outcome { Pass, PreRankOnly, None }

    private sealed record QueueItem(long BeatmapId, GameMode Mode, DateTimeOffset? RankedDate);

    /// <summary>
    /// The queue is the catalogue intersected with playcounts, minus anything already
    /// fetched. Ordered by id so the cursor is a simple "greater than" on resume.
    /// </summary>
    private async Task<List<QueueItem>> LoadQueueAsync(GameMode? onlyMode, long cursor, int? limit, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var q =
            from b in db.Beatmaps
            join p in db.PlayCounts on b.Id equals p.BeatmapId
            join bs in db.Beatmapsets on b.BeatmapsetId equals bs.Id
            where (b.Status == BeatmapStatus.Ranked || b.Status == BeatmapStatus.Approved)
                  && p.Count > 0
                  && b.Id > cursor
                  && !db.Scores.Any(s => s.BeatmapId == b.Id)
            select new { b.Id, b.Mode, bs.RankedDate };

        if (onlyMode is not null) q = q.Where(x => x.Mode == onlyMode);

        var ordered = q.OrderBy(x => x.Id);
        var rows = limit is not null
            ? await ordered.Take(limit.Value).ToListAsync(ct)
            : await ordered.ToListAsync(ct);

        return rows.Select(r => new QueueItem(r.Id, r.Mode, r.RankedDate)).ToList();
    }

    private async Task<Outcome> FetchAndStoreAsync(QueueItem item, CancellationToken ct)
    {
        var slug = ModeSlug(item.Mode);

        // Prefer /all: the best-score endpoint returns only your top score, which can be
        // a pre-rank one hiding a valid post-rank pass underneath it.
        using var doc = await api.GetAsync(
            $"/beatmaps/{item.BeatmapId}/scores/users/{api.UserId}/all?mode={slug}", ct)
            ?? await api.GetAsync($"/beatmaps/{item.BeatmapId}/scores/users/{api.UserId}?mode={slug}", ct);

        if (doc is null) return Outcome.None;

        var scores = ExtractScores(doc.RootElement);
        if (scores.Count == 0) return Outcome.None;

        // Rule 5, decided once, here. A pass qualifies only if it happened on or after
        // the set was ranked; a null rank date can never qualify (there are none today,
        // but treating unknown as qualifying would silently inflate every count).
        var qualifying = item.RankedDate is null
            ? []
            : scores.Where(s => s.PlayedAt >= item.RankedDate.Value).ToList();

        var chosen = qualifying.Count > 0
            ? qualifying.OrderByDescending(s => s.Accuracy).First()
            : scores.OrderByDescending(s => s.Accuracy).First();

        var countsAsPass = qualifying.Count > 0;

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            db.Scores.Add(new Score
            {
                BeatmapId = item.BeatmapId,
                ScoreId = chosen.ScoreId,
                PlayedAt = chosen.PlayedAt,
                PlayedAtUnix = chosen.PlayedAt.ToUnixTimeSeconds(),
                FetchedAt = DateTimeOffset.UtcNow,
                CountsAsPass = countsAsPass,
                Grade = chosen.Grade,
                Accuracy = chosen.Accuracy,
                MaxCombo = chosen.MaxCombo,
                Mods = chosen.Mods,
                IsLazer = chosen.IsLazer
            });
            await db.SaveChangesAsync(ct);
        }

        return countsAsPass ? Outcome.Pass : Outcome.PreRankOnly;
    }

    private sealed record ParsedScore(
        long ScoreId, DateTimeOffset PlayedAt, string? Grade, double Accuracy,
        int? MaxCombo, string? Mods, bool IsLazer);

    /// <summary>Handles both the /all envelope and a bare single-score response.</summary>
    private static List<ParsedScore> ExtractScores(JsonElement root)
    {
        var result = new List<ParsedScore>();

        JsonElement arr;
        if (root.TryGetProperty("scores", out var s) && s.ValueKind == JsonValueKind.Array) arr = s;
        else if (root.ValueKind == JsonValueKind.Array) arr = root;
        else if (root.TryGetProperty("score", out var one) && one.ValueKind == JsonValueKind.Object)
        {
            var p = ParseOne(one);
            if (p is not null) result.Add(p);
            return result;
        }
        else
        {
            var p = ParseOne(root);
            if (p is not null) result.Add(p);
            return result;
        }

        foreach (var e in arr.EnumerateArray())
        {
            var p = ParseOne(e);
            if (p is not null) result.Add(p);
        }
        return result;
    }

    private static ParsedScore? ParseOne(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;

        // A failed run is not a pass. The endpoint normally returns only submitted
        // passes, but the flag is checked when present rather than assumed.
        if (e.TryGetProperty("passed", out var passed) && passed.ValueKind == JsonValueKind.False)
            return null;

        var when = Str(e, "ended_at") ?? Str(e, "created_at");
        if (when is null || !DateTimeOffset.TryParse(when, out var playedAt)) return null;

        var mods = new List<string>();
        if (e.TryGetProperty("mods", out var m) && m.ValueKind == JsonValueKind.Array)
            foreach (var x in m.EnumerateArray())
            {
                if (x.ValueKind == JsonValueKind.String) mods.Add(x.GetString()!);
                else if (x.ValueKind == JsonValueKind.Object && Str(x, "acronym") is { } a) mods.Add(a);
            }

        var isLazer = e.TryGetProperty("build_id", out var b) && b.ValueKind == JsonValueKind.Number;

        return new ParsedScore(
            Long(e, "id") ?? 0,
            playedAt,
            Str(e, "rank"),
            Dbl(e, "accuracy") ?? 0,
            Int(e, "max_combo"),
            mods.Count == 0 ? null : string.Join(",", mods),
            isLazer);
    }

    private async Task MarkAsync(string jobName, SyncJobState state, string? error, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var j = await db.SyncJobs.FindAsync([jobName], ct);
        if (j is null) return;
        j.State = state;
        j.Error = error;
        if (state == SyncJobState.Completed) { j.LastSuccessAt = DateTimeOffset.UtcNow; j.Cursor = null; }
        await db.SaveChangesAsync(ct);
    }

    private static string? Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? Long(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
    private static int? Int(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    private static double? Dbl(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
