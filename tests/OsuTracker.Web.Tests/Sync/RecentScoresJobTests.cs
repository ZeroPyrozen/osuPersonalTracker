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
