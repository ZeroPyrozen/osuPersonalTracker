using System.Globalization;
using System.Text;
using OsuTracker.Web.Services;

namespace OsuTracker.Web.Badges;

/// <summary>
/// Draws the badge as a standalone SVG.
///
/// Two constraints shape everything here. The badge is consumed through an
/// &lt;img&gt; tag, so it cannot reach out for a webfont, a stylesheet or an avatar —
/// only system font stacks and self-contained markup survive. And SVG text neither
/// wraps nor reports its width, so every string is placed at a fixed anchor and kept
/// short enough that the widest plausible value still clears the next column.
/// </summary>
public static class BadgeRenderer
{
    private const string Display = "'Trebuchet MS','Segoe UI',Verdana,system-ui,sans-serif";
    private const string Mono = "'Cascadia Mono',Consolas,'DejaVu Sans Mono',monospace";

    public static (string Svg, int Width, int Height) Render(BadgeSnapshot s, BadgeOptions o) =>
        o.Layout == BadgeLayout.Slim ? Slim(s, o) : Banner(s, o);

    // ---- banner: 880 x 210 ---------------------------------------------------------

    private static (string, int, int) Banner(BadgeSnapshot s, BadgeOptions o)
    {
        const int w = 880, h = 210;
        var p = BadgePalette.For(o.Theme);
        var sb = new StringBuilder(4096);

        Open(sb, w, h, p, o, 14);

        // The ring is the badge's face, standing in for the avatar an osekai-style card
        // would carry: this app stores progress, not profile data.
        Ring(sb, 90, 96, 44, 11, s.PassedPercent, p, o.Accent, 26, 13);
        Text(sb, 90, 166, "osu!PersonalTracker", p.Muted, 10, Mono, anchor: "middle", spacing: 0.02);
        if (s.UserId > 0)
            Text(sb, 90, 182, $"#{s.UserId}", p.Muted, 9.5, Mono, anchor: "middle", opacity: 0.75);

        const int x0 = 168, x1 = 848, colW = x1 - x0;

        var scope = s.Mode is null ? "ALL MODES" : BadgePalette.ModeName(s.Mode.Value).ToUpperInvariant();
        Text(sb, x0, 40, $"{scope} · RANKED + APPROVED", p.Muted, 10, Mono, spacing: 0.11);
        Text(sb, x1, 40, $"UPDATED {s.GeneratedAt:yyyy-MM-dd}", p.Muted, 10, Mono, anchor: "end", spacing: 0.08, opacity: 0.8);

        // One <text> with two tspans rather than two <text> elements: the suffix has to
        // start immediately after a number whose rendered width is unknown here.
        sb.Append(CultureInfo.InvariantCulture,
            $"<text x=\"{x0}\" y=\"78\" font-family=\"{Display}\"><tspan font-size=\"28\" font-weight=\"700\" fill=\"{p.Ink}\">{N(s.Passed)}</tspan><tspan font-size=\"13\" fill=\"{p.Muted}\"> / {N(s.Total)} passed · {P(s.PassedPercent)}%</tspan></text>");

        if (s.RecentPasses > 0)
            Text(sb, x1, 78, $"+{N(s.RecentPasses)} in 30d", o.Accent, 12, Mono, anchor: "end", weight: 700);

        Bar(sb, x0, 90, colW, 12, s, p, o.Accent, "bcb");
        Legend(sb, x0, 124, colW, s, p, o.Accent);

        sb.Append(CultureInfo.InvariantCulture,
            $"<line x1=\"{x0}\" y1=\"140\" x2=\"{x1}\" y2=\"140\" stroke=\"{p.Line}\" stroke-width=\"1\"/>");

        if (s.Mode is null) ModeCells(sb, x0, colW, s, p);
        else BandCells(sb, x0, colW, s, p, o.Accent);

        return (Close(sb), w, h);
    }

    // ---- slim: 880 x 92 ------------------------------------------------------------

