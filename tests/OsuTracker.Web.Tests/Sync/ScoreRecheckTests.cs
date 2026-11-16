using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OsuTracker.Web.Data.Entities;
using OsuTracker.Web.Sync;
using OsuTracker.Web.Tests.Infrastructure;

namespace OsuTracker.Web.Tests.Sync;

/// <summary>
/// The score job as a safety net: it has to be cheap enough to run daily, and it has to
/// come back to a map that was played again without a counted pass on file.
/// </summary>
public sealed class ScoreRecheckTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScoreBackfillJob Job(OsuApiHarness osu, TestDatabase db) =>
        new(osu.Api, db.Factory, NullLogger<ScoreBackfillJob>.Instance);

    private static Task RunAsync(OsuApiHarness osu, TestDatabase db) =>
        Job(osu, db).RunAsync(resume: false, onlyMode: null, limit: null, Ct);

    private static async Task<TestDatabase> OnePlayedMapAsync(int plays = 3)
    {
        var db = await TestDatabase.CreateAsync();
        await db.SeedAsync(x =>
        {
            x.Beatmapsets.Add(Seed.Set(1));
            x.Beatmaps.Add(Seed.Map(1, 1));
            x.PlayCounts.Add(Seed.Plays(1, plays));
        });
        return db;
    }

    private static async Task PlayAgainAsync(TestDatabase db, int plays)
    {
        await using var x = db.Context();
        (await x.PlayCounts.SingleAsync(Ct)).Count = plays;
        await x.SaveChangesAsync(Ct);
    }

    private static HttpResponseMessage NoScores(HttpRequestMessage _) => FakeOsuServer.Status(HttpStatusCode.NotFound);

    private static HttpResponseMessage Pass(DateTimeOffset at) =>
        FakeOsuServer.Json(OsuJson.AllScores(OsuJson.Score(100, 1, at)));

    [Fact]
    public async Task A_map_with_no_scores_is_not_asked_about_again_until_it_is_played_again()
    {
        await using var db = await OnePlayedMapAsync();
        using var osu = new OsuApiHarness(NoScores);

        await RunAsync(osu, db);
        var afterFirst = osu.Server.ApiRequests.Count;
        await RunAsync(osu, db);

        Assert.True(afterFirst > 0);
        Assert.Equal(afterFirst, osu.Server.ApiRequests.Count);
    }

    [Fact]
    public async Task A_map_played_again_since_its_last_check_is_fetched_and_its_pass_recorded()
    {
        await using var db = await OnePlayedMapAsync(plays: 3);
        var passed = false;
        using var osu = new OsuApiHarness(r => passed ? Pass(DateTimeOffset.UtcNow.AddHours(-30)) : NoScores(r));

        await RunAsync(osu, db);
        await using (var x = db.Context()) Assert.Empty(await x.Scores.ToListAsync(Ct));

        // A pass the 24-hour poll never saw: the host was off, or the poll failed.
        passed = true;
        await PlayAgainAsync(db, plays: 4);
        await RunAsync(osu, db);

        await using var y = db.Context();
        Assert.True((await y.Scores.SingleAsync(Ct)).CountsAsPass);
        Assert.Equal(4, (await y.PlayCounts.SingleAsync(Ct)).CheckedCount);
    }

    [Fact]
    public async Task A_pre_rank_only_row_is_promoted_when_a_later_check_finds_a_real_pass()
    {
        await using var db = await OnePlayedMapAsync(plays: 3);
        var ranked = Seed.RankedLongAgo;
        var later = false;
        using var osu = new OsuApiHarness(_ => later
            ? FakeOsuServer.Json(OsuJson.AllScores(
                OsuJson.Score(100, 1, ranked.AddDays(-5), accuracy: 0.99),
                OsuJson.Score(101, 1, ranked.AddDays(50), accuracy: 0.80)))
            : Pass(ranked.AddDays(-5)));

        await RunAsync(osu, db);
        await using (var x = db.Context()) Assert.False((await x.Scores.SingleAsync(Ct)).CountsAsPass);

        later = true;
        await PlayAgainAsync(db, plays: 5);
        await RunAsync(osu, db);

        await using var y = db.Context();
        var row = await y.Scores.SingleAsync(Ct);
        Assert.True(row.CountsAsPass);
        Assert.Equal(101L, row.ScoreId);
        Assert.Equal(ranked.AddDays(50).ToUnixTimeSeconds(), row.PlayedAtUnix);
    }

    [Fact]
    public async Task A_map_with_a_counted_pass_is_never_fetched_however_often_it_is_replayed()
    {
        await using var db = await OnePlayedMapAsync(plays: 3);
        await db.SeedAsync(x => x.Scores.Add(Seed.Score(1, DateTimeOffset.UtcNow.AddDays(-100))));
        using var osu = new OsuApiHarness(NoScores);

        await PlayAgainAsync(db, plays: 40);
        await RunAsync(osu, db);

        Assert.Empty(osu.Server.ApiRequests);
    }

    [Fact]
    public async Task A_failed_fetch_leaves_the_map_unchecked_so_the_next_run_retries_it()
    {
        await using var db = await OnePlayedMapAsync();
        var down = true;
        using var osu = new OsuApiHarness(r => down ? FakeOsuServer.Status(HttpStatusCode.Forbidden) : NoScores(r));

        await RunAsync(osu, db);
        await using (var x = db.Context()) Assert.Null((await x.PlayCounts.SingleAsync(Ct)).CheckedCount);

        down = false;
        var before = osu.Server.ApiRequests.Count;
        await RunAsync(osu, db);

        Assert.True(osu.Server.ApiRequests.Count > before);
        await using var y = db.Context();
        Assert.Equal(3, (await y.PlayCounts.SingleAsync(Ct)).CheckedCount);
    }
}
