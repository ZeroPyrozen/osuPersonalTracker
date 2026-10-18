using OsuTracker.Web.Data.Entities;
using OsuTracker.Web.Services;

namespace OsuTracker.Web.Tests.Services;

public sealed class BrowseSearchTests
{
    // ---- bounds: the box is public input, and every term becomes a nested WHERE -------

    [Fact]
    public void A_flood_of_words_is_cut_to_a_bounded_number_of_terms()
    {
        var parsed = BrowseSearch.Parse(string.Join(' ', Enumerable.Repeat("a", 5000)));

        Assert.Equal(BrowseSearch.MaxTerms, parsed.Words.Count);
    }

    [Fact]
    public void The_bound_counts_filters_as_well_as_words()
    {
        var parsed = BrowseSearch.Parse(string.Join(' ', Enumerable.Repeat("artist=x bpm>1", 1000)));

        Assert.Equal(BrowseSearch.MaxTerms, parsed.Fields.Count + parsed.Numbers.Count);
    }

    [Fact]
    public void An_oversized_search_is_truncated_before_it_becomes_a_pattern()
    {
        var parsed = BrowseSearch.Parse(new string('x', 10_000));

        Assert.Equal(BrowseSearch.MaxLength, Assert.Single(parsed.Words).Length);
    }

    // ---- numbers: anything SQLite cannot hold as a literal stays text ------------------

    [Theory]
    [InlineData("stars>nan")]
    [InlineData("od=NaN")]
    [InlineData("bpm=infinity")]
    [InlineData("ar<1e999")]
    [InlineData("length>-Infinity")]
    public void Non_finite_numbers_are_searched_as_text(string term)
    {
        var parsed = BrowseSearch.Parse(term);

        Assert.Empty(parsed.Numbers);
        Assert.Equal([term], parsed.Words);
    }

    // ---- dates: a value that names no year, month or day is not a filter --------------

    [Theory]
    [InlineData("ranked=2019-13")]
    [InlineData("ranked=2019-00")]
    [InlineData("ranked=2019-1x")]
    [InlineData("ranked=2019-02-30")]
    [InlineData("ranked=2019-02-31x")]
    [InlineData("ranked=2019-01-01-01")]
    public void Malformed_dates_are_searched_as_text(string term)
    {
        var parsed = BrowseSearch.Parse(term);

        Assert.Empty(parsed.Numbers);
        Assert.Equal([term], parsed.Words);
    }

    [Fact]
    public void A_month_becomes_its_half_open_range()
    {
        var parsed = BrowseSearch.Parse("ranked=2019-02");

        Assert.Collection(parsed.Numbers,
            n => Assert.Equal(new NumFilter(NumField.Ranked, SearchOp.Gte, Unix(2019, 2, 1)), n),
            n => Assert.Equal(new NumFilter(NumField.Ranked, SearchOp.Lt, Unix(2019, 3, 1)), n));
    }

    // ---- existing behaviour, pinned so the fixes above cannot move it -----------------

    [Fact]
    public void Stars_equals_means_the_whole_band()
    {
        var parsed = BrowseSearch.Parse("stars=6");

        Assert.Equal(
            [new NumFilter(NumField.Stars, SearchOp.Gte, 6), new NumFilter(NumField.Stars, SearchOp.Lt, 7)],
            parsed.Numbers);
    }

    [Fact]
    public void Length_reads_minutes_and_seconds()
    {
        var parsed = BrowseSearch.Parse("length<2:30");

        Assert.Equal([new NumFilter(NumField.Length, SearchOp.Lt, 150)], parsed.Numbers);
    }

    [Fact]
    public void Quoted_values_keep_their_spaces_and_mixed_terms_all_apply()
    {
        var parsed = BrowseSearch.Parse("""camellia artist="Toby Fox" bpm>=180 status=approved""");

        Assert.Equal(["camellia"], parsed.Words);
        Assert.Equal([new TextFilter(TextField.Artist, "Toby Fox")], parsed.Fields);
        Assert.Equal([new NumFilter(NumField.Bpm, SearchOp.Gte, 180)], parsed.Numbers);
        Assert.Equal(BeatmapStatus.Approved, parsed.Status);
    }

    [Fact]
    public void A_term_that_names_no_filter_stays_a_word()
    {
        var parsed = BrowseSearch.Parse("10:00 100% stars>soon");

        Assert.Equal(["10:00", "100%", "stars>soon"], parsed.Words);
        Assert.Empty(parsed.Numbers);
    }

    private static double Unix(int y, int m, int d) =>
        new DateTimeOffset(y, m, d, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
}
