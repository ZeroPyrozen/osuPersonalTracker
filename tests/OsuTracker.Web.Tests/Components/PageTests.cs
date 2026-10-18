using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OsuTracker.Web.Components.Pages;
using OsuTracker.Web.Data.Entities;
using OsuTracker.Web.OsuApi;
using OsuTracker.Web.Services;
using OsuTracker.Web.Tests.Infrastructure;
using OsuTracker.Web.Tests.Services;

namespace OsuTracker.Web.Tests.Components;

public sealed class PageTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Waits until the factory has handed back <paramref name="contexts"/> contexts in total,
    /// then gives the component a moment to publish whatever those queries returned — the
    /// window in which a superseded load would overwrite the newer one.
    /// </summary>
    private static async Task SettleAsync<T>(IRenderedComponent<T> cut, GatedDbFactory gated, int contexts)
        where T : IComponent
    {
        cut.WaitForState(() => gated.Disposed >= contexts, Wait);
        await Task.Delay(150);
        await cut.InvokeAsync(() => { });
    }

    // ---- Browse ------------------------------------------------------------------------

    private static long[] BrowseRowIds(IRenderedComponent<Browse> cut) =>
        cut.FindAll("a.t-title").Select(a => long.Parse(a.GetAttribute("href")!.Split('/')[^1])).Order().ToArray();

    [Fact]
    public async Task Browse_shows_the_newest_filter_even_when_an_older_query_finishes_last()
    {
        await using var db = await ProgressQueryServiceTests.LadderAsync();
        await using var ctx = new BunitContext();
        var gated = new GatedDbFactory(db);
        ctx.Services.AddSingleton(new BrowseQueryService(gated));
        var nav = ctx.Services.GetRequiredService<NavigationManager>();

        nav.NavigateTo("browse?status=passed");
        var cut = ctx.Render<Browse>();
        cut.WaitForAssertion(() => Assert.Equal([1L, 3L], BrowseRowIds(cut)), Wait);
        cut.WaitForState(() => gated.Disposed >= 2, Wait);   // counts + rows
        var settled = gated.Disposed;

        // Two clicks in quick succession; each navigation is one query for its rows.
        gated.HoldCalls(0, 1);
        nav.NavigateTo("browse?status=attempted");
        nav.NavigateTo("browse?status=untouched");
        cut.WaitForState(() => gated.Held == 2, Wait);

        gated.Release(1);   // the newer navigation answers first
        cut.WaitForAssertion(() => Assert.Equal([5L], BrowseRowIds(cut)), Wait);
        gated.Release(0);   // and the older one last
        await SettleAsync(cut, gated, settled + 2);

        Assert.Equal([5L], BrowseRowIds(cut));
    }

    // ---- ModeDetail --------------------------------------------------------------------

    /// <summary>osu! 1 map; taiko 2 maps, 1 passed; mania 10 sets of 2, one of each passed.</summary>
    private static async Task<TestDatabase> ModesAsync()
    {
        var db = await TestDatabase.CreateAsync();
        await db.SeedAsync(x =>
        {
            x.Beatmapsets.Add(Seed.Set(1, title: "Osu Set"));
            x.Beatmaps.Add(Seed.Map(1, 1, GameMode.Osu));

            x.Beatmapsets.Add(Seed.Set(2, title: "Taiko Set"));
            x.Beatmaps.Add(Seed.Map(21, 2, GameMode.Taiko));
            x.Beatmaps.Add(Seed.Map(22, 2, GameMode.Taiko));
            x.Scores.Add(Seed.Score(21, DateTimeOffset.UtcNow.AddDays(-90)));

            // Ten half-done mania sets, so the nearly-done list has more than one batch.
            for (var s = 0; s < 10; s++)
            {
                var set = 100 + s;
                x.Beatmapsets.Add(Seed.Set(set, title: $"Mania Set {s}"));
                x.Beatmaps.Add(Seed.Map(set * 10, set, GameMode.Mania));
                x.Beatmaps.Add(Seed.Map(set * 10 + 1, set, GameMode.Mania));
                x.Scores.Add(Seed.Score(set * 10, DateTimeOffset.UtcNow.AddDays(-90)));
            }
        });
        return db;
    }

    /// <summary>One mode load is five queries: ladder, bands, nearly-done, its count, recent passes.</summary>
    private const int QueriesPerModeLoad = 5;

    [Fact]
    public async Task A_mode_page_shows_the_newest_mode_even_when_an_older_load_finishes_last()
    {
        await using var db = await ModesAsync();
        await using var ctx = new BunitContext();
        var gated = new GatedDbFactory(db);
        ctx.Services.AddSingleton(new ProgressQueryService(gated));

        var cut = ctx.Render<ModeDetail>(p => p.Add(x => x.Mode, "osu"));
        cut.WaitForState(() => gated.Disposed >= QueriesPerModeLoad, Wait);
        var settled = gated.Disposed;

        // Hold the taiko load's second query, its star bands, so the switch to mania lands
        // while a taiko result is still on its way back.
        gated.HoldCalls(1);
        cut.Render(p => p.Add(x => x.Mode, "taiko"));
        cut.WaitForState(() => gated.Held == 1, Wait);

        cut.Render(p => p.Add(x => x.Mode, "mania"));
        cut.WaitForAssertion(() => Assert.Contains("10 of 20 passed", cut.Markup), Wait);
        gated.Release(0);
        await SettleAsync(cut, gated, settled + 2 * QueriesPerModeLoad);

        Assert.Contains("osu!mania", cut.Find("h1").TextContent);
        Assert.Contains("10 of 20 passed", cut.Markup);
        Assert.DoesNotContain("1 of 2 passed", cut.Markup);
    }

    [Fact]
    public async Task Show_more_cannot_append_one_modes_sets_to_another_modes_list()
    {
        await using var db = await ModesAsync();
        await using var ctx = new BunitContext();
        var gated = new GatedDbFactory(db);
        ctx.Services.AddSingleton(new ProgressQueryService(gated));

        var cut = ctx.Render<ModeDetail>(p => p.Add(x => x.Mode, "mania"));
        cut.WaitForAssertion(() => Assert.Contains("show 2 more", cut.Markup), Wait);
        cut.WaitForState(() => gated.Disposed >= QueriesPerModeLoad, Wait);
        var settled = gated.Disposed;

        gated.HoldCalls(0);
        cut.Find("div.nearly-more button").Click();
        cut.WaitForState(() => gated.Held == 1, Wait);

        cut.Render(p => p.Add(x => x.Mode, "taiko"));
        cut.WaitForAssertion(() => Assert.Contains("Taiko Set", cut.Markup), Wait);
        gated.Release(0);
        await SettleAsync(cut, gated, settled + 1 + QueriesPerModeLoad);

        Assert.DoesNotContain("Mania Set", cut.Markup);
    }

    // ---- SyncStatus --------------------------------------------------------------------

    [Fact]
    public async Task The_sync_page_budget_reads_the_configured_rate()
    {
        await using var db = await TestDatabase.CreateAsync();
        await using var ctx = new BunitContext();
        var options = Options.Create(new OsuApiOptions { RequestsPerMinute = 120 });
        using var limiter = new OsuRateLimiter(options);
        for (var i = 0; i < 60; i++) await limiter.WaitAsync(Xunit.TestContext.Current.CancellationToken);
        ctx.Services.AddSingleton(db.Factory);
        ctx.Services.AddSingleton(options);
        ctx.Services.AddSingleton(limiter);

        var cut = ctx.Render<SyncStatus>();

        cut.WaitForAssertion(() => Assert.Contains("60 / 120 available this minute", cut.Markup), Wait);
        Assert.Equal(10, cut.FindAll("div.budget i.used").Count);
    }
}