    private static (string, int, int) Slim(BadgeSnapshot s, BadgeOptions o)
    {
        const int w = 880, h = 92;
        var p = BadgePalette.For(o.Theme);
        var sb = new StringBuilder(2048);

        Open(sb, w, h, p, o, 12);
        Ring(sb, 50, 46, 24, 7, s.PassedPercent, p, o.Accent, 15, 8, showCaption: false);

        const int x0 = 96, x1 = 856, colW = x1 - x0;
        var scope = s.Mode is null ? "ranked maps" : BadgePalette.ModeName(s.Mode.Value) + " maps";

        sb.Append(CultureInfo.InvariantCulture,
            $"<text x=\"{x0}\" y=\"38\" font-family=\"{Display}\"><tspan font-size=\"19\" font-weight=\"700\" fill=\"{p.Ink}\">{N(s.Passed)}</tspan><tspan font-size=\"11.5\" fill=\"{p.Muted}\"> / {N(s.Total)} {X(scope)} passed · {P(s.PassedPercent)}%</tspan></text>");

        Text(sb, x1, 38, "OSU!PERSONALTRACKER", p.Muted, 9.5, Mono, anchor: "end", spacing: 0.1, opacity: 0.8);

        Bar(sb, x0, 50, colW, 10, s, p, o.Accent, "bcs");
        Legend(sb, x0, 80, colW, s, p, o.Accent, size: 10);

        return (Close(sb), w, h);
    }

    // ---- shared pieces -------------------------------------------------------------

    private static void Open(StringBuilder sb, int w, int h, BadgePalette p, BadgeOptions o, double radius)
    {
        var glowX = o.Layout == BadgeLayout.Slim ? 50 : 90;
        var glowY = o.Layout == BadgeLayout.Slim ? 46 : 96;

        sb.Append(CultureInfo.InvariantCulture,
            $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{w}\" height=\"{h}\" viewBox=\"0 0 {w} {h}\" role=\"img\" aria-label=\"osu!PersonalTracker completion badge\">");

        sb.Append(CultureInfo.InvariantCulture,
            $"<defs><clipPath id=\"card\"><rect x=\"0\" y=\"0\" width=\"{w}\" height=\"{h}\" rx=\"{F(radius)}\"/></clipPath>" +
            $"<radialGradient id=\"glow\" cx=\"0\" cy=\"0\" r=\"1\" gradientUnits=\"userSpaceOnUse\" gradientTransform=\"translate({glowX} {glowY}) scale({F(h * 1.6)})\">" +
            $"<stop offset=\"0\" stop-color=\"{o.Accent}\" stop-opacity=\"0.20\"/><stop offset=\"1\" stop-color=\"{o.Accent}\" stop-opacity=\"0\"/></radialGradient></defs>");

        sb.Append(CultureInfo.InvariantCulture,
            $"<g clip-path=\"url(#card)\"><rect x=\"0\" y=\"0\" width=\"{w}\" height=\"{h}\" fill=\"{p.Card}\"/>" +
            $"<rect x=\"0\" y=\"0\" width=\"{w}\" height=\"{h}\" fill=\"url(#glow)\"/>" +
            $"<rect x=\"0\" y=\"0\" width=\"4\" height=\"{h}\" fill=\"{o.Accent}\"/></g>");

        // Stroke last and inset by half a pixel, or the rounded edge smears at 1x.
        sb.Append(CultureInfo.InvariantCulture,
            $"<rect x=\"0.5\" y=\"0.5\" width=\"{w - 1}\" height=\"{h - 1}\" rx=\"{F(radius)}\" fill=\"none\" stroke=\"{p.Line}\" stroke-width=\"1\"/>");
    }

    private static string Close(StringBuilder sb) => sb.Append("</svg>").ToString();

