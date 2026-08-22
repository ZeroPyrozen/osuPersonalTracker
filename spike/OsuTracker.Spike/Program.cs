using Microsoft.Extensions.Configuration;

namespace OsuTracker.Spike;

public sealed record SpikeOptions
{
    public required string ClientId { get; init; }
    public required string ClientSecret { get; init; }
    public long? UserId { get; init; }
    public string Mode { get; init; } = "fruits";      // osu | taiko | fruits | mania
    public long? NfBeatmapId { get; init; }            // a map you passed with NoFail / low acc
    public long? QualifiedBeatmapId { get; init; }     // a map you played BEFORE it was ranked
    public long? BeatmapsetId { get; init; }
    public bool SaveResponses { get; init; } = true;
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var config = new ConfigurationBuilder()
            .AddUserSecrets<SpikeOptions>(optional: true)
            .AddEnvironmentVariables("OSU_")
            .AddCommandLine(args, new Dictionary<string, string>
            {
                ["--client-id"] = "ClientId",
                ["--client-secret"] = "ClientSecret",
                ["--user-id"] = "UserId",
                ["--mode"] = "Mode",
                ["--nf-beatmap"] = "NfBeatmapId",
                ["--qualified-beatmap"] = "QualifiedBeatmapId",
                ["--beatmapset"] = "BeatmapsetId",
            })
            .Build();

        var clientId = config["ClientId"];
        var clientSecret = config["ClientSecret"];
        var userIdRaw = config["UserId"];

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
        {
            PrintUsage();
            return 2;
        }

        // UserId is optional: P1/P2/P6/P7 validate auth and the catalog without it.
        long? userId = long.TryParse(userIdRaw, out var uid) ? uid : null;

        var opt = new SpikeOptions
        {
            ClientId = clientId,
            ClientSecret = clientSecret,
            UserId = userId,
            Mode = config["Mode"] ?? "fruits",
            NfBeatmapId = ParseLong(config["NfBeatmapId"]),
            QualifiedBeatmapId = ParseLong(config["QualifiedBeatmapId"]),
            BeatmapsetId = ParseLong(config["BeatmapsetId"]),
        };

        var outputDir = Path.Combine(AppContext.BaseDirectory, "probe-output");
        Directory.CreateDirectory(outputDir);

        Console.WriteLine("osu!PersonalTracker — Phase 1 spike");
        Console.WriteLine(new string('=', 74));
        Console.WriteLine($"user      : {opt.UserId?.ToString() ?? "(not set — P3/P4/P5 will be skipped)"}");
        Console.WriteLine($"mode      : {opt.Mode}");
        Console.WriteLine($"nf map    : {opt.NfBeatmapId?.ToString() ?? "(not set — P4 will be skipped)"}");
        Console.WriteLine($"pre-rank  : {opt.QualifiedBeatmapId?.ToString() ?? "(not set — P5 falls back to nf map)"}");
        Console.WriteLine($"responses : {outputDir}");

        var deep = args.Contains("--deep");

        using var api = new OsuApiClient(opt.ClientId, opt.ClientSecret);
        var probes = new Probes(api, opt, outputDir);

        try
        {
            Console.WriteLine();
            Console.WriteLine("Authenticating (client credentials)...");
            await api.AuthenticateAsync(cts.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine();
            Console.WriteLine("AUTH FAILED — nothing else can run.");
            Console.WriteLine(ex.Message);
            Console.WriteLine();
            Console.WriteLine("Check that the client id/secret came from https://osu.ppy.sh/home/account/edit");
            Console.WriteLine("(OAuth section) and that you copied the secret, not the id, into --client-secret.");
            return 1;
        }

        var rawIdx = Array.IndexOf(args, "--raw");
        if (rawIdx >= 0 && rawIdx + 1 < args.Length)
        {
            var r = await api.GetAsync(args[rawIdx + 1], cts.Token);
            Console.WriteLine($"{r.StatusLine}");
            Console.WriteLine(r.RawBody);
            return 0;
        }

        if (args.Contains("--modhunt"))
        {
            await ModHunt.RunAsync(api, opt, cts.Token);
            return 0;
        }

        if (args.Contains("--census"))
        {
            await Census.RunAsync(api, opt, cts.Token);
            return 0;
        }

        if (deep)
        {
            await DeepProbe.RunAsync(api, opt, cts.Token);
            return 0;
        }

        // Probes are ordered so the two decisive ones (P4, P5) run after their
        // prerequisites, but each is independent — a failure never blocks the rest.
        var steps = new (string Name, Func<CancellationToken, Task> Run)[]
        {
            ("P1", probes.P1_SanityAsync),
            ("P0", probes.P0_DiscoverAsync),
            ("P2", probes.P2_RankedDateAsync),
            ("P3", probes.P3_MostPlayedAsync),
            ("P4", probes.P4_NonLeaderboardScoreAsync),
            ("P5", probes.P5_AllScoresAsync),
            ("P6", probes.P6_CatalogAsync),
            ("P7", probes.P7_RateLimitAsync),
        };

        foreach (var (name, run) in steps)
        {
            if (cts.IsCancellationRequested) break;
            try
            {
                await run(cts.Token);
                await Task.Delay(1100, cts.Token); // stay far under 60 req/min
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"  !! {name} threw: {ex.Message}");
            }
        }

