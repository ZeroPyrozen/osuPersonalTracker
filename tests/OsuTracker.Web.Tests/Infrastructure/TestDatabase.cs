using Microsoft.EntityFrameworkCore;
using OsuTracker.Web.Data;
using OsuTracker.Web.Data.Entities;

namespace OsuTracker.Web.Tests.Infrastructure;

/// <summary>
/// A real SQLite file per test, built by the app's own migrations. Every test therefore
/// also exercises the schema, and no query is ever checked against a provider that
/// translates LINQ differently from the one the app ships with.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _dir;

    private TestDatabase(string dir, DbContextOptions<TrackerDbContext> options)
    {
        _dir = dir;
        Options = options;
        Factory = new SimpleFactory(options);
    }

    public DbContextOptions<TrackerDbContext> Options { get; }

    public IDbContextFactory<TrackerDbContext> Factory { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "osutracker-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);

        var options = new DbContextOptionsBuilder<TrackerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(dir, "tracker.db")};Pooling=False")
            .Options;

        await using (var db = new TrackerDbContext(options))
            await db.Database.MigrateAsync();

        return new TestDatabase(dir, options);
    }

    public TrackerDbContext Context() => Factory.CreateDbContext();

    public async Task SeedAsync(Action<TrackerDbContext> seed)
    {
        await using var db = Context();
        seed(db);
        await db.SaveChangesAsync();
    }

    public async Task<SyncJob?> JobAsync(string name)
    {
        await using var db = Context();
        return await db.SyncJobs.AsNoTracking().SingleOrDefaultAsync(j => j.Name == name);
    }

    public ValueTask DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return ValueTask.CompletedTask;
    }

    private sealed class SimpleFactory(DbContextOptions<TrackerDbContext> options) : IDbContextFactory<TrackerDbContext>
    {
        public TrackerDbContext CreateDbContext() => new(options);
    }
}

/// <summary>Terse builders for the rows tests need, defaulting to a counted catch map.</summary>
public static class Seed
{
    public static readonly DateTimeOffset RankedLongAgo = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static Beatmapset Set(long id, DateTimeOffset? ranked = null,
        BeatmapStatus status = BeatmapStatus.Ranked, string title = "Title", string artist = "Artist") => new()
    {
        Id = id,
        Artist = artist,
        Title = title,
        Creator = "Mapper",
        Status = status,
        RankedDate = ranked ?? RankedLongAgo,
        SeenInRunAt = RankedLongAgo
    };

    public static Beatmap Map(long id, long setId, GameMode mode = GameMode.Fruits, double stars = 3,
        BeatmapStatus status = BeatmapStatus.Ranked) => new()
    {
        Id = id,
        BeatmapsetId = setId,
        Mode = mode,
        DifficultyName = $"diff {id}",
        StarRating = stars,
        TotalLength = 120,
        Status = status,
        SeenInRunAt = RankedLongAgo
    };

    public static PlayCount Plays(long mapId, int count = 3) => new()
    {
        BeatmapId = mapId,
        Count = count,
        LastSeenAt = RankedLongAgo
    };

    public static Score Score(long mapId, DateTimeOffset playedAt, bool countsAsPass = true,
        double accuracy = 0.95, bool isLazer = false) => new()
    {
        BeatmapId = mapId,
        ScoreId = mapId * 10,
        PlayedAt = playedAt,
        PlayedAtUnix = playedAt.ToUnixTimeSeconds(),
        FetchedAt = playedAt,
        CountsAsPass = countsAsPass,
        Grade = "A",
        Accuracy = accuracy,
        IsLazer = isLazer
    };
}
