using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OsuTracker.Web.OsuApi;

namespace OsuTracker.Web.Tests.Infrastructure;

/// <summary>
/// Stands in for osu.ppy.sh at the HttpMessageHandler level, so the real OsuApiClient,
/// token provider and rate limiter all run unmodified. The token endpoint answers by
/// itself; every API request is recorded and handed to the test's route function.
/// </summary>
public sealed class FakeOsuServer(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> api)
    : HttpMessageHandler
{
    private readonly List<Uri> _apiRequests = [];
    private int _tokenRequests;

    /// <summary>Replace to make the token endpoint fail, slow down, or count differently.</summary>
    public Func<int, HttpResponseMessage> TokenResponse { get; set; } = n =>
        Json($$"""{"access_token":"token-{{n}}","expires_in":86400,"token_type":"Bearer"}""");

    public int TokenRequests => Volatile.Read(ref _tokenRequests);

    public IReadOnlyList<Uri> ApiRequests
    {
        get { lock (_apiRequests) return _apiRequests.ToList(); }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.RequestUri!.AbsolutePath == "/oauth/token")
            return Task.FromResult(TokenResponse(Interlocked.Increment(ref _tokenRequests)));

        lock (_apiRequests) _apiRequests.Add(request.RequestUri);
        return api(request, ct);
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Status(HttpStatusCode status) =>
        new(status) { Content = new StringContent("", Encoding.UTF8, "application/json") };
}

/// <summary>The real API stack wired to a FakeOsuServer, with a limiter loose enough never to wait.</summary>
public sealed class OsuApiHarness : IDisposable
{
    public const long UserId = 4242;

    public OsuApiHarness(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> api,
        int requestsPerMinute = 100_000,
        long userId = UserId)
    {
        Server = new FakeOsuServer(api);
        Options = Microsoft.Extensions.Options.Options.Create(new OsuApiOptions
        {
            ClientId = "client",
            ClientSecret = "secret",
            UserId = userId,
            RequestsPerMinute = requestsPerMinute
        });

        var factory = new HandlerFactory(Server);
        Tokens = new OsuTokenProvider(factory, Options, NullLogger<OsuTokenProvider>.Instance);
        Limiter = new OsuRateLimiter(Options);
        Api = new OsuApiClient(factory, Tokens, Limiter, Options, NullLogger<OsuApiClient>.Instance);
    }

    /// <summary>Convenience for routes that do not need the cancellation token.</summary>
    public OsuApiHarness(Func<HttpRequestMessage, HttpResponseMessage> api, int requestsPerMinute = 100_000, long userId = UserId)
        : this((r, _) => Task.FromResult(api(r)), requestsPerMinute, userId)
    {
    }

    public FakeOsuServer Server { get; }
    public IOptions<OsuApiOptions> Options { get; }
    public OsuTokenProvider Tokens { get; }
    public OsuRateLimiter Limiter { get; }
    public OsuApiClient Api { get; }

    public void Dispose()
    {
        Limiter.Dispose();
        Server.Dispose();
    }

    private sealed class HandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
