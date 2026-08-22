using System.Text.Json;

namespace OsuTracker.Spike;

/// <summary>
/// Follow-up probes for the two questions P0/P4/P5 left partly open:
///   A. Does the user have any NoFail (or otherwise deep-leaderboard) pass, and is it returned?
///   B. Does /all ever return more than one score, or is it always the single best?
/// Run with --deep.
/// </summary>
public static class DeepProbe
{
    public static async Task RunAsync(OsuApiClient api, SpikeOptions opt, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("DEEP PROBE");
        Console.WriteLine(new string('=', 74));

        var rows = new List<(long Id, int Count, string Title)>();
        for (var offset = 0; offset < 1000; offset += 100)
        {
            var r = await api.GetAsync($"/users/{opt.UserId}/beatmapsets/most_played?limit=100&offset={offset}", ct);
            if (!r.Ok || r.Json is null) break;
            var arr = r.Json.Value;
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) break;

            foreach (var row in arr.EnumerateArray())
            {
                if (!row.TryGetProperty("beatmap", out var bm)) continue;
                if (Str(bm, "mode") != opt.Mode) continue;
                var id = Int(row, "beatmap_id") ?? Int(bm, "id");
                if (id is null) continue;
                var title = row.TryGetProperty("beatmapset", out var set) ? Str(set, "title") ?? "?" : "?";
                rows.Add((id.Value, Int(row, "count") ?? 0, title));
            }
            if (arr.GetArrayLength() < 100) break;
            await Task.Delay(350, ct);
        }

        Console.WriteLine($"{rows.Count} {opt.Mode}-native maps in play history");
        Console.WriteLine();

        // ---- A. hunt the TAIL: low playcount maps are where marginal / NF passes live
        Console.WriteLine("A. Scanning low-playcount maps for NoFail and deep-leaderboard passes");
        Console.WriteLine(new string('-', 74));
        Console.WriteLine($"{"beatmap",9} {"plays",6} {"rank",5} {"pos",7}  mods");

        var tail = rows.OrderBy(r => r.Count).Take(30).ToList();
        var positions = new List<int>();
        var nfFound = 0;
        var noScore = 0;
        var scored = 0;

        foreach (var c in tail)
        {
            if (ct.IsCancellationRequested) break;
            var r = await api.GetAsync($"/beatmaps/{c.Id}/scores/users/{opt.UserId}?mode={opt.Mode}", ct);

            if (!r.Ok || r.Json is null) { noScore++; Console.WriteLine($"{c.Id,9} {c.Count,6} {"--",5} {"--",7}  (no score: {(int)r.Status})"); await Task.Delay(350, ct); continue; }

            scored++;
            var j = r.Json.Value;
            var sc = j.TryGetProperty("score", out var se) ? se : j;
            var mods = ModsOf(sc);
            var pos = j.TryGetProperty("position", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : (int?)null;
            if (pos is not null) positions.Add(pos.Value);
            var isNf = mods.Any(m => m.Equals("NF", StringComparison.OrdinalIgnoreCase));
            if (isNf) nfFound++;

            Console.WriteLine($"{c.Id,9} {c.Count,6} {Str(sc, "rank"),5} {pos?.ToString() ?? "none",7}  {(mods.Count > 0 ? string.Join("+", mods) : "NoMod")}{(isNf ? "   <-- NoFail" : "")}");
            await Task.Delay(350, ct);
        }

        Console.WriteLine();
        Console.WriteLine($"  scored={scored}  no-score={noScore}  NoFail={nfFound}");
        if (positions.Count > 0)
            Console.WriteLine($"  leaderboard positions: min={positions.Min()} max={positions.Max()} median={Median(positions)}");
        Console.WriteLine();

        // ---- B. does /all ever return more than one score?
        Console.WriteLine("B. /all multiplicity across the most-played maps");
        Console.WriteLine(new string('-', 74));
        Console.WriteLine($"{"beatmap",9} {"plays",6} {"scores",7}  timestamps");

        foreach (var c in rows.OrderByDescending(r => r.Count).Take(8))
        {
            if (ct.IsCancellationRequested) break;
            var r = await api.GetAsync($"/beatmaps/{c.Id}/scores/users/{opt.UserId}/all?mode={opt.Mode}", ct);
            if (!r.Ok || r.Json is null) { Console.WriteLine($"{c.Id,9} {c.Count,6} {(int)r.Status,7}"); await Task.Delay(350, ct); continue; }

            var j = r.Json.Value;
            var scores = j.TryGetProperty("scores", out var s) && s.ValueKind == JsonValueKind.Array ? s
                       : (j.ValueKind == JsonValueKind.Array ? j : default);
            var n = scores.ValueKind == JsonValueKind.Array ? scores.GetArrayLength() : 0;

            var stamps = new List<string>();
            if (n > 0)
                foreach (var sc in scores.EnumerateArray().Take(3))
                    if ((Str(sc, "ended_at") ?? Str(sc, "created_at")) is { } t) stamps.Add(t[..10]);

            Console.WriteLine($"{c.Id,9} {c.Count,6} {n,7}  {string.Join(", ", stamps)}");
            await Task.Delay(350, ct);
        }
    }

    private static double Median(List<int> xs)
    {
        var s = xs.OrderBy(x => x).ToList();
        return s.Count % 2 == 1 ? s[s.Count / 2] : (s[s.Count / 2 - 1] + s[s.Count / 2]) / 2.0;
    }

    private static string? Str(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

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
