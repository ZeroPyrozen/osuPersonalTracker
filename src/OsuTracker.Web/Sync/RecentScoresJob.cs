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
        await MarkAsync(SyncJobState.Running, null, ct);

        var seen = 0; var newPasses = 0; var upgraded = 0; var rejected = 0; var skipped = 0;

        try
        {
            foreach (var (mode, slug) in Modes)
            {
                using var doc = await api.GetAsync(
                    $"/users/{api.UserId}/scores/recent?mode={slug}&limit=100&include_fails=1", ct);
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

            await MarkAsync(SyncJobState.Completed, null, ct);
            log.LogInformation(
                "Recent scores: {Seen} seen · {New} new passes · {Upgraded} updated · {Rejected} pre-rank · {Skipped} skipped",
                seen, newPasses, upgraded, rejected, skipped);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Recent scores poll failed");
            await MarkAsync(SyncJobState.Failed, ex.Message, ct);
            throw;
        }
    }

    private enum Ingest { NewPass, Upgraded, PreRank, Skipped }

    private async Task<Ingest> IngestAsync(JsonElement e, GameMode requestedMode, CancellationToken ct)
    {
        // A failed run is not a pass. include_fails=1 is on so we can count the play,
        // but only a completed run may produce a Score row.
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

        if (existing is null)
        {
            db.Scores.Add(new Score
            {
                BeatmapId = beatmapId.Value,
                ScoreId = GetLong(e, "id") ?? 0,
                PlayedAt = playedAt,
                PlayedAtUnix = playedAt.ToUnixTimeSeconds(),
                FetchedAt = DateTimeOffset.UtcNow,
                CountsAsPass = true,
                Grade = Str(e, "rank"),
                Accuracy = accuracy,
                MaxCombo = GetInt(e, "max_combo"),
                Mods = mods.Count == 0 ? null : string.Join(",", mods),
                IsLazer = e.TryGetProperty("build_id", out var b) && b.ValueKind == JsonValueKind.Number
            });
            await db.SaveChangesAsync(ct);
            return Ingest.NewPass;
        }

        // Already have a row. Promote a pre-rank-only row to a real pass, or refresh the
        // display fields if this run was better — neither changes the count under Rule 4,
        // but the row should show your best work.
        var wasNotCounted = !existing.CountsAsPass;
        if (wasNotCounted || accuracy > existing.Accuracy)
        {
            existing.CountsAsPass = true;
            existing.ScoreId = GetLong(e, "id") ?? existing.ScoreId;
            existing.PlayedAt = playedAt;
            existing.PlayedAtUnix = playedAt.ToUnixTimeSeconds();
            existing.FetchedAt = DateTimeOffset.UtcNow;
            existing.Grade = Str(e, "rank");
            existing.Accuracy = accuracy;
            existing.MaxCombo = GetInt(e, "max_combo");
            existing.Mods = mods.Count == 0 ? null : string.Join(",", mods);
            await db.SaveChangesAsync(ct);
            return wasNotCounted ? Ingest.NewPass : Ingest.Upgraded;
        }

        return Ingest.Skipped;
    }

    private async Task MarkAsync(SyncJobState state, string? error, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var j = await db.SyncJobs.FindAsync([JobName], ct);
        if (j is null) { j = new SyncJob { Name = JobName }; db.SyncJobs.Add(j); }
        j.State = state;
        j.Error = error;
        j.LastRunAt = DateTimeOffset.UtcNow;
        if (state == SyncJobState.Completed) j.LastSuccessAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
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
