using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace OsuTracker.Web.OsuApi;

/// <summary>
/// One bucket, registered as a singleton, shared by every sync job. Parallelism buys
/// nothing when a rate limiter is the bottleneck, so jobs simply queue behind this.
/// </summary>
public sealed class OsuRateLimiter : IDisposable
{
    private readonly TokenBucketRateLimiter _limiter;

    public OsuRateLimiter(IOptions<OsuApiOptions> options)
    {
        var perMinute = Math.Max(1, options.Value.RequestsPerMinute);
        _limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = perMinute,
            TokensPerPeriod = perMinute,
            ReplenishmentPeriod = TimeSpan.FromMinutes(1),
            QueueLimit = int.MaxValue,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });
    }

    public async Task WaitAsync(CancellationToken ct)
    {
        using var lease = await _limiter.AcquireAsync(1, ct);
        if (!lease.IsAcquired) throw new InvalidOperationException("Rate limiter refused a lease.");
    }

    /// <summary>Drain the bucket so a 429 costs us the rest of the window, not just one retry.</summary>
    public async Task PenaliseAsync(TimeSpan wait, CancellationToken ct) => await Task.Delay(wait, ct);

    public int AvailablePermits => (int)_limiter.GetStatistics()!.CurrentAvailablePermits;

    public void Dispose() => _limiter.Dispose();
}
