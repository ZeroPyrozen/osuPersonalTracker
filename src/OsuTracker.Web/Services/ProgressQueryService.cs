using Microsoft.EntityFrameworkCore;
using OsuTracker.Web.Data;
using OsuTracker.Web.Data.Entities;

namespace OsuTracker.Web.Services;

public sealed record ModeProgress(
    GameMode Mode, int Total, int Passed, int Attempted, int Untouched)
{
    public double PassedPercent => Total == 0 ? 0 : Passed * 100.0 / Total;
    public double PlayedPercent => Total == 0 ? 0 : (Passed + Attempted) * 100.0 / Total;
}

/// <summary>
/// The single place the counting rules are applied. Every screen goes through here — a
/// page that hand-rolls its own query will sooner or later disagree with the others.
/// </summary>
public sealed class ProgressQueryService(IDbContextFactory<TrackerDbContext> dbFactory)
{
    /// <summary>
    /// Rule 2 (native mode only, since converts are excluded from the catalogue) and
    /// Rule 3 (ranked + approved). Rule 4 is enforced by absence: no query below ever
    /// mentions mods, grade or accuracy.
    /// </summary>
    public static IQueryable<Beatmap> Counted(TrackerDbContext db, GameMode? mode = null)
    {
        var q = db.Beatmaps.Where(b => b.Status == BeatmapStatus.Ranked || b.Status == BeatmapStatus.Approved);
        return mode is null ? q : q.Where(b => b.Mode == mode);
    }

    /// <summary>
    /// Three simple aggregates rather than one clever query. A single grouped join with
    /// correlated subqueries per row does not translate, and even where it does it is far
    /// harder to verify than counting totals, passes and plays separately.
    /// </summary>
    public async Task<List<ModeProgress>> GetAllModesAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var totals = await Counted(db)
            .GroupBy(b => b.Mode)
            .Select(g => new { Mode = g.Key, C = g.Count() })
            .ToDictionaryAsync(x => x.Mode, x => x.C, ct);

        // Rule 5 was already applied when the score was ingested, so this is a plain
        // boolean filter rather than a date comparison across a join.
        var passed = await (
            from b in Counted(db)
            join s in db.Scores on b.Id equals s.BeatmapId
            where s.CountsAsPass
            group b by b.Mode into g
            select new { Mode = g.Key, C = g.Count() })
            .ToDictionaryAsync(x => x.Mode, x => x.C, ct);

        var played = await (
            from b in Counted(db)
            join p in db.PlayCounts on b.Id equals p.BeatmapId
            where p.Count > 0
            group b by b.Mode into g
            select new { Mode = g.Key, C = g.Count() })
            .ToDictionaryAsync(x => x.Mode, x => x.C, ct);

