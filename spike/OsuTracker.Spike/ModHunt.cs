using System.Text.Json;

namespace OsuTracker.Spike;

/// <summary>
/// Answers: does this account have ANY NoFail score, and is supporter status
/// relevant to what the tracker needs? Run with --modhunt.
/// </summary>
public static class ModHunt
{
    private static readonly string[] Modes = ["fruits", "osu", "taiko", "mania"];

    public static async Task RunAsync(OsuApiClient api, SpikeOptions opt, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("MOD HUNT — is NoFail present, and does supporter matter?");
        Console.WriteLine(new string('=', 74));

        // --- 1. supporter status straight from the profile
        var u = await api.GetAsync($"/users/{opt.UserId}", ct);
        if (u.Ok && u.Json is not null)
        {
            var j = u.Json.Value;
            Console.WriteLine($"  username      : {Str(j, "username")}");
            Console.WriteLine($"  is_supporter  : {Bool(j, "is_supporter")?.ToString() ?? "?"}");
            Console.WriteLine($"  support_level : {Int(j, "support_level")?.ToString() ?? "?"}");
            Console.WriteLine($"  join_date     : {Str(j, "join_date")}");
        }
        else Console.WriteLine($"  profile fetch: {u.StatusLine}");
        Console.WriteLine();

        // --- 2. top plays per mode: cheapest broad look at mod usage
        Console.WriteLine("Mod usage across top plays (2 requests per mode)");
        Console.WriteLine(new string('-', 74));

        var globalMods = new Dictionary<string, int>();
        foreach (var mode in Modes)
        {
            var tally = new Dictionary<string, int>();
            var n = 0;
            foreach (var type in new[] { "best", "recent" })
            {
                var r = await api.GetAsync($"/users/{opt.UserId}/scores/{type}?mode={mode}&limit=100&include_fails=1", ct);
                if (r.Ok && r.Json is { ValueKind: JsonValueKind.Array } arr)
                    foreach (var s in arr.EnumerateArray())
                    {
                        n++;
                        foreach (var m in ModsOf(s))
                        {
                            tally[m] = tally.GetValueOrDefault(m) + 1;
                            globalMods[m] = globalMods.GetValueOrDefault(m) + 1;
                        }
                    }
                await Task.Delay(900, ct);
            }
            var top = tally.OrderByDescending(k => k.Value).Take(8).Select(k => $"{k.Key}x{k.Value}");
            Console.WriteLine($"  {mode,-7} {n,4} scores   {(tally.Count == 0 ? "(none)" : string.Join("  ", top))}");
        }

        Console.WriteLine();

        // --- 3. wide sample of played maps per mode, hunting NF specifically
        Console.WriteLine("Scanning played maps for NoFail (sampled across playcount range)");
        Console.WriteLine(new string('-', 74));

        var nfHits = new List<string>();

        foreach (var mode in Modes)
        {
            var rows = new List<(long Id, int Count)>();
            for (var offset = 0; offset < 400; offset += 100)
            {
                var r = await api.GetAsync($"/users/{opt.UserId}/beatmapsets/most_played?limit=100&offset={offset}", ct);
                if (!r.Ok || r.Json is not { ValueKind: JsonValueKind.Array } arr) break;
                if (arr.GetArrayLength() == 0) break;
                foreach (var row in arr.EnumerateArray())
                {
                    if (!row.TryGetProperty("beatmap", out var bm)) continue;
                    if (Str(bm, "mode") != mode) continue;
                    var id = Int(row, "beatmap_id") ?? Int(bm, "id");
                    if (id is not null) rows.Add((id.Value, Int(row, "count") ?? 0));
                }
                await Task.Delay(700, ct);
            }

            // spread the sample across the playcount range, not just the tail
            var sample = rows.OrderByDescending(r => r.Count)
                             .Where((_, i) => i % Math.Max(1, rows.Count / 15) == 0)
                             .Take(15).ToList();

            var found = 0; var scored = 0;
            foreach (var c in sample)
            {
                if (ct.IsCancellationRequested) break;
                var r = await api.GetAsync($"/beatmaps/{c.Id}/scores/users/{opt.UserId}?mode={mode}", ct);
                if (r.Ok && r.Json is not null)
                {
                    scored++;
                    var sc = r.Json.Value.TryGetProperty("score", out var se) ? se : r.Json.Value;
                    var mods = ModsOf(sc);
                    if (mods.Any(m => m.Equals("NF", StringComparison.OrdinalIgnoreCase)))
                    {
                        found++;
                        nfHits.Add($"{mode} beatmap {c.Id} ({c.Count} plays) {Str(sc, "rank")} {string.Join("+", mods)}");
                    }
                }
                await Task.Delay(700, ct);
            }
            Console.WriteLine($"  {mode,-7} sampled {sample.Count,3} of {rows.Count,5} played   scored={scored,3}   NoFail={found}");
        }

        Console.WriteLine();
        Console.WriteLine(new string('=', 74));
        if (nfHits.Count > 0)
        {
            Console.WriteLine("NoFail scores FOUND and returned by the API:");
            foreach (var h in nfHits) Console.WriteLine($"  {h}");
            Console.WriteLine();
            Console.WriteLine("=> Rule 4 is DIRECTLY confirmed. No supporter needed.");
        }
        else
        {
            Console.WriteLine("No NoFail score found in any sample.");
            Console.WriteLine($"Mods this account actually uses: {string.Join(", ", globalMods.OrderByDescending(k => k.Value).Take(10).Select(k => $"{k.Key}({k.Value})"))}");
            Console.WriteLine();
            Console.WriteLine("=> Most likely you simply do not play with NF, rather than the API hiding it.");
        }
    }

    private static string? Str(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    private static bool? Bool(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static List<string> ModsOf(JsonElement score)
    {
        var result = new List<string>();
        if (score.ValueKind != JsonValueKind.Object) return result;
        if (!score.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Array) return result;
        foreach (var m in mods.EnumerateArray())
        {
            if (m.ValueKind == JsonValueKind.String) result.Add(m.GetString()!);
            else if (m.ValueKind == JsonValueKind.Object && Str(m, "acronym") is { } a) result.Add(a);
        }
        return result;
    }
}
