using OsuTracker.Web.Data.Entities;
using OsuTracker.Web.Services;
using OsuTracker.Web.Tests.Infrastructure;

namespace OsuTracker.Web.Tests.Services;

/// <summary>
/// One catch set of five maps, one of each kind the ladder has to tell apart:
///   1  passed, and in most_played
///   2  played, never passed
///   3  passed by `sync recent` on a map never loaded before — so no playcount row yet
///   4  played, with a pre-rank score only
///   5  never touched
/// Truth: passed {1,3}, attempted {2,4}, untouched {5}, pre-rank {4}.
/// </summary>
public sealed class ProgressQueryServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<TestDatabase> LadderAsync()
    {
        var db = await TestDatabase.CreateAsync();
        var now = DateTimeOffset.UtcNow;
        await db.SeedAsync(x =>
        {
            x.Beatmapsets.Add(Seed.Set(1));
            // Maps 2 and 3 share a star band, so a band can be wrong in both directions at once.
            x.Beatmaps.Add(Seed.Map(1, 1, stars: 1.5));
            x.Beatmaps.Add(Seed.Map(2, 1, stars: 2.5));
            x.Beatmaps.Add(Seed.Map(3, 1, stars: 2.6));
            x.Beatmaps.Add(Seed.Map(4, 1, stars: 3.5));
            x.Beatmaps.Add(Seed.Map(5, 1, stars: 4.5));

            x.PlayCounts.Add(Seed.Plays(1));
            x.PlayCounts.Add(Seed.Plays(2));
            x.PlayCounts.Add(Seed.Plays(4));

            x.Scores.Add(Seed.Score(1, now.AddDays(-100)));
            x.Scores.Add(Seed.Score(3, now.AddDays(-1)));
            x.Scores.Add(Seed.Score(4, Seed.RankedLongAgo.AddDays(-5), countsAsPass: false));
        });
        return db;
    }

    [Fact]
    public async Task The_ladder_counts_a_pass_without_a_playcount_as_passed_and_nothing_else()
    {
        await using var db = await LadderAsync();

        var row = (await new ProgressQueryService(db.Factory).GetAllModesAsync(Ct)).Single(r => r.Mode == GameMode.Fruits);

        Assert.Equal((5, 2, 2, 1), (row.Total, row.Passed, row.Attempted, row.Untouched));
    }

    [Fact]
    public async Task Star_bands_split_the_same_way_as_the_ladder()
    {
        await using var db = await LadderAsync();

        var bands = await new ProgressQueryService(db.Factory).GetStarBandsAsync(GameMode.Fruits, Ct);

        // 2–3★ holds map 2 (attempted) and map 3 (passed, no playcount).
        var band = bands.Single(b => b.Label == "2–3★");
        Assert.Equal((2, 1, 1, 0), (band.Total, band.Passed, band.Attempted, band.Untouched));
        Assert.All(bands, b => Assert.Equal(b.Total, b.Passed + b.Attempted + b.Untouched));
    }

    [Fact]
    public async Task Every_rung_adds_back_up_to_the_total_in_every_mode()
    {
        await using var db = await LadderAsync();

        var rows = await new ProgressQueryService(db.Factory).GetAllModesAsync(Ct);

        Assert.All(rows, r => Assert.Equal(r.Total, r.Passed + r.Attempted + r.Untouched));
    }
}