        return Enum.GetValues<GameMode>().Select(m =>
        {
            var total = totals.GetValueOrDefault(m);
            var p = passed.GetValueOrDefault(m);
            var pl = played.GetValueOrDefault(m);

            // A pass implies a play, so attempted is the remainder — but clamp rather
            // than trust it: a score whose beatmap never appeared in most_played would
            // otherwise produce a negative count and a quietly wrong total.
            var attempted = Math.Max(0, pl - p);
            var untouched = Math.Max(0, total - p - attempted);
            return new ModeProgress(m, total, p, attempted, untouched);
        }).ToList();
    }

    public async Task<Dictionary<GameMode, int>> GetDenominatorsAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await Counted(db)
            .GroupBy(b => b.Mode)
            .Select(g => new { Mode = g.Key, C = g.Count() })
            .ToDictionaryAsync(x => x.Mode, x => x.C, ct);
    }

    /// <summary>
    /// Star-rating bands for one mode. Three aggregates again, for the same reason as
    /// GetAllModesAsync: a single grouped query with correlated subqueries per row does
    /// not translate on SQLite.
    /// </summary>
    public async Task<List<StarBand>> GetStarBandsAsync(GameMode mode, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var totals = await Counted(db, mode)
            .GroupBy(b => b.StarRating < 2 ? 0 : b.StarRating < 3 ? 1 : b.StarRating < 4 ? 2 : b.StarRating < 5 ? 3 : b.StarRating < 6 ? 4 : b.StarRating < 7 ? 5 : 6)
            .Select(g => new { Band = g.Key, C = g.Count() })
            .ToDictionaryAsync(x => x.Band, x => x.C, ct);

        var passed = await (from b in Counted(db, mode)
                            join sc in db.Scores on b.Id equals sc.BeatmapId
                            where sc.CountsAsPass
                            group b by b.StarRating < 2 ? 0 : b.StarRating < 3 ? 1 : b.StarRating < 4 ? 2 : b.StarRating < 5 ? 3 : b.StarRating < 6 ? 4 : b.StarRating < 7 ? 5 : 6 into g
                            select new { Band = g.Key, C = g.Count() })
                            .ToDictionaryAsync(x => x.Band, x => x.C, ct);

        var played = await (from b in Counted(db, mode)
                            join p in db.PlayCounts on b.Id equals p.BeatmapId
                            where p.Count > 0
                            group b by b.StarRating < 2 ? 0 : b.StarRating < 3 ? 1 : b.StarRating < 4 ? 2 : b.StarRating < 5 ? 3 : b.StarRating < 6 ? 4 : b.StarRating < 7 ? 5 : 6 into g
                            select new { Band = g.Key, C = g.Count() })
                            .ToDictionaryAsync(x => x.Band, x => x.C, ct);

        return BandLabels.Select((label, i) =>
        {
            var total = totals.GetValueOrDefault(i);
            var p = passed.GetValueOrDefault(i);
            var attempted = Math.Max(0, played.GetValueOrDefault(i) - p);
            return new StarBand(label, total, p, attempted, Math.Max(0, total - p - attempted));
        }).ToList();
    }

    public static readonly string[] BandLabels = ["0–2★", "2–3★", "3–4★", "4–5★", "5–6★", "6–7★", "7★+"];

    // The band expression is repeated literally in each query above rather than
    // factored into a helper: EF cannot translate a method call, and calling one here
    // throws at runtime rather than at compile time. Verbose beats broken.

    /// <summary>Qualifying passes dated within the window — drives the pace projection.</summary>
    public async Task<int> GetRecentPassCountAsync(GameMode mode, int days, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days).ToUnixTimeSeconds();

        return await (from b in Counted(db, mode)
                      join sc in db.Scores on b.Id equals sc.BeatmapId
                      where sc.CountsAsPass && sc.PlayedAtUnix >= cutoff
                      select b.Id).CountAsync(ct);
    }

    /// <summary>Sets with some but not all difficulties passed, closest to done first.</summary>
    public async Task<List<NearlyDoneSet>> GetNearlyDoneAsync(
        GameMode mode, int take = 8, int skip = 0, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var rows = await (from b in Counted(db, mode)
                          join bs in db.Beatmapsets on b.BeatmapsetId equals bs.Id
                          select new
                          {
                              bs.Id,
                              bs.Artist,
                              bs.Title,
                              Passed = db.Scores.Any(sc => sc.BeatmapId == b.Id && sc.CountsAsPass)
                          })
                          .GroupBy(x => new { x.Id, x.Artist, x.Title })
                          .Select(g => new
                          {
                              g.Key.Id,
                              g.Key.Artist,
                              g.Key.Title,
                              Total = g.Count(),
                              Done = g.Count(x => x.Passed)
                          })
                          .Where(x => x.Done > 0 && x.Done < x.Total)
                          .OrderBy(x => x.Total - x.Done)
                          .ThenByDescending(x => x.Done)
                          // Remaining and done alone leave hundreds of rows tied, and SQLite
                          // is free to break a tie differently per query — which, now that
                          // this list pages, would show one set twice and hide another. The
                          // id is what makes "the next 12" mean the next 12.
                          .ThenBy(x => x.Id)
                          .Skip(skip)
                          .Take(take)
                          .ToListAsync(ct);

        return rows.Select(r => new NearlyDoneSet(r.Id, r.Artist, r.Title, r.Total, r.Done)).ToList();
    }

    /// <summary>
    /// How many sets are part-way through, so the list can say what it is a slice of.
    ///
    /// The shape above is repeated here rather than shared. Projecting that grouping into
    /// a named type — the obvious way to hand both methods one query — makes EF inline the
    /// Passed subquery back into the Count and the whole thing stops translating. Same
    /// bargain as the band expression above: verbose beats broken. If the definition of
    /// part-way through moves, it has to move in both places.
    /// </summary>
    public async Task<int> GetNearlyDoneCountAsync(GameMode mode, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        return await (from b in Counted(db, mode)
                      join bs in db.Beatmapsets on b.BeatmapsetId equals bs.Id
                      select new
                      {
                          bs.Id,
                          Passed = db.Scores.Any(sc => sc.BeatmapId == b.Id && sc.CountsAsPass)
                      })
                      .GroupBy(x => x.Id)
                      .Select(g => new { Total = g.Count(), Done = g.Count(x => x.Passed) })
                      .Where(x => x.Done > 0 && x.Done < x.Total)
                      .CountAsync(ct);
    }
}

public sealed record StarBand(string Label, int Total, int Passed, int Attempted, int Untouched);

public sealed record NearlyDoneSet(long BeatmapsetId, string Artist, string Title, int Total, int Done)
{
    public int Remaining => Total - Done;
}
