using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OsuTracker.Web.Data.Entities;
using OsuTracker.Web.Sync;
using OsuTracker.Web.Tests.Infrastructure;

namespace OsuTracker.Web.Tests.Sync;

public sealed class PlayCountSyncJobTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PlayCountSyncJob Job(OsuApiHarness osu, TestDatabase db) =>
        new(osu.Api, db.Factory, NullLogger<PlayCountSyncJob>.Instance);

    /// <summary>Maps 1..150 in the catalogue; most_played returns 1..150 plus a few the catalogue lacks.</summary>
    private static async Task<TestDatabase> CatalogueAsync()
    {
        var db = await TestDatabase.CreateAsync();
        await db.SeedAsync(x =>
        {
            x.Beatmapsets.Add(Seed.Set(1));
            for (var i = 1; i <= 150; i++) x.Beatmaps.Add(Seed.Map(i, 1));
        });
        return db;
    }

    private static HttpResponseMessage MostPlayed(HttpRequestMessage r)
    {
        var offset = int.Parse(HttpUtility.ParseQueryString(r.RequestUri!.Query)["offset"]!);
        var rows = Enumerable.Range(1, 160).Skip(offset).Take(100).Select(i => ((long)i, 2)).ToArray();
        return FakeOsuServer.Json(OsuJson.MostPlayed(rows));
    }

    [Fact]
    public async Task Playcounts_are_stored_for_catalogue_maps_only()
    {
        await using var db = await CatalogueAsync();
        using var osu = new OsuApiHarness(MostPlayed);

        await Job(osu, db).RunAsync(resume: false, Ct);

        await using var x = db.Context();
        Assert.Equal(150, await x.PlayCounts.CountAsync(Ct));
        var job = await db.JobAsync(PlayCountSyncJob.JobName);
        Assert.Equal(SyncJobState.Completed, job!.State);
        Assert.Equal(150, job.ItemsDone);
    }

    [Fact]
    public async Task A_row_that_is_not_an_object_is_skipped_rather_than_failing_the_sync()
    {
        await using var db = await CatalogueAsync();
        using var osu = new OsuApiHarness(_ => FakeOsuServer.Json("""[42, null, {"beatmap_id":7,"count":3}]"""));

        await Job(osu, db).RunAsync(resume: false, Ct);

        await using var x = db.Context();
        Assert.Equal(7L, Assert.Single(await x.PlayCounts.ToListAsync(Ct)).BeatmapId);
        Assert.Equal(SyncJobState.Completed, (await db.JobAsync(PlayCountSyncJob.JobName))!.State);
    }

    [Fact]
    public async Task Cancelling_mid_request_leaves_the_job_paused_at_its_offset()
    {
        await using var db = await CatalogueAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var osu = new OsuApiHarness(async (r, token) =>
        {
            if (HttpUtility.ParseQueryString(r.RequestUri!.Query)["offset"] == "100")
            {
                await cts.CancelAsync();
                await Task.Delay(Timeout.Infinite, token);
            }
            return MostPlayed(r);
        });

        var ex = await Record.ExceptionAsync(() => Job(osu, db).RunAsync(resume: false, cts.Token));

        Assert.Null(ex);
        var job = await db.JobAsync(PlayCountSyncJob.JobName);
        Assert.Equal(SyncJobState.Paused, job!.State);
        Assert.Null(job.Error);
        Assert.Equal("100", job.Cursor);
    }
}
