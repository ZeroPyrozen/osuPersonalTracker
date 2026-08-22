using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace OsuTracker.Web.OsuApi;

/// <summary>
/// Client-credentials grant. Phase 1 confirmed a guest token reaches every endpoint
/// this app needs, so there is no authorization-code flow to maintain.
/// </summary>
public sealed class OsuTokenProvider(IHttpClientFactory factory, IOptions<OsuApiOptions> options, ILogger<OsuTokenProvider> log)
{
    private const string TokenUrl = "https://osu.ppy.sh/oauth/token";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        // Refresh a minute early rather than discovering expiry as a 401 mid-sweep.
        if (_token is not null && DateTimeOffset.UtcNow < _expiresAt.AddMinutes(-1))
            return _token;

        await _gate.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiresAt.AddMinutes(-1))
                return _token;

            var o = options.Value;
            using var http = factory.CreateClient("osu-token");
            using var res = await http.PostAsJsonAsync(TokenUrl, new
            {
                client_id = o.ClientId,
                client_secret = o.ClientSecret,
                grant_type = "client_credentials",
                scope = "public"
            }, ct);

            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new InvalidOperationException($"osu! token request failed: {(int)res.StatusCode} {res.StatusCode}. {body}");

            using var doc = JsonDocument.Parse(body);
            _token = doc.RootElement.GetProperty("access_token").GetString()
                     ?? throw new InvalidOperationException("Token response had no access_token.");
            var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);

            log.LogInformation("osu! token acquired, valid for {Hours:F1}h", expiresIn / 3600.0);
            return _token;
        }
        finally { _gate.Release(); }
    }

    public void Invalidate() => _expiresAt = DateTimeOffset.MinValue;
}
