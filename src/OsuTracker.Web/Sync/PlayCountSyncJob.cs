using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OsuTracker.Web.Data;
using OsuTracker.Web.Data.Entities;
using OsuTracker.Web.OsuApi;

namespace OsuTracker.Web.Sync;

/// <summary>
/// Pages most_played to learn every beatmap this account has ever loaded. Cheap — the
/// Phase 1 census covered 17,412 maps in 175 requests — and it is what defines the
/// backfill queue: anything absent from here is Untouched by definition.
/// </summary>
public sealed class PlayCountSyncJob(
    OsuApiClient api,
    IDbContextFactory<TrackerDbContext> dbFactory,
    ILogger<PlayCountSyncJob> log)
{
    public const string JobName = "PlayCountSync";
    private const int PageSize = 100;

    public async Task RunAsync(bool resume, CancellationToken ct)
    {
        await using (var seed = await dbFactory.CreateDbContextAsync(ct))
        {
            var j = await seed.SyncJobs.FindAsync([JobName], ct);
            if (j is null) { j = new SyncJob { Name = JobName }; seed.SyncJobs.Add(j); }
            j.State = SyncJobState.Running;
            j.LastRunAt = DateTimeOffset.UtcNow;
            j.RunStartedAt = DateTimeOffset.UtcNow;
            j.Error = null;
            if (!resume) { j.ItemsDone = 0; j.Cursor = null; }
            await seed.SaveChangesAsync(ct);
        }

        var offset = 0;
        if (resume)
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var j = await db.SyncJobs.FindAsync([JobName], ct);
            if (j?.Cursor is not null && int.TryParse(j.Cursor, out var stored)) offset = stored;
        }

        var seen = offset;
        var matched = 0;
        var orphaned = 0;
        var now = DateTimeOffset.UtcNow;

        log.LogInformation("Playcount sync starting at offset {Offset}", offset);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var doc = await api.GetAsync(
                    $"/users/{api.UserId}/beatmapsets/most_played?limit={PageSize}&offset={offset}", ct);

                if (doc is null) break;
                var arr = doc.RootElement;
                if (arr.ValueKind != JsonValueKind.Array) break;
                var n = arr.GetArrayLength();
                if (n == 0) break;

                var rows = new List<(long BeatmapId, int Count)>(n);
                foreach (var row in arr.EnumerateArray())
                {
                    var id = GetLong(row, "beatmap_id")
                             ?? (row.TryGetProperty("beatmap", out var bm) ? GetLong(bm, "id") : null);
                    if (id is null) continue;
                    rows.Add((id.Value, GetInt(row, "count") ?? 0));
                }

                var (m, o) = await UpsertAsync(rows, now, ct);
                matched += m;
                orphaned += o;
                seen += n;
                offset += n;

                await using (var jdb = await dbFactory.CreateDbContextAsync(ct))
                {
                    var j = await jdb.SyncJobs.FindAsync([JobName], ct);
                    if (j is not null) { j.Cursor = offset.ToString(); j.ItemsDone = matched; await jdb.SaveChangesAsync(ct); }
                }

                if (offset % 1000 == 0)
                    log.LogInformation("  {Seen} rows seen, {Matched} matched to catalogue", seen, matched);

                if (n < PageSize) break;
            }

            if (ct.IsCancellationRequested)
            {
                await MarkAsync(SyncJobState.Paused, null, ct);
                log.LogWarning("Playcount sync cancelled at offset {Offset} — rerun with --resume", offset);
                return;
            }

            await MarkAsync(SyncJobState.Completed, null, ct);
            log.LogInformation(
                "Playcount sync complete: {Seen} played maps, {Matched} in catalogue, {Orphaned} outside it",
                seen, matched, orphaned);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Playcount sync failed");
            await MarkAsync(SyncJobState.Failed, ex.Message, ct);
            throw;
        }
    }

    /// <summary>
    /// Only playcounts for beatmaps we actually hold are stored — a play on a graveyard
    /// or loved map is real, but it has no denominator to belong to, so keeping it would
    /// create a row that can never be joined to anything.
    /// </summary>
    private async Task<(int Matched, int Orphaned)> UpsertAsync(
        List<(long BeatmapId, int Count)> rows, DateTimeOffset now, CancellationToken ct)
    {
        if (rows.Count == 0) return (0, 0);

        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var ids = rows.Select(r => r.BeatmapId).ToList();
        var known = await db.Beatmaps.Where(b => ids.Contains(b.Id)).Select(b => b.Id).ToListAsync(ct);
        var knownSet = known.ToHashSet();
        var existing = await db.PlayCounts.Where(p => ids.Contains(p.BeatmapId)).ToDictionaryAsync(p => p.BeatmapId, ct);

        var matched = 0;
        foreach (var (beatmapId, count) in rows)
        {
            if (!knownSet.Contains(beatmapId)) continue;
            matched++;

            if (existing.TryGetValue(beatmapId, out var pc))
            {
                pc.Count = count;
                pc.LastSeenAt = now;
            }
            else
            {
                db.PlayCounts.Add(new PlayCount { BeatmapId = beatmapId, Count = count, LastSeenAt = now });
            }
        }

        await db.SaveChangesAsync(ct);
        return (matched, rows.Count - matched);
    }

    private async Task MarkAsync(SyncJobState state, string? error, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var j = await db.SyncJobs.FindAsync([JobName], ct);
        if (j is null) return;
        j.State = state;
        j.Error = error;
        if (state == SyncJobState.Completed) { j.LastSuccessAt = DateTimeOffset.UtcNow; j.Cursor = null; }
        await db.SaveChangesAsync(ct);
    }

    private static long? GetLong(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    private static int? GetInt(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}
