using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OsuTracker.Web.Data;
using OsuTracker.Web.Data.Entities;
using OsuTracker.Web.OsuApi;

namespace OsuTracker.Web.Sync;

/// <summary>
/// Polls the 24-hour recent-scores window for each mode. Costs 4 requests and keeps the
/// tracker current forever — once backfill has run, this is the only job that needs to
/// keep running. It is also the only path by which a brand-new NoFail pass on a map you
/// have never beaten will ever be recorded, since backfill never revisits a map.
///
/// The window holds at most your newest 100 passes per mode, so it has to be polled more
/// often than you can pass 100 maps in one mode: anything older is gone from the API.
/// </summary>
public sealed class RecentScoresJob(
    OsuApiClient api,
    IDbContextFactory<TrackerDbContext> dbFactory,
    ILogger<RecentScoresJob> log)
{
    public const string JobName = "RecentScores";

    private static readonly (GameMode Mode, string Slug)[] Modes =
    [
        (GameMode.Osu, "osu"), (GameMode.Taiko, "taiko"),
        (GameMode.Fruits, "fruits"), (GameMode.Mania, "mania")
    ];

    public async Task RunAsync(CancellationToken ct)
    {
        await MarkAsync(SyncJobState.Running, null);

        var seen = 0; var newPasses = 0; var upgraded = 0; var rejected = 0; var skipped = 0;

        try
        {
            foreach (var (mode, slug) in Modes)
            {
                // Passes only. However it is paged, the endpoint returns at most the newest
                // 100 plays (osu-web caps offset + limit at 100), and with include_fails=1
                // failed plays took their share of those — rows this job then threw away,
                // while the passes they pushed out of the window were lost for good.
                using var doc = await api.GetAsync(
                    $"/users/{api.UserId}/scores/recent?mode={slug}&limit=100", ct);
                if (doc is null) continue;

                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Array) continue;

                foreach (var e in root.EnumerateArray())
                {
                    seen++;
                    var outcome = await IngestAsync(e, mode, ct);
                    switch (outcome)
                    {
                        case Ingest.NewPass: newPasses++; break;
                        case Ingest.Upgraded: upgraded++; break;
                        case Ingest.PreRank: rejected++; break;
                        default: skipped++; break;
                    }
                }
            }

            await MarkAsync(SyncJobState.Completed, null);
            log.LogInformation(
                "Recent scores: {Seen} seen · {New} new passes · {Upgraded} updated · {Rejected} pre-rank · {Skipped} skipped",
                seen, newPasses, upgraded, rejected, skipped);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Nothing to resume: the next poll re-reads the same 24-hour window.
            await MarkAsync(SyncJobState.Paused, null);
            log.LogWarning("Recent scores poll cancelled — the next poll covers the same window");
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Recent scores poll failed");
            await MarkAsync(SyncJobState.Failed, SyncError.Describe(ex));
            throw;
        }
    }

    private enum Ingest { NewPass, Upgraded, PreRank, Skipped }

    private async Task<Ingest> IngestAsync(JsonElement e, GameMode requestedMode, CancellationToken ct)
    {
        // TryGetProperty throws on anything but an object, so without this one malformed
        // element failed the poll for every score after it.
        if (e.ValueKind != JsonValueKind.Object) return Ingest.Skipped;

        // A failed run is not a pass. Only passes are asked for, but the flag is checked
        // when present rather than assumed.
        if (e.TryGetProperty("passed", out var passed) && passed.ValueKind == JsonValueKind.False)
            return Ingest.Skipped;

        var beatmapId = e.TryGetProperty("beatmap", out var bm) ? GetLong(bm, "id") : GetLong(e, "beatmap_id");
        if (beatmapId is null) return Ingest.Skipped;

        var when = Str(e, "ended_at") ?? Str(e, "created_at");
        if (when is null || !DateTimeOffset.TryParse(when, out var playedAt)) return Ingest.Skipped;

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var target = await (from b in db.Beatmaps
                            join bs in db.Beatmapsets on b.BeatmapsetId equals bs.Id
                            where b.Id == beatmapId
                            select new { b.Id, b.Mode, b.Status, bs.RankedDate })
                           .FirstOrDefaultAsync(ct);

        // Not in the catalogue at all — graveyard, loved, or pending. Nothing to count.
        if (target is null) return Ingest.Skipped;

        // Rule 3: only ranked and approved contribute.
        if (target.Status is not (BeatmapStatus.Ranked or BeatmapStatus.Approved)) return Ingest.Skipped;

        // Rule 2, the converts guard. A catch score on an osu!-native map fails here and
        // is dropped before it can reach the database.
        if (target.Mode != requestedMode) return Ingest.Skipped;

        // Rule 5.
        if (target.RankedDate is null || playedAt < target.RankedDate.Value) return Ingest.PreRank;

        var mods = new List<string>();
        if (e.TryGetProperty("mods", out var m) && m.ValueKind == JsonValueKind.Array)
            foreach (var x in m.EnumerateArray())
            {
                if (x.ValueKind == JsonValueKind.String) mods.Add(x.GetString()!);
                else if (x.ValueKind == JsonValueKind.Object && Str(x, "acronym") is { } a) mods.Add(a);
            }

        var existing = await db.Scores.FindAsync([beatmapId.Value], ct);
        var accuracy = Dbl(e, "accuracy") ?? 0;
        var modList = mods.Count == 0 ? null : string.Join(",", mods);

        if (existing is null)
        {
            var row = new Score
            {
                BeatmapId = beatmapId.Value,
                PlayedAt = playedAt,
                PlayedAtUnix = playedAt.ToUnixTimeSeconds(),
                FetchedAt = DateTimeOffset.UtcNow,
                CountsAsPass = true
            };
            Show(row, e, accuracy, modList);
            db.Scores.Add(row);
            await db.SaveChangesAsync(ct);
            return Ingest.NewPass;
        }

        // Already have a row. Promote a pre-rank-only row to a real pass, or refresh the
        // display fields if this run was better — neither changes the count under Rule 4,
        // but the row should show your best work.
        //
        // The date is a different matter: PlayedAt is when the map was first passed, and
        // it is what "passed in the last 30 days" counts. A better run of an old pass used
        // to re-date it to today, so replaying for accuracy inflated the pace projection.
        // Only a promotion (the pre-rank date never qualified) or an earlier run moves it.
        var wasNotCounted = !existing.CountsAsPass;
        var better = accuracy > existing.Accuracy;
        var earlier = wasNotCounted || playedAt < existing.PlayedAt;
        if (!wasNotCounted && !better && !earlier) return Ingest.Skipped;

        if (earlier)
        {
            existing.PlayedAt = playedAt;
            existing.PlayedAtUnix = playedAt.ToUnixTimeSeconds();
        }
        if (wasNotCounted || better) Show(existing, e, accuracy, modList);
        existing.CountsAsPass = true;
        existing.FetchedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return wasNotCounted ? Ingest.NewPass : Ingest.Upgraded;
    }

    /// <summary>
    /// Every display field from one run, set together. Each write site used to copy them
    /// out by hand, and the upgrade path missed IsLazer — so an upgraded row could show a
    /// lazer score's grade and mods flagged as a stable one.
    /// </summary>
    private static void Show(Score row, JsonElement e, double accuracy, string? mods)
    {
        row.ScoreId = GetLong(e, "id") ?? row.ScoreId;
        row.Grade = Str(e, "rank");
        row.Accuracy = accuracy;
        row.MaxCombo = GetInt(e, "max_combo");
        row.Mods = mods;
        row.IsLazer = e.TryGetProperty("build_id", out var b) && b.ValueKind == JsonValueKind.Number;
    }

    /// <summary>Not cancellable, for the same reason as CatalogSyncJob's: it is what records a cancel.</summary>
    private async Task MarkAsync(SyncJobState state, string? error)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var j = await db.SyncJobs.FindAsync([JobName]);
        if (j is null) { j = new SyncJob { Name = JobName }; db.SyncJobs.Add(j); }
        j.State = state;
        j.Error = error;
        j.LastRunAt = DateTimeOffset.UtcNow;
        if (state == SyncJobState.Completed) j.LastSuccessAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    private static string? Str(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? GetLong(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
    private static int? GetInt(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    private static double? Dbl(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
