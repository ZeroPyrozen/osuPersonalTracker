using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OsuTracker.Web.OsuApi;

/// <summary>
/// Typed client for osu! API v2. Every call passes through the shared rate limiter and
/// a retry policy that treats dropped connections as ordinary, because Phase 1 showed
/// the servers close the socket under sustained paging rather than returning 429.
/// </summary>
public sealed class OsuApiClient(
    IHttpClientFactory factory,
    OsuTokenProvider tokens,
    OsuRateLimiter limiter,
    IOptions<OsuApiOptions> options,
    ILogger<OsuApiClient> log)
{
    private const string ApiBase = "https://osu.ppy.sh/api/v2";
    private const int MaxAttempts = 5;

    public long UserId => options.Value.UserId;

    /// <summary>
    /// GET and parse. Returns null for 404 — which is a valid answer meaning "no such
    /// score", and by far the most common response during backfill, not an error.
    /// </summary>
    public async Task<JsonDocument?> GetAsync(string path, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            await limiter.WaitAsync(ct);

            HttpResponseMessage? res = null;
            try
            {
                var token = await tokens.GetTokenAsync(ct);
                using var http = factory.CreateClient("osu-api");
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}{path}");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                res = await http.SendAsync(req, ct);

                if (res.StatusCode == HttpStatusCode.NotFound) return null;

                if (res.StatusCode == HttpStatusCode.Unauthorized && attempt < MaxAttempts)
                {
                    log.LogWarning("401 on {Path} — refreshing token", path);
                    tokens.Invalidate();
                    continue;
                }

                if ((int)res.StatusCode == 429)
                {
                    var wait = TimeSpan.FromSeconds(Math.Min(60, 10 * attempt));
                    log.LogWarning("429 on {Path} — pausing {Seconds}s", path, wait.TotalSeconds);
                    await limiter.PenaliseAsync(wait, ct);
                    if (attempt < MaxAttempts) continue;
                }

                res.EnsureSuccessStatusCode();
                var body = await res.Content.ReadAsStringAsync(ct);
                return body.Length == 0 ? null : JsonDocument.Parse(body);
            }
            catch (Exception ex) when (attempt < MaxAttempts && !ct.IsCancellationRequested && IsTransient(ex))
            {
                var wait = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                log.LogWarning("{Kind} on {Path} (attempt {Attempt}/{Max}) — retrying in {Seconds}s",
                    ex.GetType().Name, path, attempt, MaxAttempts, wait.TotalSeconds);
                await Task.Delay(wait, ct);
            }
            finally { res?.Dispose(); }
        }
    }

    /// <summary>
    /// The osu! servers drop connections and stall under load. Those surface as
    /// HttpRequestException / IOException / a timeout, never as a status code, so a
    /// client that only inspects status codes will crash instead of retrying.
    ///
    /// Note on cancellation: an HttpClient timeout throws TaskCanceledException whose
    /// own CancellationToken IS already cancelled, so inspecting that token cannot tell
    /// a timeout apart from a real caller cancel. The caller's token is the only
    /// reliable discriminator, and it is checked in the catch filter above.
    /// </summary>
    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException => true,
        IOException => true,
        TimeoutException => true,
        OperationCanceledException => true,
        _ => false
    };
}