    private static void Ring(StringBuilder sb, double cx, double cy, double r, double sw,
        double percent, BadgePalette p, string accent, double size, double capSize, bool showCaption = true)
    {
        var c = 2 * Math.PI * r;
        var offset = c * (1 - Math.Clamp(percent, 0, 100) / 100.0);

        sb.Append(CultureInfo.InvariantCulture,
            $"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\" fill=\"none\" stroke=\"{p.Alt}\" stroke-width=\"{F(sw)}\"/>");

        if (percent > 0)
            sb.Append(CultureInfo.InvariantCulture,
                $"<circle cx=\"{F(cx)}\" cy=\"{F(cy)}\" r=\"{F(r)}\" fill=\"none\" stroke=\"{accent}\" stroke-width=\"{F(sw)}\" stroke-linecap=\"round\" stroke-dasharray=\"{F(c)}\" stroke-dashoffset=\"{F(offset)}\" transform=\"rotate(-90 {F(cx)} {F(cy)})\"/>");

        // Floored, not rounded: a ring reading 100% at 99.6 would be a lie the headline
        // beside it then contradicts. The exact figure is spelled out there anyway.
        var label = percent > 0 && percent < 1 ? "&lt;1" : ((int)Math.Floor(percent)).ToString(CultureInfo.InvariantCulture);
        var baseline = showCaption ? cy + size * 0.22 : cy + size * 0.36;

        sb.Append(CultureInfo.InvariantCulture,
            $"<text x=\"{F(cx)}\" y=\"{F(baseline)}\" text-anchor=\"middle\" font-family=\"{Display}\" font-weight=\"700\" font-size=\"{F(size)}\" fill=\"{p.Ink}\">{label}<tspan font-size=\"{F(capSize)}\" fill=\"{p.Muted}\">%</tspan></text>");

        if (showCaption)
            Text(sb, cx, baseline + capSize + 5, "PASSED", p.Muted, 8.5, Mono, anchor: "middle", spacing: 0.16);
    }

    /// <summary>Passed / attempted / untouched, clipped into one rounded track.</summary>
    private static void Bar(StringBuilder sb, double x, double y, double w, double h,
        BadgeSnapshot s, BadgePalette p, string accent, string clipId)
    {
        sb.Append(CultureInfo.InvariantCulture,
            $"<defs><clipPath id=\"{clipId}\"><rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" rx=\"{F(h / 2)}\"/></clipPath></defs>" +
            $"<g clip-path=\"url(#{clipId})\"><rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"{F(h)}\" fill=\"{p.Alt}\"/>");

        var passed = Span(w, s.Passed, s.Total);
        var attempted = Span(w, s.Attempted, s.Total);

        if (passed > 0)
            sb.Append(CultureInfo.InvariantCulture,
                $"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(passed)}\" height=\"{F(h)}\" fill=\"{accent}\"/>");
        if (attempted > 0)
            sb.Append(CultureInfo.InvariantCulture,
                $"<rect x=\"{F(x + passed)}\" y=\"{F(y)}\" width=\"{F(attempted)}\" height=\"{F(h)}\" fill=\"{p.Warn}\"/>");

        sb.Append("</g>");
    }

    private static void Legend(StringBuilder sb, double x, double y, double w,
        BadgeSnapshot s, BadgePalette p, string accent, double size = 10.5)
    {
        var step = w / 3;
        Chip(sb, x, y, accent, $"Passed {N(s.Passed)}", p, size);
        Chip(sb, x + step, y, p.Warn, $"Attempted {N(s.Attempted)}", p, size);
        Chip(sb, x + step * 2, y, p.Alt, $"Untouched {N(s.Untouched)}", p, size);
    }

    private static void Chip(StringBuilder sb, double x, double y, string colour, string label,
        BadgePalette p, double size)
    {
        // The untouched swatch is the track colour, which on the light theme is a hair
        // off the card — without an outline the third legend entry loses its marker.
        var outline = colour == p.Alt ? $" stroke=\"{p.Line}\" stroke-width=\"1\"" : "";
        sb.Append(CultureInfo.InvariantCulture,
            $"<rect x=\"{F(x)}\" y=\"{F(y - 8)}\" width=\"8\" height=\"8\" rx=\"2\" fill=\"{colour}\"{outline}/>");
        Text(sb, x + 14, y, label, p.Muted, size, Mono);
    }

