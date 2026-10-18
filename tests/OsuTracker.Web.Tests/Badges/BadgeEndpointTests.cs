using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OsuTracker.Web.Badges;
using OsuTracker.Web.Data;
using OsuTracker.Web.OsuApi;
using OsuTracker.Web.Services;
using OsuTracker.Web.Tests.Infrastructure;

namespace OsuTracker.Web.Tests.Badges;

/// <summary>The badge endpoints in a real ASP.NET Core pipeline, on an in-memory server.</summary>
public sealed class BadgeEndpointTests : IAsyncLifetime
{
    private TestDatabase _db = null!;
    private GatedDbFactory _contexts = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _db = await TestDatabase.CreateAsync();
        await _db.SeedAsync(x =>
        {
            x.Beatmapsets.Add(Seed.Set(1));
            x.Beatmaps.Add(Seed.Map(10, 1));
            x.Beatmaps.Add(Seed.Map(11, 1));
            x.Scores.Add(Seed.Score(10, DateTimeOffset.UtcNow.AddDays(-3)));
        });
        _contexts = new GatedDbFactory(_db);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IDbContextFactory<TrackerDbContext>>(_contexts);
        builder.Services.AddSingleton(Options.Create(new OsuApiOptions { UserId = 4242 }));
        builder.Services.AddSingleton<ProgressQueryService>();
        builder.Services.AddSingleton<BadgeService>();
        builder.Services.AddRateLimiter(o =>
            o.AddPolicy(BadgeEndpoints.RateLimitPolicy, _ => RateLimitPartition.GetNoLimiter("test")));

        _app = builder.Build();
        _app.Use((ctx, next) =>
        {
            // Lets a test say where its request comes from, as ForwardedHeaders does in the app.
            if (ctx.Request.Headers.TryGetValue("X-Test-Remote", out var ip))
                ctx.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());

            // HttpClient splits a typed If-None-Match list into separate values before the
            // in-memory server sees it; Kestrel hands over one header line as one string.
            // This delivers it the way a real socket does.
            if (ctx.Request.Headers.TryGetValue("X-Test-Raw-If-None-Match", out var raw))
                ctx.Request.Headers.IfNoneMatch = raw.ToString();
            return next(ctx);
        });
        _app.UseRateLimiter();
        _app.MapBadgeEndpoints();
        await _app.StartAsync(Ct);
        _client = _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        await _db.DisposeAsync();
    }

    private async Task<HttpResponseMessage> GetAsync(string path, string? ifNoneMatch = null, string? from = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (ifNoneMatch is not null) request.Headers.Add("X-Test-Raw-If-None-Match", ifNoneMatch);
        if (from is not null) request.Headers.Add("X-Test-Remote", from);
        return await _client.SendAsync(request, Ct);
    }

    private async Task<string> ETagOfAsync(string path)
    {
        using var first = await GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        return first.Headers.GetValues("ETag").Single();
    }

    // ---- revalidation, as RFC 9110 §13.1.2 defines If-None-Match -----------------------

    [Theory]
    [InlineData("/badge.svg")]
    [InlineData("/badge/fruits.png?scale=1")]
    public async Task An_unchanged_badge_revalidates_to_304(string path)
    {
        var etag = await ETagOfAsync(path);

        using var again = await GetAsync(path, etag);

        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
        Assert.Equal(etag, again.Headers.GetValues("ETag").Single());
    }

    [Fact]
    public async Task A_tag_weakened_by_a_proxy_still_revalidates()
    {
        // A compressing proxy may turn "abc" into W/"abc"; If-None-Match compares weakly.
        var etag = await ETagOfAsync("/badge.svg");

        using var again = await GetAsync("/badge.svg", $"W/{etag}");

        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
    }

    [Fact]
    public async Task A_list_of_tags_revalidates_when_any_of_them_matches()
    {
        var etag = await ETagOfAsync("/badge.svg");

        using var again = await GetAsync("/badge.svg", $"\"something-else\", {etag}");

        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
    }

    [Fact]
    public async Task A_star_revalidates_any_current_badge()
    {
        using var again = await GetAsync("/badge.svg", "*");

        Assert.Equal(HttpStatusCode.NotModified, again.StatusCode);
    }

    [Theory]
    [InlineData("\"stale\"")]
    // Anyone on the internet can send this header; nonsense in it must cost a 200, not a 500.
    [InlineData("not-a-tag")]
    [InlineData("W/")]
    public async Task A_stale_or_malformed_tag_gets_the_badge(string ifNoneMatch)
    {
        using var again = await GetAsync("/badge.svg", ifNoneMatch);

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.StartsWith("<?xml", await again.Content.ReadAsStringAsync(Ct));
    }

    // ---- what a badge response is allowed to do ---------------------------------------

    [Fact]
    public async Task The_svg_can_run_nothing_even_when_opened_as_a_page()
    {
        using var response = await GetAsync("/badge.svg");

        Assert.Equal("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task The_png_is_not_sniffed_as_anything_else()
    {
        using var response = await GetAsync("/badge.png?scale=1");

        Assert.Equal("image/png", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    // ---- refresh=1 is for whoever is actually here (existing guard, pinned) -----------

    [Fact]
    public async Task Refresh_from_the_internet_is_served_from_the_cache()
    {
        using (await GetAsync("/badge.svg")) { }
        var built = _contexts.Disposed;

        using (await GetAsync("/badge.svg?refresh=1", from: "203.0.113.9")) { }
        Assert.Equal(built, _contexts.Disposed);

        using (await GetAsync("/badge.svg?refresh=1", from: "127.0.0.1")) { }
        Assert.True(_contexts.Disposed > built, "a local refresh=1 should rebuild the snapshot");
    }
}
