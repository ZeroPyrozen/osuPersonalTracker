using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OsuTracker.Web.Data;
using OsuTracker.Web.Data.Entities;
using OsuTracker.Web.OsuApi;

namespace OsuTracker.Web.Sync;

/// <summary>
/// Walks the ranked and approved catalogue and writes every native difficulty.
/// Search results embed the full beatmaps[] array, so one pass over ~1,100 pages
/// captures all four modes — no per-set request needed.
/// </summary>
public sealed class CatalogSyncJob(
    OsuApiClient api,
    IDbContextFactory<TrackerDbContext> dbFactory,
    ILogger<CatalogSyncJob> log)
{
    public const string JobName = "CatalogSync";

    /// <summary>
    /// One sweep, not two. Verified against the live API: `s=ranked` already returns
    /// Approved sets (e.g. set 39804 comes back with ranked=2), so it covers all of
    /// Rule 3 by itself. There is no `s=approved` filter — passing one is silently
    /// ignored and yields a byte-identical duplicate of the ranked sweep.
    /// Actual status is read per set from the `ranked` int, never assumed from the query.
    /// </summary>
    private static readonly (string Query, BeatmapStatus Status)[] Sweeps =
    [
        ("ranked", BeatmapStatus.Ranked)
    ];

    public async Task RunAsync(bool resume, CancellationToken ct, int? maxPages = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var job = await db.SyncJobs.FindAsync([JobName], ct);
        if (job is null)
        {
            job = new SyncJob { Name = JobName };
            db.SyncJobs.Add(job);
        }

        // The reconciliation watermark. On a fresh run this is "now"; on a resume we
        // keep the original watermark so rows written before the crash still count as seen.
        var runStart = resume && job.RunStartedAt is not null && job.State != SyncJobState.Completed
            ? job.RunStartedAt.Value
            : DateTimeOffset.UtcNow;

        var startCursor = resume ? job.Cursor : null;

        job.State = SyncJobState.Running;
        job.RunStartedAt = runStart;
        job.LastRunAt = DateTimeOffset.UtcNow;
        job.Error = null;
        if (!resume) { job.ItemsDone = 0; job.Cursor = null; }
        await db.SaveChangesAsync(ct);

        log.LogInformation("Catalog sync starting (resume={Resume}, watermark={Watermark:u})", resume, runStart);

        var totalSets = job.ItemsDone;
        var totalMaps = 0;

        try
        {
            foreach (var (query, _) in Sweeps)
            {
                // A resume cursor belongs to whichever sweep was in flight; encode the
                // sweep name into it so we do not replay the wrong one.
                string? cursor = null;
                if (startCursor is not null)
                {
                    var parts = startCursor.Split('|', 2);
                    if (parts.Length == 2 && parts[0] == query) cursor = parts[1];
                    else if (parts.Length == 2 && parts[0] != query && Array.FindIndex(Sweeps, s => s.Query == parts[0]) > Array.FindIndex(Sweeps, s => s.Query == query))
                        continue; // this sweep already finished before the crash
                    startCursor = null;
                }

                var page = 0;
                while (!ct.IsCancellationRequested)
                {
                    var url = $"/beatmapsets/search?s={query}" + (cursor is null ? "" : $"&cursor_string={Uri.EscapeDataString(cursor)}");
                    using var doc = await api.GetAsync(url, ct);
                    if (doc is null) break;

                    var root = doc.RootElement;
                    if (!root.TryGetProperty("beatmapsets", out var sets) || sets.ValueKind != JsonValueKind.Array) break;
                    var count = sets.GetArrayLength();
                    if (count == 0) break;

                    var (sCount, mCount) = await UpsertPageAsync(sets, runStart, ct);
                    totalSets += sCount;
                    totalMaps += mCount;
                    page++;

                    cursor = root.TryGetProperty("cursor_string", out var cs) && cs.ValueKind == JsonValueKind.String
                        ? cs.GetString()
                        : null;

                    // Checkpoint after every page. This is what makes the job survivable.
                    await using (var jdb = await dbFactory.CreateDbContextAsync(ct))
                    {
                        var j = await jdb.SyncJobs.FindAsync([JobName], ct);
                        if (j is not null)
                        {
                            j.Cursor = cursor is null ? null : $"{query}|{cursor}";
                            j.ItemsDone = totalSets;
                            await jdb.SaveChangesAsync(ct);
                        }
                    }

                    if (page % 20 == 0)
                        log.LogInformation("  {Query}: {Pages} pages, {Sets} sets, {Maps} difficulties", query, page, totalSets, totalMaps);

                    if (cursor is null) break; // end of this sweep
                    if (maxPages is not null && page >= maxPages) { log.LogWarning("Stopping early at {Pages} pages (--max-pages)", page); break; }
                }

                log.LogInformation("Sweep '{Query}' complete: {Pages} pages", query, page);
            }

            if (ct.IsCancellationRequested)
            {
                await MarkAsync(SyncJobState.Paused, null, ct);
                log.LogWarning("Catalog sync cancelled — cursor preserved, rerun with --resume");
                return;
            }

            // Only reconcile after a sweep that actually finished. Running this on a
            // partial run would mark most of the catalogue as departed.
            var departed = await ReconcileAsync(runStart, ct);
            await MarkAsync(SyncJobState.Completed, null, ct);

            log.LogInformation("Catalog sync complete: {Sets} sets, {Maps} difficulties, {Departed} departed",
                totalSets, totalMaps, departed);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Catalog sync failed");
            await MarkAsync(SyncJobState.Failed, ex.Message, ct);
            throw;
        }
    }

    private async Task<(int Sets, int Maps)> UpsertPageAsync(JsonElement sets, DateTimeOffset runStart, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var ids = new List<long>();
        var parsed = new List<(Beatmapset Set, List<Beatmap> Maps)>();

        foreach (var s in sets.EnumerateArray())
        {
            var setId = GetLong(s, "id");
            if (setId is null) continue;

            var set = new Beatmapset
            {
                Id = setId.Value,
                Artist = GetString(s, "artist") ?? "",
                Title = GetString(s, "title") ?? "",
                Creator = GetString(s, "creator") ?? "",
                CreatorUserId = GetLong(s, "user_id") ?? 0,
                Status = StatusRules.Parse(GetInt(s, "ranked") ?? 0),
                RankedDate = GetDate(s, "ranked_date"),
                SubmittedDate = GetDate(s, "submitted_date"),
                Bpm = GetDouble(s, "bpm") ?? 0,
                CoverUrl = s.TryGetProperty("covers", out var c) ? GetString(c, "cover") : null,
                SeenInRunAt = runStart
            };

            var maps = new List<Beatmap>();
            if (s.TryGetProperty("beatmaps", out var bms) && bms.ValueKind == JsonValueKind.Array)
            {
                foreach (var b in bms.EnumerateArray())
                {
                    var mapId = GetLong(b, "id");
                    if (mapId is null) continue;

                    // Rule 2: the set's own beatmaps[] are all native. Skip anything
                    // flagged as a convert defensively, in case the API ever includes them.
                    if (b.TryGetProperty("convert", out var cv) && cv.ValueKind == JsonValueKind.True) continue;

                    var modeInt = GetInt(b, "mode_int");
                    if (modeInt is null or < 0 or > 3) continue;

                    maps.Add(new Beatmap
                    {
                        Id = mapId.Value,
                        BeatmapsetId = setId.Value,
                        Mode = (GameMode)modeInt.Value,
                        DifficultyName = GetString(b, "version") ?? "",
                        StarRating = GetDouble(b, "difficulty_rating") ?? 0,
                        TotalLength = GetInt(b, "total_length") ?? 0,
                        MaxCombo = GetInt(b, "max_combo"),
                        Cs = GetDouble(b, "cs") ?? 0,
                        Ar = GetDouble(b, "ar") ?? 0,
                        Od = GetDouble(b, "accuracy") ?? 0,
                        Hp = GetDouble(b, "drain") ?? 0,
                        Status = StatusRules.Parse(GetInt(b, "ranked") ?? 0),
                        SeenInRunAt = runStart
                    });
                }
            }

            ids.Add(setId.Value);
            parsed.Add((set, maps));
        }

        var existingSets = await db.Beatmapsets.Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var mapIds = parsed.SelectMany(p => p.Maps.Select(m => m.Id)).ToList();
        var existingMaps = await db.Beatmaps.Where(x => mapIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);

        var mapCount = 0;

        foreach (var (set, maps) in parsed)
        {
            if (existingSets.TryGetValue(set.Id, out var dbSet))
            {
                dbSet.Artist = set.Artist; dbSet.Title = set.Title;
                dbSet.Creator = set.Creator; dbSet.CreatorUserId = set.CreatorUserId;
                dbSet.Status = set.Status; dbSet.RankedDate = set.RankedDate;
                dbSet.SubmittedDate = set.SubmittedDate; dbSet.Bpm = set.Bpm;
                dbSet.CoverUrl = set.CoverUrl; dbSet.SeenInRunAt = runStart;
            }
            else db.Beatmapsets.Add(set);

            foreach (var m in maps)
            {
                mapCount++;
                if (existingMaps.TryGetValue(m.Id, out var dbMap))
                {
                    dbMap.BeatmapsetId = m.BeatmapsetId; dbMap.Mode = m.Mode;
                    dbMap.DifficultyName = m.DifficultyName; dbMap.StarRating = m.StarRating;
                    dbMap.TotalLength = m.TotalLength; dbMap.MaxCombo = m.MaxCombo;
                    dbMap.Cs = m.Cs; dbMap.Ar = m.Ar; dbMap.Od = m.Od; dbMap.Hp = m.Hp;
                    dbMap.Status = m.Status; dbMap.SeenInRunAt = runStart;
                }
                else db.Beatmaps.Add(m);
            }
        }

        await db.SaveChangesAsync(ct);
        return (parsed.Count, mapCount);
    }

    /// <summary>
    /// Anything still counted but not seen this run has left the catalogue. Mark it
    /// Departed — never delete the row, and never touch its Score.
    /// </summary>
    private async Task<int> ReconcileAsync(DateTimeOffset runStart, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Raw SQL rather than ExecuteUpdate: SetProperty on an enum carrying a value
        // converter does not translate, and this UPDATE is clearer written out anyway.
        const int departed = (int)BeatmapStatus.Departed;

        var departedMaps = await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE Beatmaps SET Status = {departed}
             WHERE Status IN (1, 2) AND SeenInRunAt < {runStart}
             """, ct);

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
             UPDATE Beatmapsets SET Status = {departed}
             WHERE Status IN (1, 2) AND SeenInRunAt < {runStart}
             """, ct);

        if (departedMaps > 0)
            log.LogWarning("{Count} difficulties left the catalogue and were marked Departed", departedMaps);

        return departedMaps;
    }

    private async Task MarkAsync(SyncJobState state, string? error, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var j = await db.SyncJobs.FindAsync([JobName], ct);
        if (j is null) return;
        j.State = state;
        j.Error = error;
        if (state == SyncJobState.Completed)
        {
            j.LastSuccessAt = DateTimeOffset.UtcNow;
            j.Cursor = null;
        }
        await db.SaveChangesAsync(ct);
    }

    // ---- JSON helpers ----
    private static string? GetString(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? GetLong(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    private static int? GetInt(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static double? GetDouble(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static DateTimeOffset? GetDate(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(v.GetString(), out var d) ? d : null;
}
