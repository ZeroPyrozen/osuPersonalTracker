using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OsuTracker.Web.Data.Entities;
using OsuTracker.Web.Sync;
using OsuTracker.Web.Tests.Infrastructure;

namespace OsuTracker.Web.Tests.Sync;

public sealed class RecentScoresJobTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RecentScoresJob Job(OsuApiHarness osu, TestDatabase db) =>
        new(osu.Api, db.Factory, NullLogger<RecentScoresJob>.Instance);

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
