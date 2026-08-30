using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Mime;
using System.Text;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;
using OsuTracker.Web.Services;

namespace OsuTracker.Web.Badges;

/// <summary>
/// The public face of the badge: two image endpoints that anything able to fetch a URL
/// can embed. Both are anonymous by design — a badge that needs a session is a badge
/// nobody else can see.
/// </summary>
public static class BadgeEndpoints
{
    /// <summary>
    /// A minute matches the snapshot TTL. Longer would leave a stale badge pinned in
    /// someone's CDN after a sync; shorter just re-sends bytes that cannot have changed.
    /// </summary>
    private const int MaxAgeSeconds = 60;

    /// <summary>Named so Program.cs and the endpoints cannot drift apart on the string.</summary>
    public const string RateLimitPolicy = "badge";

    /// <summary>Above this a PNG rasterise roughly doubles again in cost.</summary>
    private const int ExpensiveScale = 3;

    public static void MapBadgeEndpoints(this WebApplication app)
    {
        app.MapGet("/badge.svg", (HttpContext ctx, BadgeService badges, CancellationToken ct) =>
            WriteAsync(ctx, badges, null, png: false, ct))
            .RequireRateLimiting(RateLimitPolicy);

        app.MapGet("/badge.png", (HttpContext ctx, BadgeService badges, CancellationToken ct) =>
            WriteAsync(ctx, badges, null, png: true, ct))
            .RequireRateLimiting(RateLimitPolicy);

        // Path form as well as the query form: some embedders (osu!'s own BBCode among
        // them) are happier with a URL that ends in a real file extension.
        app.MapGet("/badge/{mode}.svg", (HttpContext ctx, string mode, BadgeService badges, CancellationToken ct) =>
            WriteAsync(ctx, badges, mode, png: false, ct))
            .RequireRateLimiting(RateLimitPolicy);

        app.MapGet("/badge/{mode}.png", (HttpContext ctx, string mode, BadgeService badges, CancellationToken ct) =>
            WriteAsync(ctx, badges, mode, png: true, ct))
            .RequireRateLimiting(RateLimitPolicy);
    }

    /// <summary>What a request costs to serve when the raster cache misses.</summary>
    public enum BadgeCost
    {
        /// <summary>SVG: built from the cached snapshot, a few KB, no rasterising at all.</summary>
        Vector,
        /// <summary>PNG at embed scale — measured at ~210 ms and ~100 KB on a miss.</summary>
        Raster,
        /// <summary>PNG at download scale — ~450 KB and up to ~450 ms on a miss.</summary>
        LargeRaster
    }

    /// <summary>
    /// How dear this request is, decided before the handler runs so the limiter can bucket
    /// it. Read from the raw query rather than from BadgeOptions, which does not exist yet
    /// at partitioning time.
    ///
    /// Scale is the discriminator rather than "will it miss the cache", which is the thing
    /// that actually costs — but is unknowable this early. It correlates well enough: an
    /// honest embedder asks for one URL over and over and is served from cache whatever
    /// its tier, while a caller varying the accent to force misses pays the tier's price
    /// every time.
    /// </summary>
    public static BadgeCost CostOf(HttpContext ctx)
    {
        if (ctx.Request.Path.Value?.EndsWith(".png", StringComparison.OrdinalIgnoreCase) != true)
            return BadgeCost.Vector;

        return ParseScale(ctx.Request.Query["scale"].ToString()) >= ExpensiveScale
            ? BadgeCost.LargeRaster
            : BadgeCost.Raster;
    }

    /// <summary>Requests per minute allowed per client, per tier.</summary>
    public static int AllowancePerMinute(BadgeCost cost) => cost switch
    {
        // A page embedding the badge fetches it once and then honours max-age for a
        // minute, so even a busy embedder stays far under these.
        BadgeCost.Vector => 60,
        BadgeCost.Raster => 30,
        // A download is a handful of clicks, never a stream.
        BadgeCost.LargeRaster => 12,
        _ => 30
    };