        PrintSummary(probes.Findings);
        return probes.Findings.Any(f => f.Verdict == Verdict.Fail) ? 3 : 0;
    }

    private static void PrintSummary(IReadOnlyList<Finding> findings)
    {
        Console.WriteLine();
        Console.WriteLine(new string('=', 74));
        Console.WriteLine("VERDICT");
        Console.WriteLine(new string('=', 74));

        foreach (var f in findings)
            Console.WriteLine($"{f.Marker} {f.Id,-4} {f.Question}");

        Console.WriteLine();
        Console.WriteLine("The two that decide the design:");

        var p4 = findings.FirstOrDefault(f => f.Id == "P4");
        var p5 = findings.FirstOrDefault(f => f.Id == "P5");

        Console.WriteLine($"  Rule 4 (any pass counts, NF included) : {Describe(p4)}");
        Console.WriteLine($"  Rule 5 (only post-rank plays count)   : {Describe(p5)}");
        Console.WriteLine();

        if (p4?.Verdict == Verdict.Pass && p5?.Verdict == Verdict.Pass)
        {
            Console.WriteLine("Both green — build the design exactly as written.");
        }
        else
        {
            Console.WriteLine("Not both green — read the detail above. The fallback in each case is to lean on");
            Console.WriteLine("the recent-scores poller and mark historical coverage as approximate in the UI.");
        }

        Console.WriteLine();
        Console.WriteLine("Detail:");
        foreach (var f in findings.Where(f => f.Verdict != Verdict.Skipped))
        {
            Console.WriteLine($"  {f.Id}: {f.Detail}");
            Console.WriteLine();
        }
    }

    private static string Describe(Finding? f) => f is null
        ? "not run"
        : f.Verdict switch
        {
            Verdict.Pass => "IMPLEMENTABLE",
            Verdict.Fail => "BLOCKED — fallback required",
            Verdict.Inconclusive => "INCONCLUSIVE — re-run with a better test map",
            _ => "SKIPPED — supply the beatmap id"
        };

    private static long? ParseLong(string? s) => long.TryParse(s, out var v) ? v : null;

    private static void PrintUsage()
    {
        Console.WriteLine("""
        osu!PersonalTracker — Phase 1 spike

        Answers the two questions the design depends on:
          Rule 4  Does the API expose passes that are NOT on the leaderboard (NoFail, low acc)?
          Rule 5  Does the all-scores endpoint exist, with timestamps, so pre-rank plays
                  can be excluded without false negatives?

        REQUIRED
          --client-id      <id>       from https://osu.ppy.sh/home/account/edit (OAuth section)
          --client-secret  <secret>
          --user-id        <id>       your numeric osu! user id

        STRONGLY RECOMMENDED
          --nf-beatmap        <id>    a difficulty you passed with NoFail and/or low accuracy,
                                      which is NOT on your leaderboard. Without it P4 is skipped
                                      and Rule 4 stays unanswered.
          --qualified-beatmap <id>    a difficulty you played BEFORE it was ranked.

        OPTIONAL
          --mode <osu|taiko|fruits|mania>   default: fruits
          --beatmapset <id>                 explicit set to probe for ranked_date

        Prefer user secrets over passing the secret on the command line:
          dotnet user-secrets set ClientId <id>
          dotnet user-secrets set ClientSecret <secret>
          dotnet user-secrets set UserId <your-user-id>

        Then just:
          dotnet run -- --nf-beatmap 123456 --qualified-beatmap 234567
        """);
    }
}
