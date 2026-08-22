using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace OsuTracker.Spike;

/// <summary>
/// Minimal osu! API v2 client for the Phase 1 spike. Deliberately not the shape the
/// real app will use — this exists to answer questions, not to be reused.
/// </summary>
public sealed class OsuApiClient : IDisposable
{
    private const string TokenUrl = "https://osu.ppy.sh/oauth/token";
    private const string ApiBase = "https://osu.ppy.sh/api/v2";

    private readonly HttpClient _http = new();
    private readonly string _clientId;
    private readonly string _clientSecret;
    private string? _token;

    public OsuApiClient(string clientId, string clientSecret)
    {
        _clientId = clientId;
        _clientSecret = clientSecret;
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.Add("x-api-version", "20240529");
    }

    /// <summary>Client credentials grant. Returns a guest token with the public scope.</summary>
    public async Task AuthenticateAsync(CancellationToken ct = default)
    {
        using var res = await _http.PostAsJsonAsync(TokenUrl, new
        {
            client_id = _clientId,
            client_secret = _clientSecret,
            grant_type = "client_credentials",
            scope = "public"
        }, ct);

        var body = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Token request failed ({(int)res.StatusCode} {res.StatusCode}). Response: {Truncate(body, 400)}");

        using var doc = JsonDocument.Parse(body);
        _token = doc.RootElement.GetProperty("access_token").GetString()
                 ?? throw new InvalidOperationException("Token response contained no access_token.");

        var expires = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 0;
        Console.WriteLine($"  token acquired, expires in {expires / 3600.0:F1}h");
    }

    /// <summary>
    /// GET a path under /api/v2. Never throws on HTTP status — the status IS the finding
    /// for most of these probes, so it comes back in the result for the caller to judge.
    /// </summary>
    public async Task<ApiResult> GetAsync(string path, CancellationToken ct = default)
    {
        if (_token is null) throw new InvalidOperationException("Call AuthenticateAsync first.");

        using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}{path}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        var sw = Stopwatch.StartNew();
        HttpResponseMessage? res = null;
        string body = "";

        // The osu! servers drop connections under sustained paging, so transport
        // failures are expected operationally, not exceptional. Retry with backoff.
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            try
            {
                using var attemptReq = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}{path}");
                attemptReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                attemptReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                res?.Dispose();
                res = await _http.SendAsync(attemptReq, ct);
                body = await res.Content.ReadAsStringAsync(ct);

                if ((int)res.StatusCode == 429)
                {
                    var wait = TimeSpan.FromSeconds(5 * attempt);
                    Console.WriteLine($"     429 — backing off {wait.TotalSeconds:F0}s");
                    await Task.Delay(wait, ct);
                    continue;
                }
                break;
            }
            catch (HttpRequestException) when (attempt < 4)
            {
                var wait = TimeSpan.FromSeconds(2 * attempt);
                Console.WriteLine($"     transport error — retrying in {wait.TotalSeconds:F0}s (attempt {attempt}/4)");
                await Task.Delay(wait, ct);
            }
            catch (IOException) when (attempt < 4)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }

        sw.Stop();
        if (res is null) throw new HttpRequestException($"GET {path} failed after 4 attempts.");

        JsonElement? json = null;
        if (res.IsSuccessStatusCode && body.Length > 0)
        {
            try { json = JsonDocument.Parse(body).RootElement.Clone(); }
            catch (JsonException) { /* leave null; caller reports raw body */ }
        }

        // Rate-limit headers are not documented as guaranteed; capture whatever is present.
        var rateHeaders = res.Headers
            .Where(h => h.Key.Contains("ratelimit", StringComparison.OrdinalIgnoreCase)
                     || h.Key.Contains("retry-after", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);

        var result = new ApiResult(path, res.StatusCode, json, body, sw.ElapsedMilliseconds, rateHeaders);
        res.Dispose();
        return result;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";

    public void Dispose() => _http.Dispose();
}

public sealed record ApiResult(
    string Path,
    HttpStatusCode Status,
    JsonElement? Json,
    string RawBody,
    long ElapsedMs,
    IReadOnlyDictionary<string, string> RateHeaders)
{
    public bool Ok => (int)Status is >= 200 and < 300;
    public bool NotFound => Status == HttpStatusCode.NotFound;
    public bool Unauthorized => Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    public string StatusLine => $"{(int)Status} {Status} ({ElapsedMs}ms)";
}
