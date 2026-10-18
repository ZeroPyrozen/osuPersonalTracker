using System.Net;
using OsuTracker.Web.Tests.Infrastructure;

namespace OsuTracker.Web.Tests.OsuApi;

public sealed class OsuTokenProviderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_cached_token_is_reused()
    {
        using var osu = new OsuApiHarness(_ => FakeOsuServer.Json("{}"));

        var first = await osu.Tokens.GetTokenAsync(Ct);
        var second = await osu.Tokens.GetTokenAsync(Ct);

        Assert.Equal(first, second);
        Assert.Equal(1, osu.Server.TokenRequests);
    }

    [Fact]
    public async Task A_token_can_be_fetched_again_after_it_is_invalidated()
    {
        using var osu = new OsuApiHarness(_ => FakeOsuServer.Json("{}"));
        await osu.Tokens.GetTokenAsync(Ct);

        osu.Tokens.Invalidate();
        var renewed = await osu.Tokens.GetTokenAsync(Ct);

        Assert.Equal("token-2", renewed);
        Assert.Equal(2, osu.Server.TokenRequests);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_token_request()
    {
        using var osu = new OsuApiHarness(_ => FakeOsuServer.Json("{}"));

        var tokens = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => osu.Tokens.GetTokenAsync(Ct)));

        Assert.All(tokens, t => Assert.Equal("token-1", t));
        Assert.Equal(1, osu.Server.TokenRequests);
    }

    [Fact]
    public async Task A_401_mid_sweep_is_recovered_with_a_fresh_token()
    {
        using var osu = new OsuApiHarness(r =>
            r.Headers.Authorization?.Parameter == "token-1"
                ? FakeOsuServer.Status(HttpStatusCode.Unauthorized)
                : FakeOsuServer.Json("""{"ok":true}"""));

        using var doc = await osu.Api.GetAsync("/me", Ct);

        Assert.True(doc!.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(2, osu.Server.TokenRequests);
        Assert.Equal(2, osu.Server.ApiRequests.Count);
    }
}
