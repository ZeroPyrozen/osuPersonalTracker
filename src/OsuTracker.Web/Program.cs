using Microsoft.EntityFrameworkCore;
using OsuTracker.Web.Badges;
using OsuTracker.Web.Components;
using OsuTracker.Web.Data;
using OsuTracker.Web.OsuApi;
using OsuTracker.Web.Services;
using OsuTracker.Web.Sync;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// ---- data -------------------------------------------------------------------
// WAL so a long sync never blocks the UI from reading.
var dbPath = Path.Combine(builder.Environment.ContentRootPath, "tracker.db");
builder.Services.AddDbContextFactory<TrackerDbContext>(o =>
    o.UseSqlite($"Data Source={dbPath};Cache=Shared;Pooling=True"));

// ---- osu! api ---------------------------------------------------------------
builder.Services.Configure<OsuApiOptions>(builder.Configuration.GetSection(OsuApiOptions.SectionName));
// x-api-version is not optional: without it the API serves legacy-shaped responses,
// the /all scores endpoint 404s, score ids come back as legacy ids, and mods lose the
// lazer-only markers. Rule 5 depends on /all, so this header is load-bearing.
builder.Services.AddHttpClient("osu-api", c =>
{
    c.Timeout = TimeSpan.FromSeconds(60);
    c.DefaultRequestHeaders.Add("x-api-version", "20240529");
});
builder.Services.AddHttpClient("osu-token", c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<OsuTokenProvider>();
builder.Services.AddSingleton<OsuRateLimiter>();
builder.Services.AddSingleton<OsuApiClient>();

// ---- sync + queries ---------------------------------------------------------
builder.Services.AddSingleton<CatalogSyncJob>();
builder.Services.AddSingleton<PlayCountSyncJob>();
builder.Services.AddSingleton<ScoreBackfillJob>();
builder.Services.AddSingleton<RecentScoresJob>();
builder.Services.AddSingleton<ProgressQueryService>();
builder.Services.AddSingleton<BrowseQueryService>();
builder.Services.AddSingleton<BadgeService>();

var app = builder.Build();

// Headless entry: `dotnet run -- sync catalog`. Runs the job and exits without
// starting Kestrel, so a multi-hour sweep does not need the web host up.
if (args.Length > 0 && args[0].Equals("sync", StringComparison.OrdinalIgnoreCase))
{
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    await EnableWalAsync(app.Services);
    return await SyncCommands.RunAsync(app.Services, args, cts.Token);
}

await using (var scope = app.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TrackerDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}
await EnableWalAsync(app.Services);

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseAntiforgery();
app.MapStaticAssets();
app.MapBadgeEndpoints();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
return 0;

static async Task EnableWalAsync(IServiceProvider services)
{
    var factory = services.GetRequiredService<IDbContextFactory<TrackerDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;");
}
