using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OsuTracker.Web.Data.Entities;
using OsuTracker.Web.Services;
using OsuTracker.Web.Sync;
using OsuTracker.Web.Tests.Infrastructure;

namespace OsuTracker.Web.Tests.Sync;

public sealed class RecentScoresJobTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RecentScoresJob Job(OsuApiHarness osu, TestDatabase db) =>
        new(osu.Api, db.Factory, NullLogger<RecentScoresJob>.Instance);

    /// <summary>
    /// /users/{id}/scores/recent as osu-web serves it (UsersController::scores and
    /// Solo\Score::scopeRecent): newest first, failed plays only with include_fails=1,
    /// limit clamped to 1..100, and never more than 100 rows in total however it is paged.
    /// </summary>
    private static HttpResponseMessage OsuWebRecent(HttpRequestMessage r, string mode, IReadOnlyList<Dictionary<string, object?>> plays)
    {
        var q = HttpUtility.ParseQueryString(r.RequestUri!.Query);
        if (q["mode"] != mode) return FakeOsuServer.Json("[]");

        var includeFails = q["include_fails"] is "1" or "true";
        var limit = Math.Clamp(int.TryParse(q["limit"], out var l) ? l : 5, 1, 100);
        var offset = Math.Max(0, int.TryParse(q["offset"], out var o) ? o : 0);
        var perPage = offset >= 100 ? 0 : Math.Min(limit, 100 - offset);

        var rows = plays
            .Where(p => includeFails || (bool)p["passed"]!)
            .OrderByDescending(p => (string)p["ended_at"]!)
            .Skip(offset).Take(perPage)
            .ToArray();
        return FakeOsuServer.Json(OsuJson.Array(rows));
    }

    /// <summary>One osu! set ranked long ago, maps 10..14.</summary>
    private static async Task<TestDatabase> OsuMapsAsync(Action<OsuTracker.Web.Data.TrackerDbContext>? extra = null)
    {
        var db = await TestDatabase.CreateAsync();
        await db.SeedAsync(x =>
        {
            x.Beatmapsets.Add(Seed.Set(1));
            for (var i = 10; i <= 14; i++) x.Beatmaps.Add(Seed.Map(i, 1, GameMode.Osu));
            extra?.Invoke(x);
        });
        return db;
    }

    private static Task<int> RecentPassesAsync(TestDatabase db) =>
        new ProgressQueryService(db.Factory).GetRecentPassCountAsync(GameMode.Osu, 30, Ct);

    [Fact]
    public async Task A_pass_behind_a_hundred_newer_fails_is_still_recorded()
    {
        await using var db = await OsuMapsAsync();
        var now = DateTimeOffset.UtcNow;
        var plays = Enumerable.Range(0, 150)
            .Select(i => OsuJson.Score(9000 + i, 11, now.AddMinutes(-i - 1), accuracy: 0.6, passed: false))
            .Append(OsuJson.Score(500, 10, now.AddHours(-3)))
            .ToList();
        using var osu = new OsuApiHarness(r => OsuWebRecent(r, "osu", plays));

        await Job(osu, db).RunAsync(Ct);

        await using var x = db.Context();
        Assert.Equal(10L, (await x.Scores.SingleAsync(Ct)).BeatmapId);
    }

    [Fact]
    public async Task Improving_an_old_pass_updates_what_it_shows_but_not_when_it_was_passed()
    {
        var firstPassed = DateTimeOffset.UtcNow.AddDays(-200);
        await using var db = await OsuMapsAsync(x => x.Scores.Add(Seed.Score(10, firstPassed, accuracy: 0.90)));
        using var osu = new OsuApiHarness(r => OsuWebRecent(r, "osu",
            [OsuJson.Score(777, 10, DateTimeOffset.UtcNow.AddHours(-1), accuracy: 0.97, rank: "S")]));

        await Job(osu, db).RunAsync(Ct);

        await using var x = db.Context();
        var row = await x.Scores.SingleAsync(Ct);
        Assert.Equal((777L, 0.97, "S"), (row.ScoreId, row.Accuracy, row.Grade));
        Assert.Equal(firstPassed.ToUnixTimeSeconds(), row.PlayedAtUnix);
        Assert.Equal(0, await RecentPassesAsync(db));
    }

    [Fact]
    public async Task Promoting_a_pre_rank_row_dates_it_by_the_qualifying_pass()
    {
        await using var db = await OsuMapsAsync(x =>
            x.Scores.Add(Seed.Score(10, Seed.RankedLongAgo.AddDays(-3), countsAsPass: false, accuracy: 0.99)));
        var passedAt = DateTimeOffset.UtcNow.AddHours(-2);
        using var osu = new OsuApiHarness(r => OsuWebRecent(r, "osu", [OsuJson.Score(778, 10, passedAt, accuracy: 0.80)]));

        await Job(osu, db).RunAsync(Ct);

        await using var x = db.Context();
        var row = await x.Scores.SingleAsync(Ct);
        Assert.True(row.CountsAsPass);
        Assert.Equal((778L, 0.80), (row.ScoreId, row.Accuracy));
        Assert.Equal(passedAt.ToUnixTimeSeconds(), row.PlayedAtUnix);
        Assert.Equal(1, await RecentPassesAsync(db));
    }

    [Fact]
    public async Task Two_passes_of_a_new_map_in_one_window_date_it_by_the_earlier()
    {
        await using var db = await OsuMapsAsync();
        var now = DateTimeOffset.UtcNow;
        using var osu = new OsuApiHarness(r => OsuWebRecent(r, "osu",
        [
            OsuJson.Score(801, 12, now.AddHours(-1), accuracy: 0.85),
            OsuJson.Score(800, 12, now.AddHours(-5), accuracy: 0.95)
        ]));

        await Job(osu, db).RunAsync(Ct);

        await using var x = db.Context();
        var row = await x.Scores.SingleAsync(Ct);
        Assert.Equal(now.AddHours(-5).ToUnixTimeSeconds(), row.PlayedAtUnix);
        Assert.Equal((800L, 0.95), (row.ScoreId, row.Accuracy));
    }

    [Fact]
    public async Task An_upgrade_carries_every_display_field_including_the_client()
    {
        await using var db = await OsuMapsAsync(x => x.Scores.Add(Seed.Score(10, DateTimeOffset.UtcNow.AddDays(-50), accuracy: 0.90, isLazer: false)));
        using var osu = new OsuApiHarness(r => OsuWebRecent(r, "osu",
            [OsuJson.Score(779, 10, DateTimeOffset.UtcNow.AddHours(-1), accuracy: 0.98, lazer: true, rank: "SH", mods: ["HD", "CL"])]));

        await Job(osu, db).RunAsync(Ct);

        await using var x = db.Context();
        var row = await x.Scores.SingleAsync(Ct);
        Assert.Equal((779L, 0.98, "SH", "HD,CL", true), (row.ScoreId, row.Accuracy, row.Grade, row.Mods, row.IsLazer));
    }

    [Fact]
    public async Task A_convert_and_a_pre_rank_play_are_not_recorded()
    {
        await using var db = await TestDatabase.CreateAsync();
        var rankedRecently = DateTimeOffset.UtcNow.AddHours(-3);
        await db.SeedAsync(x =>
        {
            x.Beatmapsets.Add(Seed.Set(1));
            x.Beatmaps.Add(Seed.Map(10, 1, GameMode.Osu));
            x.Beatmapsets.Add(Seed.Set(2, ranked: rankedRecently));
            x.Beatmaps.Add(Seed.Map(20, 2, GameMode.Osu));
        });
        using var osu = new OsuApiHarness(r => HttpUtility.ParseQueryString(r.RequestUri!.Query)["mode"] switch
        {
            // A catch play on an osu!-native map is a convert.
            "fruits" => FakeOsuServer.Json(OsuJson.Array(OsuJson.Score(1, 10, DateTimeOffset.UtcNow.AddHours(-1)))),
            // An osu! play from before its set was ranked.
            "osu" => FakeOsuServer.Json(OsuJson.Array(OsuJson.Score(2, 20, rankedRecently.AddHours(-2)))),
            _ => FakeOsuServer.Json("[]")
        });

        await Job(osu, db).RunAsync(Ct);

        await using var x = db.Context();
        Assert.Empty(await x.Scores.ToListAsync(Ct));
    }

    [Fact]
    public async Task An_element_that_is_not_a_score_is_skipped_rather_than_failing_the_poll()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.SeedAsync(x =>
        {
            x.Beatmapsets.Add(Seed.Set(1));
            x.Beatmaps.Add(Seed.Map(10, 1, GameMode.Osu));
        });
        var pass = OsuJson.Score(500, 10, DateTimeOffset.UtcNow.AddHours(-1));
        using var osu = new OsuApiHarness(r => FakeOsuServer.Json(
            r.RequestUri!.Query.Contains("mode=osu") ? $"[42, null, {System.Text.Json.JsonSerializer.Serialize(pass)}]" : "[]"));

        await Job(osu, db).RunAsync(Ct);

        await using var x = db.Context();
        Assert.Equal(10L, (await x.Scores.SingleAsync(Ct)).BeatmapId);
        Assert.Equal(SyncJobState.Completed, (await db.JobAsync(RecentScoresJob.JobName))!.State);
    }

    [Fact]
    public async Task Cancelling_mid_request_leaves_the_job_paused_rather_than_running()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var osu = new OsuApiHarness(async (r, token) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
            return FakeOsuServer.Json("[]");
        });

        var ex = await Record.ExceptionAsync(() => Job(osu, db).RunAsync(cts.Token));

        Assert.Null(ex);
        var job = await db.JobAsync(RecentScoresJob.JobName);
        Assert.Equal(SyncJobState.Paused, job!.State);
        Assert.Null(job.Error);
    }
}