    /// <summary>
    /// Whether the caller is on this machine or this LAN. Only meaningful once
    /// ForwardedHeaders has run — before that every Funnel request looks like loopback,
    /// which is exactly the mistake this guards against.
    /// </summary>
    private static bool IsLocal(HttpContext ctx)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is null) return false;
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();

        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254);
        }

        return ip.IsIPv6LinkLocal || ip.IsIPv6UniqueLocal;
    }

    private static async Task<IResult> WriteAsync(
        HttpContext ctx, BadgeService badges, string? modeFromPath, bool png, CancellationToken ct)
    {
        var q = ctx.Request.Query;
        var options = BadgeOptions.Parse(
            modeFromPath ?? q["mode"].ToString(),
            q["layout"].ToString(),
            q["theme"].ToString(),
            q["accent"].ToString());

        var scale = png ? ParseScale(q["scale"].ToString()) : 1;

        // refresh=1 skips the snapshot TTL and re-runs every aggregate over the whole
        // catalogue — 200x the cost of the cached answer, and the cache is the only thing
        // standing between a public URL and the database. Nobody embedding a badge needs
        // it; it exists so the owner can see a sync land immediately. So it stays, but
        // only for whoever is actually here.
        var fresh = q["refresh"].ToString() is "1" or "true" && IsLocal(ctx);

        var snap = await badges.GetSnapshotAsync(options.Mode, fresh, ct);
        var etag = Tag(BadgeService.ETag(snap, options), png, scale);

        ctx.Response.Headers.CacheControl = $"public, max-age={MaxAgeSeconds}";
        ctx.Response.Headers.ETag = etag;
        // Badges are embedded cross-origin by definition; without this a browser on
        // some other page refuses to paint the image it just downloaded.
        ctx.Response.Headers.AccessControlAllowOrigin = "*";

        if (ctx.Request.Headers.IfNoneMatch.Any(v => v == etag))
            return Results.StatusCode(StatusCodes.Status304NotModified);

        var (svg, width, height) = BadgeRenderer.Render(snap, options);

        if (!png)
            return Results.Text(svg, "image/svg+xml", Encoding.UTF8);

        var bytes = BadgeRasterizer.ToPng(etag, svg, width, height, scale);
        if (bytes.Length == 0)
        {
            // Rasterising failed, so answer with the vector rather than a broken image:
            // every embedder that accepts a PNG renders an SVG too.
            ctx.Response.Headers.Remove(HeaderNames.ETag);
            return Results.Text(svg, "image/svg+xml", Encoding.UTF8);
        }

        if (q["download"].ToString() is "1" or "true")
            ctx.Response.Headers.ContentDisposition =
                $"attachment; filename=osu-tracker-{options.ModeSlug}-{options.Layout.ToString().ToLowerInvariant()}.png";

        return Results.File(bytes, MediaTypeNames.Image.Png);
    }

    /// <summary>Above 4x a banner is 3520px wide, which no profile page wants.</summary>
    private static int ParseScale(string raw) =>
        int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? Math.Clamp(v, 1, 4)
            : 2;

    /// <summary>Format and scale change the bytes, so they have to change the tag.</summary>
    private static string Tag(string baseTag, bool png, int scale) =>
        png ? baseTag[..^1] + $"-png{scale}\"" : baseTag;
}

/// <summary>
/// SVG to PNG, so the badge can go somewhere that will not take a vector — osu!'s own
/// profile among them, since its image proxy only re-serves raster formats.
/// </summary>
public static class BadgeRasterizer
{
    // Rasterising is orders of magnitude dearer than building the SVG, and the same
    // handful of URLs get hit over and over, so finished bytes are worth holding.
    //
    // 24 was under even the honest variety: four modes by two layouts by two themes is
    // sixteen badges before a single scale is chosen, so the legitimate set could evict
    // itself. Sized to hold all of it now, which also means a miss is a real signal that
    // someone is varying the accent rather than embedding a badge.
    private const int MaxEntries = 64;
    private static readonly ConcurrentDictionary<string, byte[]> Cache = new();

    public static byte[] ToPng(string key, string svg, int width, int height, int scale)
    {
        if (Cache.TryGetValue(key, out var hit)) return hit;

        var bytes = Rasterize(svg, width, height, scale);

        // Crude eviction on purpose: this is a fixed set of badge variants, not a
        // general cache, so "clear it and refill" costs one render per live URL.
        if (Cache.Count >= MaxEntries) Cache.Clear();
        if (bytes.Length > 0) Cache[key] = bytes;

        return bytes;
    }

    private static byte[] Rasterize(string svg, int width, int height, int scale)
    {
        try
        {
            using var doc = new Svg.Skia.SKSvg();
            if (doc.FromSvg(svg) is not { } picture) return [];

            var info = new SkiaSharp.SKImageInfo(width * scale, height * scale);
            using var surface = SkiaSharp.SKSurface.Create(info);
            var canvas = surface.Canvas;

            // Transparent, not white: the card has rounded corners, and a white fill
            // would show as four hard tabs against any dark page.
            canvas.Clear(SkiaSharp.SKColors.Transparent);
            canvas.Scale(scale);
            canvas.DrawPicture(picture);
            canvas.Flush();

            using var image = surface.Snapshot();
            using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        catch (Exception)
        {
            // A failed rasterise must not take the request down — the caller falls back
            // to serving the SVG, which is the same badge in a different container.
            return [];
        }
    }
}
