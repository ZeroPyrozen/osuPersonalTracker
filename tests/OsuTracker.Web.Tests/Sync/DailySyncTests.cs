using System.Net;
using System.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OsuTracker.Web.Badges;
using OsuTracker.Web.Data.Entities;
using OsuTracker.Web.Services;
using OsuTracker.Web.Sync;
using OsuTracker.Web.Tests.Infrastructure;

namespace OsuTracker.Web.Tests.Sync;

public sealed class CatalogTopUpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static CatalogSyncJob Job(OsuApiHarness osu, TestDatabase db) =>
        new(osu.Api, db.Factory, NullLogger<CatalogSyncJob>.Instance);

    /// <summary>Newest first: set 9 is new, set 1 is already held. Always offers a next page.</summary>
    private static HttpResponseMessage Newest(HttpRequestMessage r) =>
        HttpUtility.ParseQueryString(r.RequestUri!.Query)["cursor_string"] is null
            ? FakeOsuServer.Json(OsuJson.SearchPage("c1", OsuJson.Set(9, [91, 92]), OsuJson.Set(1, [11])))
            : FakeOsuServer.Json(OsuJson.SearchPage("c1", OsuJson.Set(1, [11])));

    private static Task SeedAsync(TestDatabase db) => db.SeedAsync(x =>
    {
        x.Beatmapsets.Add(Seed.Set(1));
        x.Beatmaps.Add(Seed.Map(11, 1));
        x.Beatmapsets.Add(Seed.Set(2));
        x.Beatmaps.Add(Seed.Map(21, 2));
    });

    [Fact]
    public async Task A_top_up_adds_newly_ranked_sets_and_marks_nothing_departed()
    {
        await using var db = await TestDatabase.CreateAsync();
        await SeedAsync(db);
        using var osu = new OsuApiHarness(Newest);

        await Job(osu, db).RunNewestAsync(pages: 2, Ct);

        await using var x = db.Context();
        Assert.Equal(2, await x.Beatmaps.CountAsync(b => b.BeatmapsetId == 9, Ct));
        // Set 2 was on neither page. A full sweep would call that departed; this must not.
        Assert.Equal(BeatmapStatus.Ranked, (await x.Beatmaps.SingleAsync(b => b.Id == 21, Ct)).Status);
        Assert.Equal(2, osu.Server.ApiRequests.Count);
    }

    [Fact]
    public async Task A_top_up_keeps_its_own_job_row_and_leaves_a_paused_sweep_resumable()
    {
        await using var db = await TestDatabase.CreateAsync();
        await SeedAsync(db);
        await db.SeedAsync(x => x.SyncJobs.Add(new SyncJob
        {
            Name = CatalogSyncJob.JobName, State = SyncJobState.Paused, Cursor = "ranked|c7", ItemsDone = 350
        }));
        using var osu = new OsuApiHarness(Newest);

        await Job(osu, db).RunNewestAsync(pages: 1, Ct);

        var sweep = await db.JobAsync(CatalogSyncJob.JobName);
        Assert.Equal((SyncJobState.Paused, "ranked|c7", 350), (sweep!.State, sweep.Cursor, sweep.ItemsDone));
        var topUp = await db.JobAsync(CatalogSyncJob.TopUpJobName);
        Assert.Equal(SyncJobState.Completed, topUp!.State);
        Assert.NotNull(topUp.LastSuccessAt);
    }
}

public sealed class DailySyncTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ServiceProvider Services(TestDatabase db, OsuApiHarness osu)
    {
        var s = new ServiceCollection();
        s.AddLogging();
        s.AddSingleton(db.Factory);
        s.AddSingleton(osu.Options);
        s.AddSingleton(osu.Api);
        s.AddSingleton<CatalogSyncJob>();
        s.AddSingleton<PlayCountSyncJob>();
        s.AddSingleton<ScoreBackfillJob>();
        s.AddSingleton<RecentScoresJob>();
        s.AddSingleton<ProgressQueryService>();
        return s.BuildServiceProvider();
    }

    [Fact]
    public async Task Daily_counts_a_pass_on_a_map_ranked_since_the_last_sweep()
    {
        // Nothing is held yet: set 9 was ranked after the last full sweep, played and
        // passed more than a day ago. The recent poll can no longer see any of it.
        await using var db = await TestDatabase.CreateAsync();
        using var osu = new OsuApiHarness(r => r.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/beatmapsets/search") =>
                FakeOsuServer.Json(OsuJson.SearchPage(null, OsuJson.Set(9, [91]))),
            var p when p.EndsWith("/most_played") =>
                FakeOsuServer.Json(OsuJson.MostPlayed((91, 2))),
            _ => FakeOsuServer.Json(OsuJson.AllScores(OsuJson.Score(900, 91, DateTimeOffset.UtcNow.AddDays(-3))))
        });
        await using var services = Services(db, osu);

        var exit = await SyncCommands.RunAsync(services, ["sync", "daily"], Ct);

        Assert.Equal(0, exit);
        await using var x = db.Context();
        Assert.True((await x.Scores.SingleAsync(s => s.BeatmapId == 91, Ct)).CountsAsPass);
    }

    [Fact]
    public async Task A_refused_request_is_a_failed_job_and_exit_1_not_an_unhandled_exception()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var osu = new OsuApiHarness(_ => FakeOsuServer.Status(HttpStatusCode.Forbidden));
        await using var services = Services(db, osu);

        var exit = await SyncCommands.RunAsync(services, ["sync", "playcounts"], Ct);

        Assert.Equal(1, exit);
        var job = await db.JobAsync(PlayCountSyncJob.JobName);
        Assert.Equal((SyncJobState.Failed, "osu! API returned 403"), (job!.State, job.Error));
    }
}

public sealed class BadgeSyncDateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly BadgeOptions Banner = BadgeOptions.Parse(null, null, null, null);

    private static BadgeService Service(TestDatabase db) => new(
        new ProgressQueryService(db.Factory),
        Microsoft.Extensions.Options.Options.Create(new OsuTracker.Web.OsuApi.OsuApiOptions { UserId = 4242 }));

    [Fact]
    public async Task The_badge_is_dated_by_the_last_successful_sync_not_by_today()
    {
        await using var db = await TestDatabase.CreateAsync();
        var synced = new DateTimeOffset(2026, 8, 21, 9, 0, 0, TimeSpan.Zero);
        await db.SeedAsync(x =>
        {
            x.SyncJobs.Add(new SyncJob { Name = "A", State = SyncJobState.Completed, LastSuccessAt = synced.AddDays(-9) });
            x.SyncJobs.Add(new SyncJob { Name = "B", State = SyncJobState.Failed, LastSuccessAt = synced });
            x.SyncJobs.Add(new SyncJob { Name = "C", State = SyncJobState.Failed });
        });

        var snap = await Service(db).GetSnapshotAsync(null, ct: Ct);
        var (svg, _, _) = BadgeRenderer.Render(snap, Banner);

        Assert.Contains("Updated 2026-08-21", svg);
    }

    [Fact]
    public async Task A_tracker_that_has_never_synced_says_so()
    {
        await using var db = await TestDatabase.CreateAsync();

        var snap = await Service(db).GetSnapshotAsync(null, ct: Ct);
        var (svg, _, _) = BadgeRenderer.Render(snap, Banner);

        Assert.Contains("Not synced yet", svg);
        Assert.DoesNotContain("Updated", svg);
    }
}
