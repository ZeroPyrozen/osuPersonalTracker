using OsuTracker.Web.Data.Entities;
using OsuTracker.Web.Services;

namespace OsuTracker.Web.Tests.Infrastructure;

/// <summary>Proves the harness itself before anything is built on it.</summary>
public sealed class HarnessSmokeTests
{
    [Fact]
    public async Task Migrations_build_an_empty_database_the_queries_can_read()
    {
        await using var db = await TestDatabase.CreateAsync();

        var rows = await new ProgressQueryService(db.Factory).GetAllModesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.Equal(0, r.Total));
    }

    [Fact]
    public async Task Fake_server_serves_the_real_api_client_and_hands_out_a_token()
    {
        using var osu = new OsuApiHarness(_ => FakeOsuServer.Json("""{"ok":true}"""));

        using var doc = await osu.Api.GetAsync("/me", TestContext.Current.CancellationToken);

        Assert.True(doc!.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, osu.Server.TokenRequests);
        Assert.Equal("/api/v2/me", Assert.Single(osu.Server.ApiRequests).AbsolutePath);
    }

    [Fact]
    public async Task Seeded_rows_round_trip()
    {
        await using var db = await TestDatabase.CreateAsync();
        await db.SeedAsync(x =>
        {
            x.Beatmapsets.Add(Seed.Set(1));
            x.Beatmaps.Add(Seed.Map(10, 1));
        });

        var denominators = await new ProgressQueryService(db.Factory).GetDenominatorsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, denominators[GameMode.Fruits]);
    }
}
