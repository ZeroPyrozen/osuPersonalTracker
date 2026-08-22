using System.Text.Json;

namespace OsuTracker.Spike;

/// <summary>Pages most_played to exhaustion and tallies by native mode — sizes the backfill queue.</summary>
public static class Census
{
    public static async Task RunAsync(OsuApiClient api, SpikeOptions opt, CancellationToken ct)
    {
        Console.WriteLine();
        Console.WriteLine("PLAYCOUNT CENSUS — sizing the backfill queue");
        Console.WriteLine(new string('=', 74));

        var byMode = new Dictionary<string, int>();
        var total = 0;
        var requests = 0;

        for (var offset = 0; ; offset += 100)
        {
            var r = await api.GetAsync($"/users/{opt.UserId}/beatmapsets/most_played?limit=100&offset={offset}", ct);
            requests++;
            if (!r.Ok || r.Json is null) { Console.WriteLine($"  stopped at offset {offset}: {r.StatusLine}"); break; }

            var arr = r.Json.Value;
            if (arr.ValueKind != JsonValueKind.Array) break;
            var n = arr.GetArrayLength();
            if (n == 0) break;

            foreach (var row in arr.EnumerateArray())
            {
                total++;
                if (!row.TryGetProperty("beatmap", out var bm)) continue;
                var m = bm.TryGetProperty("mode", out var mv) && mv.ValueKind == JsonValueKind.String ? mv.GetString()! : "?";
                byMode[m] = byMode.GetValueOrDefault(m) + 1;
            }

            if (offset % 1000 == 0) Console.WriteLine($"  ...{total} rows");
            if (n < 100) break;
            await Task.Delay(900, ct);
        }

        Console.WriteLine();
        Console.WriteLine($"  total distinct beatmaps ever played : {total}");
        Console.WriteLine($"  requests used                       : {requests}");
        Console.WriteLine();
        foreach (var kv in byMode.OrderByDescending(k => k.Value))
            Console.WriteLine($"    {kv.Key,-8} {kv.Value,6}   <- backfill queue for this mode");
        Console.WriteLine();
        Console.WriteLine($"  Backfill cost at 60 req/min: ~{total / 60.0:F0} minutes for ALL modes.");
    }
}
