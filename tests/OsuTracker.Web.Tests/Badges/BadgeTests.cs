using System.Globalization;
using System.Xml.Linq;
using OsuTracker.Web.Badges;
using OsuTracker.Web.Data.Entities;
using OsuTracker.Web.Services;

namespace OsuTracker.Web.Tests.Badges;

public sealed class BadgeTests
{
    private static readonly BadgeOptions Banner = BadgeOptions.Parse(null, null, null, null);

    private static BadgeSnapshot Snapshot(DateTimeOffset at) => new(
        UserId: 4242, Mode: null, Total: 100, Passed: 40, Attempted: 10, Untouched: 50, RecentPasses: 3,
        Modes: [new ModeProgress(GameMode.Fruits, 100, 40, 10, 50)],
        Bands: [],
        GeneratedAt: at);

    [Fact]
    public void The_etag_holds_through_the_day_so_an_unchanged_badge_stays_cached()
    {
        var morning = new DateTimeOffset(2026, 10, 18, 1, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            BadgeService.ETag(Snapshot(morning), Banner),
            BadgeService.ETag(Snapshot(morning.AddHours(20)), Banner));
    }

    [Fact]
    public void The_etag_changes_with_the_date_the_banner_draws()
    {
        // Same numbers, different UPDATED line: a strong ETag has to tell them apart, and
        // the PNG cache is keyed on it, so otherwise the first day's raster is served on.
        var day = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var (svg1, _, _) = BadgeRenderer.Render(Snapshot(day), Banner);
        var (svg2, _, _) = BadgeRenderer.Render(Snapshot(day.AddDays(1)), Banner);
        Assert.NotEqual(svg1, svg2);

        Assert.NotEqual(
            BadgeService.ETag(Snapshot(day), Banner),
            BadgeService.ETag(Snapshot(day.AddDays(1)), Banner));
    }

    [Theory]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    [InlineData("fa-IR")]
    [InlineData("id-ID")]
    public void The_banner_is_the_same_document_whatever_culture_the_server_runs_in(string culture)
    {
        var at = new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);
        var expected = BadgeRenderer.Render(Snapshot(at), Banner).Svg;

        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo(culture);
            var (svg, _, _) = BadgeRenderer.Render(Snapshot(at), Banner);

            Assert.Contains("UPDATED 2026-10-18", svg);
            Assert.Equal(expected, svg);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(null, "banner")]
    [InlineData("fruits", "banner")]
    [InlineData(null, "slim")]
    public void Every_layout_renders_well_formed_svg(string? mode, string layout)
    {
        var options = BadgeOptions.Parse(mode, layout, "light", "#abc");
        var snap = Snapshot(DateTimeOffset.UtcNow) with
        {
            Mode = options.Mode,
            Bands = [new StarBand("0–2★", 10, 5, 2, 3), new StarBand("7★+", 0, 0, 0, 0)]
        };

        var (svg, width, height) = BadgeRenderer.Render(snap, options);

        var root = XDocument.Parse(svg).Root!;
        Assert.Equal("svg", root.Name.LocalName);
        Assert.Equal(width.ToString(CultureInfo.InvariantCulture), root.Attribute("width")!.Value);
        Assert.Equal(height.ToString(CultureInfo.InvariantCulture), root.Attribute("height")!.Value);
    }

    [Theory]
    [InlineData("\"/><script>alert(1)</script>")]
    [InlineData("url(javascript:alert(1))")]
    [InlineData("#12345g")]
    public void An_accent_that_is_not_a_colour_falls_back_to_the_mode_colour(string accent)
    {
        var options = BadgeOptions.Parse("taiko", null, null, accent);

        Assert.Equal(BadgePalette.ModeColour(GameMode.Taiko), options.Accent);
    }
}
