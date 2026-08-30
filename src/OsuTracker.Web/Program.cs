using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using OsuTracker.Web.Badges;
using OsuTracker.Web.Components;
using OsuTracker.Web.Data;
using OsuTracker.Web.OsuApi;
using OsuTracker.Web.Services;
using OsuTracker.Web.Sync;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents(o =>
{
    // Every page here is interactive, so each visitor costs a server-side circuit — and
    // the app is anonymous and publicly reachable. The defaults (100 circuits retained
    // for 3 minutes after they disconnect) are sized for a server, not for a Pi that is
    // also running a desktop.
    //
    // Retention exists so a blip does not lose your place. It costs little here: this
    // app deliberately keeps its state in the URL, so a dropped circuit reloads into the
    // same view rather than losing anything.
    o.DisconnectedCircuitMaxRetained = 20;
    o.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(2);

    // Never on: it would put exception detail in the browser of whoever caused it.
    o.DetailedErrors = false;
});

// ---- behind the Funnel ------------------------------------------------------
// Tailscale Funnel proxies every public request to 127.0.0.1, so without this the whole
// internet arrives as one client: rate limits would bucket strangers together with the
// LAN, and nothing downstream could tell them apart. Loopback is the only hop trusted,
// and only one of them, so a forwarded header from further out cannot spoof its way in.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.ForwardLimit = 1;
    o.KnownProxies.Clear();
    o.KnownProxies.Add(IPAddress.Loopback);
    o.KnownProxies.Add(IPAddress.IPv6Loopback);
});

// ---- rate limiting ----------------------------------------------------------
// Only the badge endpoints. They are the anonymous, publicly embedded, CPU-shaped part
// of the app; the pages behind them ride a Blazor circuit whose SignalR traffic a naive
// limiter would sever mid-session.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = (ctx, _) =>
    {
        ctx.HttpContext.Response.Headers.RetryAfter = "60";
        return ValueTask.CompletedTask;
    };

    // Retaining fewer disconnected circuits bounds the pool an abandoned session leaves
    // behind, but nothing bounds circuits that are simply held open. Meter the handshake
    // that creates them instead: /_blazor/negotiate is a plain POST that happens once per
    // page load, so capping it slows mass circuit creation without touching the WebSocket
    // an established session is already using. Everything else is explicitly unlimited.
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var path = ctx.Request.Path;
        if (!path.StartsWithSegments("/_blazor")
            || path.Value?.EndsWith("/negotiate", StringComparison.OrdinalIgnoreCase) != true)
            return RateLimitPartition.GetNoLimiter("unmetered");

        var client = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetTokenBucketLimiter(
            $"negotiate|{client}",
            _ => new TokenBucketRateLimiterOptions
            {
                // Enhanced navigation reuses one circuit across link clicks, so a person
                // negotiates on full page loads only — nowhere near this.
                TokenLimit = 20,
                TokensPerPeriod = 20,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    o.AddPolicy(BadgeEndpoints.RateLimitPolicy, ctx =>
    {
        // Post-ForwardedHeaders this is the real remote client, so one abusive caller
        // cannot spend everyone else's budget.
        var client = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var cost = BadgeEndpoints.CostOf(ctx);
        var allowance = BadgeEndpoints.AllowancePerMinute(cost);

        // Separate buckets, so exhausting the dear one cannot lock a viewer out of the
        // cheap badge they actually came for.
        return RateLimitPartition.GetTokenBucketLimiter(
            $"{cost}|{client}",
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = allowance,
                TokensPerPeriod = allowance,
                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});

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

// First in the pipeline: everything after this point, rate limiting included, has to
// see the real client address rather than the proxy's.
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRateLimiter();
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
