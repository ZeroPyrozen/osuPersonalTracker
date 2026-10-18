using OsuTracker.Web.Data.Entities;
using OsuTracker.Web.Services;
using OsuTracker.Web.Tests.Infrastructure;

namespace OsuTracker.Web.Tests.Services;

public sealed class BrowseQueryServiceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<TestDatabase> CatalogueAsync()
    {
        var db = await TestDatabase.CreateAsync();
        await db.SeedAsync(x =>
        {
            x.Beatmapsets.Add(Seed.Set(1, title: "Freedom Dive", artist: "xi"));
            x.Beatmapsets.Add(Seed.Set(2, title: "Galaxy Collapse", artist: "Kurokotei"));
            for (var i = 1; i <= 6; i++) x.Beatmaps.Add(Seed.Map(i, i <= 3 ? 1 : 2, stars: i));
        });
        return db;
    }

    [Fact]
    public async Task A_search_past_sqlites_expression_depth_still_answers()
    {
        await using var db = await CatalogueAsync();
        var browse = new BrowseQueryService(db.Factory);

        var result = await browse.QueryAsync(new BrowseFilter
        {
            Status = BrowseStatus.All,
            Search = string.Join(' ', Enumerable.Repeat("a", 1200))
        }, Ct);

        Assert.True(result.Total >= 0);
    }

    [Fact]
    public async Task Thousands_of_terms_on_a_request_thread_do_not_take_the_process_down()
    {
        await using var db = await CatalogueAsync();
        var browse = new BrowseQueryService(db.Factory);

        // A thread-pool thread, as a request or circuit callback is: its stack is a
        // fraction of the main thread's, which is where the overflow was found.
        var result = await Task.Run(() => browse.QueryAsync(new BrowseFilter
        {
            Status = BrowseStatus.All,
            Search = string.Join(' ', Enumerable.Repeat("a", 5000))
        }, Ct), Ct);

        Assert.True(result.Total >= 0);
    }

    [Theory]
    [InlineData("stars>nan")]
    [InlineData("bpm=infinity")]
    [InlineData("ar<1e999")]
    public async Task Non_finite_numbers_answer_instead_of_failing_the_page(string search)
    {
        await using var db = await CatalogueAsync();
        var browse = new BrowseQueryService(db.Factory);

        var result = await browse.QueryAsync(new BrowseFilter { Status = BrowseStatus.All, Search = search }, Ct);

        Assert.Equal(0, result.Total);
    }

    [Fact]
    public async Task A_page_far_past_the_end_returns_no_rows_rather_than_wrapping_to_the_first()
    {
        await using var db = await CatalogueAsync();
        var browse = new BrowseQueryService(db.Factory);

        var result = await browse.QueryAsync(new BrowseFilter { Status = BrowseStatus.All, Page = 50_000_000 }, Ct);

        Assert.Empty(result.Rows);
        Assert.Equal(6, result.Total);
    }

    [Fact]
    public async Task Ordinary_searches_still_filter()
    {
        await using var db = await CatalogueAsync();
        var browse = new BrowseQueryService(db.Factory);

        var result = await browse.QueryAsync(new BrowseFilter { Status = BrowseStatus.All, Search = "galaxy stars>=5" }, Ct);

        Assert.Equal([5L, 6L], result.Rows.Select(r => r.BeatmapId));
    }
}
