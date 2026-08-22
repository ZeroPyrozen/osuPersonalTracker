using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Mime;
using System.Text;
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

    public static void MapBadgeEndpoints(this WebApplication app)
    {
        app.MapGet("/badge.svg", (HttpContext ctx, BadgeService badges, CancellationToken ct) =>
            WriteAsync(ctx, badges, null, png: false, ct));

        app.MapGet("/badge.png", (HttpContext ctx, BadgeService badges, CancellationToken ct) =>
            WriteAsync(ctx, badges, null, png: true, ct));

        // Path form as well as the query form: some embedders (osu!'s own BBCode among
        // them) are happier with a URL that ends in a real file extension.
        app.MapGet("/badge/{mode}.svg", (HttpContext ctx, string mode, BadgeService badges, CancellationToken ct) =>
            WriteAsync(ctx, badges, mode, png: false, ct));

        app.MapGet("/badge/{mode}.png", (HttpContext ctx, string mode, BadgeService badges, CancellationToken ct) =>
            WriteAsync(ctx, badges, mode, png: true, ct));
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
        var fresh = q["refresh"].ToString() is "1" or "true";

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
    private const int MaxEntries = 24;
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