    /// <summary>Per-mode strip on the combined badge: name, pass rate, mini bar.</summary>
    private static void ModeCells(StringBuilder sb, double x, double w, BadgeSnapshot s, BadgePalette p)
    {
        var rows = s.Modes.OrderBy(m => m.Mode).ToList();
        if (rows.Count == 0) return;
        var cw = w / rows.Count;

        for (var i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            var cx = x + cw * i;

            Text(sb, cx, 160, BadgePalette.ModeShort(r.Mode).ToUpperInvariant(), p.Muted, 9.5, Mono, spacing: 0.12);
            sb.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{F(cx)}\" y=\"180\" font-family=\"{Display}\"><tspan font-size=\"15\" font-weight=\"700\" fill=\"{p.Ink}\">{P(r.PassedPercent)}</tspan>" +
                $"<tspan font-size=\"10\" fill=\"{p.Muted}\">% · {N(r.Passed)}/{N(r.Total)}</tspan></text>");
            MiniBar(sb, cx, 188, cw - 18, r.PassedPercent, p, BadgePalette.ModeColour(r.Mode));
        }
    }

    /// <summary>Star bands on a single-mode badge — where the wall is, at a glance.</summary>
    private static void BandCells(StringBuilder sb, double x, double w, BadgeSnapshot s, BadgePalette p, string accent)
    {
        if (s.Bands.Count == 0) return;
        var cw = w / s.Bands.Count;

        for (var i = 0; i < s.Bands.Count; i++)
        {
            var b = s.Bands[i];
            var cx = x + cw * i;
            var pct = b.Total == 0 ? 0 : b.Passed * 100.0 / b.Total;

            Text(sb, cx, 160, b.Label, p.Muted, 9.5, Mono, spacing: 0.04);
            sb.Append(CultureInfo.InvariantCulture,
                $"<text x=\"{F(cx)}\" y=\"180\" font-family=\"{Display}\"><tspan font-size=\"14\" font-weight=\"700\" fill=\"{(b.Total == 0 ? p.Muted : p.Ink)}\">{(b.Total == 0 ? "—" : P(pct))}</tspan>" +
                $"<tspan font-size=\"9.5\" fill=\"{p.Muted}\">{(b.Total == 0 ? "" : "%")}</tspan></text>");
            MiniBar(sb, cx, 188, cw - 14, pct, p, accent);
        }
    }

    private static void MiniBar(StringBuilder sb, double x, double y, double w, double percent,
        BadgePalette p, string colour)
    {
        sb.Append(CultureInfo.InvariantCulture,
            $"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(w)}\" height=\"3\" rx=\"1.5\" fill=\"{p.Alt}\"/>");

        var fill = w * Math.Clamp(percent, 0, 100) / 100.0;
        if (fill > 0)
            sb.Append(CultureInfo.InvariantCulture,
                $"<rect x=\"{F(x)}\" y=\"{F(y)}\" width=\"{F(Math.Max(fill, 2))}\" height=\"3\" rx=\"1.5\" fill=\"{colour}\"/>");
    }

    private static void Text(StringBuilder sb, double x, double y, string value, string fill,
        double size, string family, string anchor = "start", int weight = 400,
        double spacing = 0, double opacity = 1)
    {
        var track = spacing == 0 ? "" : $" letter-spacing=\"{F(spacing * size)}\"";
        var fade = opacity >= 1 ? "" : $" opacity=\"{F(opacity)}\"";

        sb.Append(CultureInfo.InvariantCulture,
            $"<text x=\"{F(x)}\" y=\"{F(y)}\" font-family=\"{family}\" font-size=\"{F(size)}\" font-weight=\"{weight}\" fill=\"{fill}\" text-anchor=\"{anchor}\"{track}{fade}>{X(value)}</text>");
    }

    /// <summary>A segment under 2px reads as absent, which is a different claim.</summary>
    private static double Span(double full, int part, int total)
    {
        if (total == 0 || part <= 0) return 0;
        return Math.Max(full * part / total, 2);
    }

    private static string N(int v) => v.ToString("N0", CultureInfo.InvariantCulture);
    private static string P(double v) => v.ToString("F1", CultureInfo.InvariantCulture);
    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static string X(string v) => v
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&apos;");
}
