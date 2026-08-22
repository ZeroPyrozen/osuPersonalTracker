using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OsuTracker.Web.Data;
using OsuTracker.Web.Data.Entities;
using OsuTracker.Web.Services;

namespace OsuTracker.Web.Sync;

/// <summary>
/// CLI entry points, so a long catalogue sweep can run headless without booting the
/// web host. Phase 3 will surface the same jobs through the Sync screen.
/// </summary>
public static class SyncCommands
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args, CancellationToken ct)
    {
        var command = args.Length > 1 ? args[1].ToLowerInvariant() : "";
        var resume = args.Contains("--resume");
        int? maxPages = null;
        var mp = Array.FindIndex(args, a => a == "--max-pages");
        if (mp >= 0 && mp + 1 < args.Length && int.TryParse(args[mp + 1], out var mpv)) maxPages = mpv;

        int? limit = null;
        var li = Array.FindIndex(args, a => a == "--limit");
        if (li >= 0 && li + 1 < args.Length && int.TryParse(args[li + 1], out var liv)) limit = liv;

        GameMode? onlyMode = null;
        var mi = Array.FindIndex(args, a => a == "--mode");
        if (mi >= 0 && mi + 1 < args.Length)
            onlyMode = args[mi + 1].ToLowerInvariant() switch
            {
                "osu" or "standard" => GameMode.Osu,
                "taiko" => GameMode.Taiko,
                "fruits" or "catch" => GameMode.Fruits,
                "mania" => GameMode.Mania,
                _ => null
            };

        var dbFactory = services.GetRequiredService<IDbContextFactory<TrackerDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            await db.Database.MigrateAsync(ct);

        switch (command)
        {
            case "catalog":
                await services.GetRequiredService<CatalogSyncJob>().RunAsync(resume, ct, maxPages);
                await PrintDenominatorsAsync(services, ct);
                return 0;

            case "playcounts":
                await services.GetRequiredService<PlayCountSyncJob>().RunAsync(resume, ct);
                await PrintProgressAsync(services, ct);
                return 0;

            case "scores":
                await services.GetRequiredService<ScoreBackfillJob>().RunAsync(resume, onlyMode, limit, ct);
                await PrintProgressAsync(services, ct);
                return 0;

            case "recent":
                await services.GetRequiredService<RecentScoresJob>().RunAsync(ct);
                await PrintProgressAsync(services, ct);
                return 0;

            case "status":
                await PrintStatusAsync(services, ct);
                return 0;

            default:
                Console.WriteLine("""
                Usage: dotnet run -- sync <command> [--resume]

                  catalog     Sweep ranked + approved and fill Beatmapset / Beatmap.
                              --resume continues from the stored cursor after a crash.
                  playcounts  Page most_played and fill PlayCount. Defines the backfill queue.
                  recent      Poll the 24h window for each mode. 4 requests; run this on a
                              timer once backfill is done and the tracker stays current.
                  scores      Backfill scores over the queue. --mode <osu|taiko|catch|mania>
                              narrows it, --limit N caps it, --resume continues.
                  status      Show job state, denominators and current progress.
                """);
                return 2;
        }
    }

    private static async Task PrintStatusAsync(IServiceProvider services, CancellationToken ct)
    {
        var dbFactory = services.GetRequiredService<IDbContextFactory<TrackerDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        Console.WriteLine();
        Console.WriteLine("Jobs");
        Console.WriteLine(new string('-', 70));
        var jobs = await db.SyncJobs.ToListAsync(ct);
        if (jobs.Count == 0) Console.WriteLine("  (none have run)");
        foreach (var j in jobs)
            Console.WriteLine($"  {j.Name,-16} {j.State,-10} done={j.ItemsDone,-8} last={j.LastSuccessAt?.ToString("u") ?? "never"}{(j.Error is null ? "" : $"  ERROR: {j.Error}")}");

        await PrintProgressAsync(services, ct);
    }

    private static async Task PrintDenominatorsAsync(IServiceProvider services, CancellationToken ct)
    {
        var progress = services.GetRequiredService<ProgressQueryService>();
        var denoms = await progress.GetDenominatorsAsync(ct);

        var dbFactory = services.GetRequiredService<IDbContextFactory<TrackerDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var sets = await db.Beatmapsets.CountAsync(s => s.Status == BeatmapStatus.Ranked || s.Status == BeatmapStatus.Approved, ct);
        var departed = await db.Beatmaps.CountAsync(b => b.Status == BeatmapStatus.Departed, ct);

        // Invariant culture: a locale that uses "." as the thousands separator turns
        // 142,560 into "142.560", which reads as a decimal and is genuinely confusing.
        static string N(int v) => v.ToString("N0", CultureInfo.InvariantCulture);

        var approvedMaps = await db.Beatmaps.CountAsync(b => b.Status == BeatmapStatus.Approved, ct);

        Console.WriteLine();
        Console.WriteLine("Denominators — ranked + approved, native difficulties only");
        Console.WriteLine(new string('-', 70));
        var total = 0;
        foreach (var mode in Enum.GetValues<GameMode>())
        {
            var n = denoms.GetValueOrDefault(mode);
            total += n;
            Console.WriteLine($"  {mode,-8} {N(n),10}");
        }
        Console.WriteLine(new string('-', 70));
        Console.WriteLine($"  {"TOTAL",-8} {N(total),10}   across {N(sets)} beatmapsets");
        Console.WriteLine($"  {"of which",-8} {N(approvedMaps),10}   are Approved (status 2)");
        if (departed > 0) Console.WriteLine($"  {"departed",-8} {N(departed),10}   (kept, not counted)");
        Console.WriteLine();
    }

    /// <summary>Denominators plus the three-rung ladder, once playcounts exist.</summary>
    private static async Task PrintProgressAsync(IServiceProvider services, CancellationToken ct)
    {
        static string N(int v) => v.ToString("N0", CultureInfo.InvariantCulture);

        var rows = await services.GetRequiredService<ProgressQueryService>().GetAllModesAsync(ct);

        Console.WriteLine();
        Console.WriteLine("Progress — ranked + approved, native difficulties only");
        Console.WriteLine(new string('-', 70));
        Console.WriteLine($"  {"mode",-8} {"total",10} {"passed",9} {"attempt",9} {"untouched",11} {"%",7}");
        Console.WriteLine(new string('-', 70));

        int t = 0, p = 0, a = 0;
        foreach (var r in rows)
        {
            t += r.Total; p += r.Passed; a += r.Attempted;
            Console.WriteLine($"  {r.Mode,-8} {N(r.Total),10} {N(r.Passed),9} {N(r.Attempted),9} {N(r.Untouched),11} {r.PassedPercent.ToString("F2", CultureInfo.InvariantCulture),6}%");
        }
        Console.WriteLine(new string('-', 70));
        var pct = t == 0 ? 0 : p * 100.0 / t;
        Console.WriteLine($"  {"TOTAL",-8} {N(t),10} {N(p),9} {N(a),9} {N(t - p - a),11} {pct.ToString("F2", CultureInfo.InvariantCulture),6}%");
        Console.WriteLine();
    }
}

