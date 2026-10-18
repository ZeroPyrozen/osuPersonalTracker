using System.Net;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OsuTracker.Web.Data.Entities;
using OsuTracker.Web.Services;
using OsuTracker.Web.Sync;
using OsuTracker.Web.Tests.Infrastructure;

namespace OsuTracker.Web.Tests.Sync;

public sealed partial class ScoreBackfillJobTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ScoreBackfillJob Job(OsuApiHarness osu, TestDatabase db) =>
        new(osu.Api, db.Factory, NullLogger<ScoreBackfillJob>.Instance);

    [GeneratedRegex(@"/beatmaps/(\d+)/scores/users/")]
    private static partial Regex BeatmapInPath();

    private static long BeatmapOf(HttpRequestMessage r) =>
        long.Parse(BeatmapInPath().Match(r.RequestUri!.AbsolutePath).Groups[1].Value);

    /// <summary>Maps 1..n, all played, in a set ranked long ago — the whole queue.</summary>
    private static async Task<TestDatabase> QueueAsync(int n)
    {
        var db = await TestDatabase.CreateAsync();
        await db.SeedAsync(x =>
        {
            x.Beatmapsets.Add(Seed.Set(1));
            for (var i = 1; i <= n; i++)
            {
                x.Beatmaps.Add(Seed.Map(i, 1));
                x.PlayCounts.Add(Seed.Plays(i));
            }
        });
        return db;
    }

    private static HttpResponseMessage OnePass(HttpRequestMessage r) =>
        FakeOsuServer.Json(OsuJson.AllScores(OsuJson.Score(BeatmapOf(r) * 100, BeatmapOf(r), DateTimeOffset.UtcNow.AddDays(-400))));

    [Fact]
    public async Task Every_queued_map_is_fetched_and_the_job_completes()
    {
        await using var db = await QueueAsync(5);
        using var osu = new OsuApiHarness(OnePass);

        await Job(osu, db).RunAsync(resume: false, onlyMode: null, limit: null, Ct);

        await using var x = db.Context();
        Assert.Equal(5, await x.Scores.CountAsync(s => s.CountsAsPass, Ct));
        Assert.Equal(SyncJobState.Completed, (await db.JobAsync(ScoreBackfillJob.JobName))!.State);
    }

    [Fact]
    public async Task An_isolated_failure_is_skipped_and_the_run_still_completes()
    {
        await using var db = await QueueAsync(5);
        using var osu = new OsuApiHarness(r => BeatmapOf(r) == 3 ? FakeOsuServer.Status(HttpStatusCode.Forbidden) : OnePass(r));

        await Job(osu, db).RunAsync(resume: false, onlyMode: null, limit: null, Ct);

        await using var x = db.Context();
        Assert.Equal([1L, 2L, 4L, 5L], await x.Scores.OrderBy(s => s.BeatmapId).Select(s => s.BeatmapId).ToListAsync(Ct));
        Assert.Equal(SyncJobState.Completed, (await db.JobAsync(ScoreBackfillJob.JobName))!.State);
    }

    [Fact]
    public async Task A_run_of_failures_stops_the_backfill_instead_of_skipping_the_whole_queue()
    {
        await using var db = await QueueAsync(40);
        // Revoked credentials: every token request is refused, so every map fails at once.
        using var osu = new OsuApiHarness(OnePass);
        osu.Server.TokenResponse = _ => FakeOsuServer.Json("""{"error":"invalid_client"}""", HttpStatusCode.Unauthorized);

        var ex = await Record.ExceptionAsync(() => Job(osu, db).RunAsync(resume: false, onlyMode: null, limit: null, Ct));

        Assert.NotNull(ex);
        var job = await db.JobAsync(ScoreBackfillJob.JobName);
        Assert.Equal(SyncJobState.Failed, job!.State);
        Assert.True(osu.Server.TokenRequests < 40, $"kept going for {osu.Server.TokenRequests} maps");
    }

    [Fact]
    public async Task After_a_run_of_failures_resume_retries_the_maps_that_failed()
    {
        await using var db = await QueueAsync(40);
        var outage = true;
        using var osu = new OsuApiHarness(r =>
            outage && BeatmapOf(r) > 30 ? FakeOsuServer.Status(HttpStatusCode.Forbidden) : OnePass(r));
        await Record.ExceptionAsync(() => Job(osu, db).RunAsync(resume: false, onlyMode: null, limit: null, Ct));

        outage = false;
        await Job(osu, db).RunAsync(resume: true, onlyMode: null, limit: null, Ct);

        await using var x = db.Context();
        Assert.Equal(40, await x.Scores.CountAsync(Ct));
        Assert.Equal(SyncJobState.Completed, (await db.JobAsync(ScoreBackfillJob.JobName))!.State);
    }

    [Fact]
    public async Task A_map_first_passed_long_ago_is_not_a_recent_pass_however_recently_it_was_bettered()
    {
        await using var db = await QueueAsync(1);
        var firstPassed = DateTimeOffset.UtcNow.AddDays(-300);
        using var osu = new OsuApiHarness(_ => FakeOsuServer.Json(OsuJson.AllScores(
            OsuJson.Score(201, 1, firstPassed, accuracy: 0.90),
            OsuJson.Score(202, 1, DateTimeOffset.UtcNow.AddDays(-2), accuracy: 0.99, rank: "S"))));

        await Job(osu, db).RunAsync(resume: false, onlyMode: null, limit: null, Ct);

        await using var x = db.Context();
        var row = await x.Scores.SingleAsync(Ct);
        Assert.Equal((202L, 0.99, "S"), (row.ScoreId, row.Accuracy, row.Grade));
        Assert.Equal(firstPassed.ToUnixTimeSeconds(), row.PlayedAtUnix);
        Assert.Equal(0, await new ProgressQueryService(db.Factory).GetRecentPassCountAsync(GameMode.Fruits, 30, Ct));
    }

    [Fact]
    public async Task Only_passes_after_the_ranking_qualify_and_the_first_of_them_dates_the_row()
    {
        await using var db = await QueueAsync(1);
        var ranked = Seed.RankedLongAgo;
        using var osu = new OsuApiHarness(_ => FakeOsuServer.Json(OsuJson.AllScores(
            OsuJson.Score(301, 1, ranked.AddDays(-10), accuracy: 0.99),   // qualified-era, best overall
            OsuJson.Score(302, 1, ranked.AddDays(10), accuracy: 0.80),    // first after ranking
            OsuJson.Score(303, 1, ranked.AddDays(40), accuracy: 0.85))));  // best after ranking

        await Job(osu, db).RunAsync(resume: false, onlyMode: null, limit: null, Ct);

        await using var x = db.Context();
        var row = await x.Scores.SingleAsync(Ct);
        Assert.True(row.CountsAsPass);
        Assert.Equal((303L, 0.85), (row.ScoreId, row.Accuracy));
        Assert.Equal(ranked.AddDays(10).ToUnixTimeSeconds(), row.PlayedAtUnix);
    }

    [Fact]
    public async Task A_bare_array_of_scores_is_read_as_well_as_the_envelope()
    {
        await using var db = await QueueAsync(1);
        using var osu = new OsuApiHarness(r => FakeOsuServer.Json(
            OsuJson.Array(OsuJson.Score(100, BeatmapOf(r), DateTimeOffset.UtcNow.AddDays(-400)))));

        await Job(osu, db).RunAsync(resume: false, onlyMode: null, limit: null, Ct);

        await using var x = db.Context();
        Assert.True((await x.Scores.SingleAsync(Ct)).CountsAsPass);
    }

    [Fact]
    public async Task Cancelling_mid_request_leaves_the_job_paused()
    {
        await using var db = await QueueAsync(5);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var osu = new OsuApiHarness(async (r, token) =>
        {
            if (BeatmapOf(r) == 3)
            {
                await cts.CancelAsync();
                await Task.Delay(Timeout.Infinite, token);
            }
            return OnePass(r);
        });

        var ex = await Record.ExceptionAsync(() => Job(osu, db).RunAsync(resume: false, onlyMode: null, limit: null, cts.Token));

        Assert.Null(ex);
        var job = await db.JobAsync(ScoreBackfillJob.JobName);
        Assert.Equal(SyncJobState.Paused, job!.State);
        Assert.Null(job.Error);
    }
}
