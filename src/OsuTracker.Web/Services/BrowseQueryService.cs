using Microsoft.EntityFrameworkCore;
using OsuTracker.Web.Data;
using OsuTracker.Web.Data.Entities;

namespace OsuTracker.Web.Services;

public enum BrowseStatus { All, Untouched, Attempted, Passed, PreRankOnly }

public enum BrowseSort
{
    /// <summary>Easiest thing never passed — literally the next map to play.</summary>
    StarsAsc,
    StarsDesc,
    /// <summary>Maps you keep loading and keep not finishing.</summary>
    PlaysDesc,
    RankedDesc,
    RankedAsc
}

public sealed record BrowseFilter
{
    public GameMode Mode { get; init; } = GameMode.Fruits;
    public BrowseStatus Status { get; init; } = BrowseStatus.Untouched;
    public BrowseSort Sort { get; init; } = BrowseSort.StarsAsc;
    public double? MinStars { get; init; }
    public double? MaxStars { get; init; }
    public string? Search { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; } = 50;
}

public sealed record BrowseRow(
    long BeatmapId, long BeatmapsetId, string Artist, string Title, string Creator,
    string Difficulty, double StarRating, int TotalLength,
    BrowseStatus State, string? Grade, double? Accuracy, string? Mods,
    int PlayCount, DateTimeOffset? RankedDate, DateTimeOffset? PassedAt);

public sealed record BrowseResult(List<BrowseRow> Rows, int Total, int Page, int PageSize)
{
    public int PageCount => PageSize == 0 ? 1 : Math.Max(1, (int)Math.Ceiling(Total / (double)PageSize));
}

public sealed class BrowseQueryService(IDbContextFactory<TrackerDbContext> dbFactory)
{
    public async Task<BrowseResult> QueryAsync(BrowseFilter f, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        // Shape once, then filter — the projection carries everything the status
        // predicates need so they never have to re-join.
        var q =
            from b in ProgressQueryService.Counted(db, f.Mode)
            join bs in db.Beatmapsets on b.BeatmapsetId equals bs.Id
            join sc in db.Scores on b.Id equals sc.BeatmapId into scores
            from s in scores.DefaultIfEmpty()
            join pc in db.PlayCounts on b.Id equals pc.BeatmapId into plays
            from p in plays.DefaultIfEmpty()
            select new
            {
                b.Id, b.BeatmapsetId, bs.Artist, bs.Title, bs.Creator,
                Difficulty = b.DifficultyName, b.StarRating, b.TotalLength,
                bs.RankedDate,
                HasScore = s != null,
                Passed = s != null && s.CountsAsPass,
                // Every score column needs an explicit null guard. PlayedAt and Accuracy
                // are non-nullable on the entity, so on a left-join miss EF materialises
                // them straight into a value type and throws "Nullable object must have
                // a value" rather than yielding null.
                Grade = s == null ? null : s.Grade,
                Accuracy = s == null ? (double?)null : s.Accuracy,
                Mods = s == null ? null : s.Mods,
                PassedAt = s == null ? (DateTimeOffset?)null : s.PlayedAt,
                Plays = p == null ? 0 : p.Count
            };

        q = f.Status switch
        {
            BrowseStatus.Passed => q.Where(x => x.Passed),
            BrowseStatus.PreRankOnly => q.Where(x => x.HasScore && !x.Passed),
            BrowseStatus.Attempted => q.Where(x => !x.Passed && x.Plays > 0),
            BrowseStatus.Untouched => q.Where(x => !x.Passed && x.Plays == 0),
            _ => q
        };

        if (f.MinStars is not null) q = q.Where(x => x.StarRating >= f.MinStars);
        if (f.MaxStars is not null) q = q.Where(x => x.StarRating < f.MaxStars);

        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            var term = f.Search.Trim();
            q = q.Where(x => EF.Functions.Like(x.Title, $"%{term}%")
                          || EF.Functions.Like(x.Artist, $"%{term}%")
                          || EF.Functions.Like(x.Creator, $"%{term}%")
                          || EF.Functions.Like(x.Difficulty, $"%{term}%"));
        }

        var total = await q.CountAsync(ct);

        q = f.Sort switch
        {
            BrowseSort.StarsAsc => q.OrderBy(x => x.StarRating).ThenBy(x => x.Id),
            BrowseSort.StarsDesc => q.OrderByDescending(x => x.StarRating).ThenBy(x => x.Id),
            BrowseSort.PlaysDesc => q.OrderByDescending(x => x.Plays).ThenBy(x => x.Id),
            BrowseSort.RankedDesc => q.OrderByDescending(x => x.RankedDate).ThenBy(x => x.Id),
            BrowseSort.RankedAsc => q.OrderBy(x => x.RankedDate).ThenBy(x => x.Id),
            _ => q.OrderBy(x => x.StarRating).ThenBy(x => x.Id)
        };

        var page = Math.Max(0, f.Page);
        var rows = await q.Skip(page * f.PageSize).Take(f.PageSize).ToListAsync(ct);

        return new BrowseResult(
            rows.Select(x => new BrowseRow(
                x.Id, x.BeatmapsetId, x.Artist, x.Title, x.Creator, x.Difficulty,
                x.StarRating, x.TotalLength,
                x.Passed ? BrowseStatus.Passed
                    : x.HasScore ? BrowseStatus.PreRankOnly
                    : x.Plays > 0 ? BrowseStatus.Attempted
                    : BrowseStatus.Untouched,
                x.Grade, x.Accuracy, x.Mods, x.Plays, x.RankedDate, x.PassedAt))
                .ToList(),
            total, page, f.PageSize);
    }

    /// <summary>Counts for the status filter chips, so each shows its own size.</summary>
    public async Task<Dictionary<BrowseStatus, int>> GetStatusCountsAsync(GameMode mode, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var total = await ProgressQueryService.Counted(db, mode).CountAsync(ct);

        var passed = await (from b in ProgressQueryService.Counted(db, mode)
                            join s in db.Scores on b.Id equals s.BeatmapId
                            where s.CountsAsPass
                            select b.Id).CountAsync(ct);

        var preRank = await (from b in ProgressQueryService.Counted(db, mode)
                             join s in db.Scores on b.Id equals s.BeatmapId
                             where !s.CountsAsPass
                             select b.Id).CountAsync(ct);

        var played = await (from b in ProgressQueryService.Counted(db, mode)
                            join p in db.PlayCounts on b.Id equals p.BeatmapId
                            where p.Count > 0
                            select b.Id).CountAsync(ct);

        var attempted = Math.Max(0, played - passed);

        return new Dictionary<BrowseStatus, int>
        {
            [BrowseStatus.All] = total,
            [BrowseStatus.Passed] = passed,
            [BrowseStatus.Attempted] = attempted,
            [BrowseStatus.PreRankOnly] = preRank,
            [BrowseStatus.Untouched] = Math.Max(0, total - passed - attempted)
        };
    }
}
