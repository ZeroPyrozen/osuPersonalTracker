using System.Net;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OsuTracker.Web.Data.Entities;
using OsuTracker.Web.Sync;
using OsuTracker.Web.Tests.Infrastructure;

namespace OsuTracker.Web.Tests.Sync;

public sealed class CatalogSyncJobTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Three pages: sets 1+2, then 3+4, then 5 — cursors c1, c2, then the end.</summary>
    private static HttpResponseMessage Catalogue(HttpRequestMessage r) =>
        HttpUtility.ParseQueryString(r.RequestUri!.Query)["cursor_string"] switch
        {
            null => FakeOsuServer.Json(OsuJson.SearchPage("c1", OsuJson.Set(1, [11]), OsuJson.Set(2, [21, 22]))),
            "c1" => FakeOsuServer.Json(OsuJson.SearchPage("c2", OsuJson.Set(3, [31]), OsuJson.Set(4, [41]))),
            "c2" => FakeOsuServer.Json(OsuJson.SearchPage(null, OsuJson.Set(5, [51]))),
            var other => throw new InvalidOperationException($"unexpected cursor {other}")
        };

    private static CatalogSyncJob Job(OsuApiHarness osu, TestDatabase db) =>
        new(osu.Api, db.Factory, NullLogger<CatalogSyncJob>.Instance);

    /// <summary>A catalogue as a previous full sweep left it: every set seen, all counted.</summary>
    private static Task SeedPreviousSweepAsync(TestDatabase db) => db.SeedAsync(x =>
    {
        foreach (var (set, maps) in new[] { (1L, new[] { 11L }), (2, [21, 22]), (3, [31]), (4, [41]), (5, [51]) })
        {
            x.Beatmapsets.Add(Seed.Set(set));
            foreach (var m in maps) x.Beatmaps.Add(Seed.Map(m, set));
        }
    });

    [Fact]
    public async Task A_full_sweep_writes_every_set_and_completes()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var osu = new OsuApiHarness(Catalogue);

        await Job(osu, db).RunAsync(resume: false, Ct);

        await using var x = db.Context();
        Assert.Equal(5, await x.Beatmapsets.CountAsync(Ct));
        Assert.Equal(6, await x.Beatmaps.CountAsync(b => b.Status == BeatmapStatus.Ranked, Ct));
        var job = await db.JobAsync(CatalogSyncJob.JobName);
        Assert.Equal(SyncJobState.Completed, job!.State);
        Assert.Equal(5, job.ItemsDone);
        Assert.Null(job.Cursor);
    }

    [Fact]
    public async Task A_set_missing_from_a_finished_sweep_is_marked_departed()
    {
        await using var db = await TestDatabase.CreateAsync();
        await SeedPreviousSweepAsync(db);
        await db.SeedAsync(x =>
        {
            x.Beatmapsets.Add(Seed.Set(9));
            x.Beatmaps.Add(Seed.Map(91, 9));
        });
        using var osu = new OsuApiHarness(Catalogue);

        await Job(osu, db).RunAsync(resume: false, Ct);

        await using var x = db.Context();
        Assert.Equal(BeatmapStatus.Departed, (await x.Beatmaps.FindAsync([91L], Ct))!.Status);
        Assert.Equal(6, await x.Beatmaps.CountAsync(b => b.Status == BeatmapStatus.Ranked, Ct));
    }

    [Fact]
    public async Task Stopping_early_with_max_pages_leaves_the_rest_of_the_catalogue_counted()
    {
        await using var db = await TestDatabase.CreateAsync();
        await SeedPreviousSweepAsync(db);
        using var osu = new OsuApiHarness(Catalogue);

        await Job(osu, db).RunAsync(resume: false, Ct, maxPages: 1);

        await using var x = db.Context();
        Assert.Equal(0, await x.Beatmaps.CountAsync(b => b.Status == BeatmapStatus.Departed, Ct));
        var job = await db.JobAsync(CatalogSyncJob.JobName);
        Assert.Equal(SyncJobState.Paused, job!.State);
        Assert.Equal("ranked|c1", job.Cursor);
    }

    [Fact]
    public async Task A_sweep_stopped_by_max_pages_resumes_where_it_stopped_and_then_completes()
    {
        await using var db = await TestDatabase.CreateAsync();
        await SeedPreviousSweepAsync(db);
        using var osu = new OsuApiHarness(Catalogue);
        await Job(osu, db).RunAsync(resume: false, Ct, maxPages: 1);

        await Job(osu, db).RunAsync(resume: true, Ct);

        await using var x = db.Context();
        Assert.Equal(0, await x.Beatmaps.CountAsync(b => b.Status == BeatmapStatus.Departed, Ct));
        var job = await db.JobAsync(CatalogSyncJob.JobName);
        Assert.Equal(SyncJobState.Completed, job!.State);
        Assert.Equal(5, job.ItemsDone);
        Assert.Equal(3, osu.Server.ApiRequests.Count);
    }

    [Fact]
    public async Task Resuming_a_sweep_that_already_completed_counts_from_zero()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var osu = new OsuApiHarness(Catalogue);
        await Job(osu, db).RunAsync(resume: false, Ct);

        await Job(osu, db).RunAsync(resume: true, Ct);

        Assert.Equal(5, (await db.JobAsync(CatalogSyncJob.JobName))!.ItemsDone);
    }

    [Fact]
    public async Task Cancelling_mid_request_leaves_the_job_paused_with_its_cursor()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var osu = new OsuApiHarness(async (r, token) =>
        {
            if (HttpUtility.ParseQueryString(r.RequestUri!.Query)["cursor_string"] == "c1")
            {
                // Ctrl+C lands while the request is in flight, which is nearly always.
                await cts.CancelAsync();
                await Task.Delay(Timeout.Infinite, token);
            }
            return Catalogue(r);
        });

        var ex = await Record.ExceptionAsync(() => Job(osu, db).RunAsync(resume: false, cts.Token));

        Assert.Null(ex);
        var job = await db.JobAsync(CatalogSyncJob.JobName);
        Assert.Equal(SyncJobState.Paused, job!.State);
        Assert.Null(job.Error);
        Assert.Equal("ranked|c1", job.Cursor);
    }

    [Fact]
    public async Task A_failure_is_recorded_without_its_details_and_keeps_the_cursor()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var osu = new OsuApiHarness(r =>
            HttpUtility.ParseQueryString(r.RequestUri!.Query)["cursor_string"] == "c1"
                ? FakeOsuServer.Status(HttpStatusCode.Forbidden)
                : Catalogue(r));

        await Assert.ThrowsAsync<HttpRequestException>(() => Job(osu, db).RunAsync(resume: false, Ct));

        var job = await db.JobAsync(CatalogSyncJob.JobName);
        Assert.Equal(SyncJobState.Failed, job!.State);
        Assert.Equal("osu! API returned 403", job.Error);
        Assert.Equal("ranked|c1", job.Cursor);
    }
}
