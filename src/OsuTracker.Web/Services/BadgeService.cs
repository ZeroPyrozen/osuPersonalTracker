using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using OsuTracker.Web.Badges;
using OsuTracker.Web.Data.Entities;
using OsuTracker.Web.OsuApi;

namespace OsuTracker.Web.Services;

/// <summary>The numbers a badge draws, already reduced to one mode or to the total.</summary>
public sealed record BadgeSnapshot(
    long UserId,
    GameMode? Mode,
    int Total,
    int Passed,
    int Attempted,
    int Untouched,
    int RecentPasses,
    IReadOnlyList<ModeProgress> Modes,
    IReadOnlyList<StarBand> Bands,
    DateTimeOffset GeneratedAt)
{
    public double PassedPercent => Total == 0 ? 0 : Passed * 100.0 / Total;
    public double PlayedPercent => Total == 0 ? 0 : (Passed + Attempted) * 100.0 / Total;

    /// <summary>
    /// Only the drawn numbers go in, so an ETag survives an unrelated sync but changes
    /// the moment a badge would actually look different.
    /// </summary>
    public string Fingerprint => string.Create(CultureInfo.InvariantCulture,
        $"{UserId}|{Mode}|{Total}|{Passed}|{Attempted}|{Untouched}|{RecentPasses}|" +
        $"{string.Join(",", Modes.Select(m => $"{m.Mode}:{m.Passed}/{m.Total}"))}|" +
        $"{string.Join(",", Bands.Select(b => $"{b.Passed}/{b.Total}"))}");
}

/// <summary>
/// Badge stats with a short TTL in front of them. A badge URL sits in an &lt;img&gt; tag
/// on someone else's page, so it can be hit far harder than any dashboard — a handful of
/// aggregate queries per request would put a sync-sized load on SQLite for no new data.
/// </summary>
public sealed class BadgeService(ProgressQueryService progress, IOptions<OsuApiOptions> api)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _gate = new(1, 1);
    // Keyed by the ruleset id with -1 standing in for "all modes": a nullable enum is
    // not a valid dictionary key, and inventing a fifth GameMode would leak into queries.
    private readonly Dictionary<int, (DateTimeOffset At, BadgeSnapshot Snap)> _cache = [];

    private static int Key(GameMode? mode) => mode is null ? -1 : (int)mode.Value;

    /// <summary>Rendering version — bump it when the SVG changes, so cached ETags miss.</summary>
    public const string RenderVersion = "1";

    public async Task<BadgeSnapshot> GetSnapshotAsync(GameMode? mode, bool force = false, CancellationToken ct = default)
    {
        if (!force && TryPeek(mode, out var cached)) return cached;

        await _gate.WaitAsync(ct);
        try
        {
            // Re-check inside the gate: several queued requests would otherwise each
            // run the full query set after the first one had already refreshed it.
            if (!force && TryPeek(mode, out cached)) return cached;

            var snap = await BuildAsync(mode, ct);
            lock (_cache) _cache[Key(mode)] = (DateTimeOffset.UtcNow, snap);
            return snap;
        }
        finally { _gate.Release(); }
    }

    private bool TryPeek(GameMode? mode, out BadgeSnapshot snap)
    {
        lock (_cache)
        {
            if (_cache.TryGetValue(Key(mode), out var hit) && DateTimeOffset.UtcNow - hit.At < Ttl)
            {
                snap = hit.Snap;
                return true;
            }
        }
        snap = null!;
        return false;
    }

    private async Task<BadgeSnapshot> BuildAsync(GameMode? mode, CancellationToken ct)
    {
        var modes = await progress.GetAllModesAsync(ct);

        if (mode is null)
        {
            var recent = 0;
            foreach (var m in Enum.GetValues<GameMode>())
                recent += await progress.GetRecentPassCountAsync(m, 30, ct);

            return new BadgeSnapshot(
                api.Value.UserId, null,
                modes.Sum(m => m.Total), modes.Sum(m => m.Passed),
                modes.Sum(m => m.Attempted), modes.Sum(m => m.Untouched),
                recent, modes, [], DateTimeOffset.UtcNow);
        }

        var row = modes.First(m => m.Mode == mode.Value);
        var bands = await progress.GetStarBandsAsync(mode.Value, ct);
        var passes = await progress.GetRecentPassCountAsync(mode.Value, 30, ct);

        return new BadgeSnapshot(
            api.Value.UserId, mode, row.Total, row.Passed, row.Attempted, row.Untouched,
            passes, modes, bands, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// ETag over the drawn numbers plus the look. Weak would be wrong here: two badges
    /// with the same stats but different themes are not interchangeable bytes.
    /// </summary>
    public static string ETag(BadgeSnapshot snap, BadgeOptions options)
    {
        var raw = $"{RenderVersion}|{options.CacheKey}|{snap.Fingerprint}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return "\"" + Convert.ToHexString(hash)[..20].ToLowerInvariant() + "\"";
    }
}
