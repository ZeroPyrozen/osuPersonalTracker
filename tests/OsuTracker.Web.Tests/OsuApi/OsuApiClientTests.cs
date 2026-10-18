using System.Net;
using OsuTracker.Web.OsuApi;
using OsuTracker.Web.Tests.Infrastructure;

namespace OsuTracker.Web.Tests.OsuApi;

public sealed class OsuApiClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_404_is_an_answer_not_an_error()
    {
        using var osu = new OsuApiHarness(_ => FakeOsuServer.Status(HttpStatusCode.NotFound));

        var doc = await osu.Api.GetAsync("/beatmaps/1/scores/users/1", Ct);

        Assert.Null(doc);
        Assert.Single(osu.Server.ApiRequests);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task A_client_error_fails_at_once_instead_of_being_retried(HttpStatusCode status)
    {
        using var osu = new OsuApiHarness(_ => FakeOsuServer.Status(status));
        // Retrying would sit in 2s, 4s, 8s, 16s of backoff; give up long before that.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));

        var ex = await Record.ExceptionAsync(() => osu.Api.GetAsync("/x", deadline.Token));

        var http = Assert.IsType<HttpRequestException>(ex);
        Assert.Equal(status, http.StatusCode);
        Assert.Single(osu.Server.ApiRequests);
    }

    [Fact]
    public async Task A_server_error_is_still_retried()
    {
        var calls = 0;
        using var osu = new OsuApiHarness(_ => Interlocked.Increment(ref calls) == 1
            ? FakeOsuServer.Status(HttpStatusCode.BadGateway)
            : FakeOsuServer.Json("""{"ok":true}"""));

        using var doc = await osu.Api.GetAsync("/x", Ct);

        Assert.True(doc!.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(2, osu.Server.ApiRequests.Count);
    }

    [Fact]
    public async Task A_dropped_connection_is_still_retried()
    {
        var calls = 0;
        using var osu = new OsuApiHarness(_ => Interlocked.Increment(ref calls) == 1
            ? throw new HttpRequestException("The response ended prematurely.")
            : FakeOsuServer.Json("""{"ok":true}"""));

        using var doc = await osu.Api.GetAsync("/x", Ct);

        Assert.True(doc!.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(2, osu.Server.ApiRequests.Count);
    }
}

public sealed class OsuRateLimiterTests
{
    [Fact]
    public async Task Penalise_drains_the_bucket_so_the_retry_waits_for_the_next_window()
    {
        using var limiter = new OsuRateLimiter(Microsoft.Extensions.Options.Options.Create(
            new OsuApiOptions { RequestsPerMinute = 10 }));
        Assert.Equal(10, limiter.AvailablePermits);

        await limiter.PenaliseAsync(TimeSpan.Zero, TestContext.Current.CancellationToken);

        Assert.Equal(0, limiter.AvailablePermits);
    }
}
