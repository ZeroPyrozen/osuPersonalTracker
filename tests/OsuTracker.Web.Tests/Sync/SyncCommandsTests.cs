using Microsoft.Extensions.DependencyInjection;
using OsuTracker.Web.Data.Entities;
using OsuTracker.Web.Services;
using OsuTracker.Web.Sync;
using OsuTracker.Web.Tests.Infrastructure;

namespace OsuTracker.Web.Tests.Sync;

public sealed class SyncCommandsTests
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

    private static HttpResponseMessage Empty(HttpRequestMessage _) => FakeOsuServer.Json("[]");

    [Theory]
    [InlineData("scores", "--mode", "ctb")]
    [InlineData("scores", "--limit", "ten")]
    [InlineData("scores", "--limit", "0")]
    [InlineData("catalog", "--max-pages", "-1")]
    public async Task An_unreadable_flag_is_an_error_rather_than_a_default(params string[] rest)
    {
        await using var db = await TestDatabase.CreateAsync();
        using var osu = new OsuApiHarness(Empty);
        await using var services = Services(db, osu);

        var exit = await SyncCommands.RunAsync(services, ["sync", .. rest], Ct);

        Assert.Equal(2, exit);
        Assert.Empty(osu.Server.ApiRequests);
    }

    [Fact]
    public async Task A_flag_with_no_value_is_an_error()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var osu = new OsuApiHarness(Empty);
        await using var services = Services(db, osu);

        var exit = await SyncCommands.RunAsync(services, ["sync", "scores", "--mode"], Ct);

        Assert.Equal(2, exit);
        Assert.Empty(osu.Server.ApiRequests);
    }

    [Theory]
    [InlineData("recent")]
    [InlineData("playcounts")]
    [InlineData("scores")]
    public async Task A_missing_user_id_stops_user_commands_before_any_request(string command)
    {
        await using var db = await TestDatabase.CreateAsync();
        using var osu = new OsuApiHarness(Empty, userId: 0);
        await using var services = Services(db, osu);

        var exit = await SyncCommands.RunAsync(services, ["sync", command], Ct);

        Assert.Equal(2, exit);
        Assert.Empty(osu.Server.ApiRequests);
        await using var x = db.Context();
        Assert.Empty(x.SyncJobs);
    }

    [Fact]
    public async Task Missing_credentials_stop_the_catalogue_too()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var osu = new OsuApiHarness(Empty);
        osu.Options.Value.ClientSecret = "";
        await using var services = Services(db, osu);

        var exit = await SyncCommands.RunAsync(services, ["sync", "catalog"], Ct);

        Assert.Equal(2, exit);
        Assert.Equal(0, osu.Server.TokenRequests);
    }

    [Fact]
    public async Task Status_reads_the_database_without_needing_credentials()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var osu = new OsuApiHarness(Empty, userId: 0);
        osu.Options.Value.ClientId = "";
        await using var services = Services(db, osu);

        Assert.Equal(0, await SyncCommands.RunAsync(services, ["sync", "status"], Ct));
    }

    [Fact]
    public async Task A_recognised_mode_alias_is_accepted()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var osu = new OsuApiHarness(Empty);
        await using var services = Services(db, osu);

        var exit = await SyncCommands.RunAsync(services, ["sync", "scores", "--mode", "catch", "--limit", "5"], Ct);

        Assert.Equal(0, exit);
        Assert.Equal(SyncJobState.Completed, (await db.JobAsync($"{ScoreBackfillJob.JobName}:{GameMode.Fruits}"))!.State);
    }

    [Fact]
    public async Task A_poll_completes_and_exits_zero()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var osu = new OsuApiHarness(Empty);
        await using var services = Services(db, osu);

        var exit = await SyncCommands.RunAsync(services, ["sync", "recent"], Ct);

        Assert.Equal(0, exit);
        Assert.Equal(SyncJobState.Completed, (await db.JobAsync(RecentScoresJob.JobName))!.State);
    }

    [Fact]
    public async Task Ctrl_c_exits_130_with_the_job_paused_rather_than_a_stack_trace()
    {
        await using var db = await TestDatabase.CreateAsync();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        using var osu = new OsuApiHarness(async (_, token) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
            return FakeOsuServer.Json("[]");
        });
        await using var services = Services(db, osu);

        var exit = await SyncCommands.RunAsync(services, ["sync", "recent"], cts.Token);

        Assert.Equal(130, exit);
        Assert.Equal(SyncJobState.Paused, (await db.JobAsync(RecentScoresJob.JobName))!.State);
    }
}
