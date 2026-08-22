using System.Globalization;
using OsuTracker.Web.Data.Entities;

namespace OsuTracker.Web.Badges;

public enum BadgeLayout { Banner, Slim }

public enum BadgeTheme { Dark, Light }

/// <summary>
/// Everything the renderer needs that the caller chose, parsed once from the query
/// string. Parsing is total: an unknown value falls back to the default rather than
/// 400-ing, because a badge lives inside an &lt;img&gt; tag where an error is invisible.
/// </summary>
public sealed record BadgeOptions(
    GameMode? Mode,
    BadgeLayout Layout,
    BadgeTheme Theme,
    string Accent)
{
    public static BadgeOptions Parse(string? mode, string? layout, string? theme, string? accent)
    {
        var m = ParseMode(mode);
        return new BadgeOptions(
            m,
            layout?.ToLowerInvariant() is "slim" ? BadgeLayout.Slim : BadgeLayout.Banner,
            theme?.ToLowerInvariant() is "light" ? BadgeTheme.Light : BadgeTheme.Dark,
            ResolveAccent(accent, m));
    }

    /// <summary>null means "every mode combined", which is the default view.</summary>
    public static GameMode? ParseMode(string? s) => s?.ToLowerInvariant() switch
    {
        "osu" or "standard" or "std" or "0" => GameMode.Osu,
        "taiko" or "1" => GameMode.Taiko,
        "fruits" or "catch" or "ctb" or "2" => GameMode.Fruits,
        "mania" or "3" => GameMode.Mania,
        _ => null
    };

    /// <summary>
    /// A named mode, a literal #rrggbb, or the colour of whichever mode is on show.
    /// Hex is validated rather than trusted: the value lands inside an SVG attribute,
    /// so anything unrecognised has to be dropped, not escaped and passed through.
    /// </summary>
    private static string ResolveAccent(string? raw, GameMode? mode)
    {
        var v = raw?.Trim();
        if (!string.IsNullOrEmpty(v))
        {
            if (ParseMode(v) is { } named) return BadgePalette.ModeColour(named);
            if (v is "gold") return "#E9D27A";
            if (v is "mint") return "#3FB57A";
            if (IsHex(v)) return v.StartsWith('#') ? v : "#" + v;
        }
        return mode is null ? BadgePalette.ModeColour(GameMode.Osu) : BadgePalette.ModeColour(mode.Value);
    }

    private static bool IsHex(string v)
    {
        var body = v.StartsWith('#') ? v[1..] : v;
        return (body.Length is 3 or 6)
            && body.All(c => char.IsAsciiHexDigit(c));
    }

    /// <summary>Stable across requests, so it can seed an ETag.</summary>
    public string CacheKey => string.Create(CultureInfo.InvariantCulture,
        $"{Mode?.ToString() ?? "all"}|{Layout}|{Theme}|{Accent}");

    public string ModeSlug => Mode switch
    {
        GameMode.Osu => "osu",
        GameMode.Taiko => "taiko",
        GameMode.Fruits => "fruits",
        GameMode.Mania => "mania",
        _ => "all"
    };
}

/// <summary>
/// The two badge palettes, kept as literals rather than CSS variables: the SVG is
/// served as a standalone image, so nothing from app.css reaches it.
/// </summary>
public sealed record BadgePalette(
    string Card, string Alt, string Line, string Ink, string Muted, string Warn)
{
    public static readonly BadgePalette Dark =
        new("#1C1826", "#2A2436", "#322B42", "#EDE9F5", "#948CA8", "#E3A93C");

    public static readonly BadgePalette Light =
        new("#FFFFFF", "#EAE6F1", "#DCD6E6", "#221C2E", "#6E6683", "#C08420");

    public static BadgePalette For(BadgeTheme t) => t == BadgeTheme.Light ? Light : Dark;

    public static string ModeColour(GameMode m) => m switch
    {
        GameMode.Osu => "#F0559C",
        GameMode.Taiko => "#F2764B",
        GameMode.Fruits => "#9BD64A",
        GameMode.Mania => "#7B8CF5",
        _ => "#948CA8"
    };

    public static string ModeName(GameMode m) => m switch
    {
        GameMode.Osu => "osu!",
        GameMode.Taiko => "osu!taiko",
        GameMode.Fruits => "osu!catch",
        GameMode.Mania => "osu!mania",
        _ => m.ToString()
    };

    public static string ModeShort(GameMode m) => m switch
    {
        GameMode.Osu => "osu!",
        GameMode.Taiko => "taiko",
        GameMode.Fruits => "catch",
        GameMode.Mania => "mania",
        _ => m.ToString()
    };
}
