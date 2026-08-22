using System.Text.Json;

namespace OsuTracker.Spike;

public enum Verdict { Pass, Fail, Inconclusive, Skipped }

public sealed record Finding(string Id, string Question, Verdict Verdict, string Detail)
{
    public string Marker => Verdict switch
    {
        Verdict.Pass => "[ PASS ]",
        Verdict.Fail => "[ FAIL ]",
        Verdict.Inconclusive => "[  ??  ]",
        _ => "[ SKIP ]"
    };
}

public sealed class Probes(OsuApiClient api, SpikeOptions opt, string outputDir)
{
    private readonly List<Finding> _findings = [];
    private long? _discoveredScoredMap;
    private long? _discoveredNfMap;

    public IReadOnlyList<Finding> Findings => _findings;

    private void Add(string id, string q, Verdict v, string detail)
    {
        _findings.Add(new Finding(id, q, v, detail));
        Console.WriteLine($"  -> {v.ToString().ToUpperInvariant()}: {detail}");
    }

    private async Task SaveAsync(string name, ApiResult r)
    {
        if (!opt.SaveResponses || r.RawBody.Length == 0) return;
        var path = Path.Combine(outputDir, $"{name}.json");
        await File.WriteAllTextAsync(path, Prettify(r.RawBody));
    }

    private static string Prettify(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (JsonException) { return raw; }
    }

    // ---------------------------------------------------------------------
    // P1 — can we talk to the API at all, and does a beatmap carry its mode?
    // ---------------------------------------------------------------------
    public async Task P1_SanityAsync(CancellationToken ct)
    {
        Header("P1", "Auth works and a beatmap reports its native mode");

        var probeId = opt.NfBeatmapId ?? 129891; // FREEDOM DiVE [FOUR DIMENSIONS], a stable well-known id
        var r = await api.GetAsync($"/beatmaps/{probeId}", ct);
        await SaveAsync("p1-beatmap", r);
        Console.WriteLine($"  GET /beatmaps/{probeId} -> {r.StatusLine}");

        if (!r.Ok)
        {
            Add("P1", "API reachable", Verdict.Fail,
                $"{r.StatusLine}. {(r.Unauthorized ? "Client-credentials token was rejected for this endpoint." : "Unexpected status.")} Body: {Head(r.RawBody)}");
            return;
        }

        var j = r.Json!.Value;
        var mode = Str(j, "mode");
        var modeInt = Int(j, "mode_int");
        var stars = Dbl(j, "difficulty_rating");
        var status = Str(j, "status");
        var setId = Int(j, "beatmapset_id");

        Add("P1", "API reachable", Verdict.Pass,
            $"beatmap {probeId}: mode={mode} (mode_int={modeInt}), {stars:F2}*, status={status}, set={setId}");

        if (modeInt is null)
            Add("P1b", "Beatmap carries a native mode (Rule 2)", Verdict.Fail,
                "No mode_int on the beatmap object — converts-out cannot be implemented from this field.");
        else
            Add("P1b", "Beatmap carries a native mode (Rule 2)", Verdict.Pass,
                $"mode_int={modeInt} present. Rule 2 is implementable as a plain column.");
    }

    // ---------------------------------------------------------------------
    // P2 — does a beatmapset expose ranked_date? Rule 5 is dead without it.
    // ---------------------------------------------------------------------
    public async Task P2_RankedDateAsync(CancellationToken ct)
    {
        Header("P2", "Beatmapsets expose ranked_date (Rule 5 needs this)");

        var setId = opt.BeatmapsetId;
        if (setId is null)
        {
            var probeId = opt.NfBeatmapId ?? 129891;
            var bm = await api.GetAsync($"/beatmaps/{probeId}", ct);
            setId = bm.Ok ? Int(bm.Json!.Value, "beatmapset_id") : null;
        }

        if (setId is null)
        {
            Add("P2", "ranked_date available", Verdict.Skipped, "Could not resolve a beatmapset id to probe.");
            return;
        }

        var r = await api.GetAsync($"/beatmapsets/{setId}", ct);
        await SaveAsync("p2-beatmapset", r);
        Console.WriteLine($"  GET /beatmapsets/{setId} -> {r.StatusLine}");

        if (!r.Ok)
        {
            Add("P2", "ranked_date available", Verdict.Fail, $"{r.StatusLine}. Body: {Head(r.RawBody)}");
            return;
        }

        var j = r.Json!.Value;
        var rankedDate = Str(j, "ranked_date");
        var submitted = Str(j, "submitted_date");
        var status = Str(j, "status");
        var rankedInt = Int(j, "ranked");

        // Does the set list its beatmaps, each with its own mode? That is how the
        // catalog sync gets every difficulty in one pass.
        int? diffCount = null;
        var modes = new HashSet<string>();
        if (j.TryGetProperty("beatmaps", out var beatmaps) && beatmaps.ValueKind == JsonValueKind.Array)
        {
            diffCount = beatmaps.GetArrayLength();
            foreach (var b in beatmaps.EnumerateArray())
                if (Str(b, "mode") is { } m) modes.Add(m);
        }

        if (rankedDate is null)
            Add("P2", "ranked_date available (Rule 5)", Verdict.Fail,
                $"No ranked_date on the set (status={status}). Rule 5 cannot be enforced from this endpoint.");
        else
            Add("P2", "ranked_date available (Rule 5)", Verdict.Pass,
                $"ranked_date={rankedDate}, submitted={submitted}, status={status} (ranked={rankedInt})");

        Add("P2b", "Set lists all difficulties with modes", diffCount > 0 ? Verdict.Pass : Verdict.Inconclusive,
            diffCount > 0
                ? $"{diffCount} difficulties, modes present: {string.Join(", ", modes.OrderBy(x => x))}"
                : "Set object did not include a beatmaps[] array.");
    }

    // ---------------------------------------------------------------------
    // P3 — most_played: the backfill queue. Does it carry mode? timestamps?
    // ---------------------------------------------------------------------
    public async Task P3_MostPlayedAsync(CancellationToken ct)
    {
        Header("P3", "most_played shape (backfill queue + Attempted state)");

        if (opt.UserId is null)
        {
            Add("P3", "most_played readable", Verdict.Skipped, "No --user-id supplied.");
            return;
        }

        var r = await api.GetAsync($"/users/{opt.UserId}/beatmapsets/most_played?limit=5", ct);
        await SaveAsync("p3-most-played", r);
        Console.WriteLine($"  GET /users/{opt.UserId}/beatmapsets/most_played?limit=5 -> {r.StatusLine}");

        if (!r.Ok)
        {
            Add("P3", "most_played readable with guest token", Verdict.Fail,
                $"{r.StatusLine}. {(r.Unauthorized ? "Guest token may not reach this endpoint — the auth-code grant would be required." : "")} Body: {Head(r.RawBody)}");
            return;
        }

        var arr = r.Json!.Value;
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
        {
            Add("P3", "most_played readable", Verdict.Inconclusive,
                "Returned an empty array — is the user id correct, and does this account have plays?");
            return;
        }

        var first = arr[0];
        var count = Int(first, "count");
        var beatmapId = Int(first, "beatmap_id");

        Add("P3", "most_played readable with guest token", Verdict.Pass,
            $"{arr.GetArrayLength()} rows; first: beatmap {beatmapId}, count={count}");

        // The design assumes NO per-play timestamps here. Confirm that assumption.
        var tsKey = FindTimestampKey(first);
        if (tsKey is null)
            Add("P3b", "Playcounts carry timestamps (would let Rule 5 filter Attempted)", Verdict.Fail,
                "No timestamp field found. CONFIRMS the design: Attempted must stay approximate and be marked with a tilde in the UI.");
        else
            Add("P3b", "Playcounts carry timestamps", Verdict.Pass,
                $"Found '{tsKey}' — Rule 5 may be enforceable on Attempted after all. Worth revisiting the design.");

        // Does the playcount row tell us which ruleset the plays were in?
        var modeOnRow = Str(first, "mode") ?? (first.TryGetProperty("beatmap", out var bm) ? Str(bm, "mode") : null);
        if (first.TryGetProperty("beatmap", out var bmEl) && Str(bmEl, "mode") is { } nativeMode)
            Add("P3c", "Playcount rows distinguish the ruleset played", Verdict.Inconclusive,
                $"Nested beatmap.mode='{nativeMode}' is the map's NATIVE mode, not the mode you played it in. " +
                "Assume mode-blind playcounts (as designed); convert plays will inflate Attempted.");
        else
            Add("P3c", "Playcount rows distinguish the ruleset played", Verdict.Inconclusive,
                $"No ruleset field on the playcount row (mode={modeOnRow ?? "absent"}). Assume mode-blind, as designed.");
    }

    // ---------------------------------------------------------------------
    // P0 — find a real test map from the user's own play history, so P4/P5
    // do not depend on the user remembering one.
    // ---------------------------------------------------------------------
    public async Task P0_DiscoverAsync(CancellationToken ct)
    {
        Header("P0", $"Discover a played {opt.Mode}-native map to test P4/P5 against");

        if (opt.UserId is null || opt.NfBeatmapId is not null)
        {
            Add("P0", "Test map discovery", Verdict.Skipped, "Not needed.");
            return;
        }

        var candidates = new List<(long Id, int Count, string Title)>();

        for (var offset = 0; offset < 300 && candidates.Count < 40; offset += 100)
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
                candidates.Add((id.Value, Int(row, "count") ?? 0, title));
            }
            await Task.Delay(400, ct);
        }

        Console.WriteLine($"  {candidates.Count} {opt.Mode}-native maps found in play history");

        if (candidates.Count == 0)
        {
            Add("P0", "Test map discovery", Verdict.Fail,
                $"No {opt.Mode}-native maps in most_played. Try --mode osu, or pass --nf-beatmap explicitly.");
            return;
        }

        // Probe candidates for an actual score; prefer one with NoFail, since that is
        // precisely the score a leaderboard-gated endpoint would hide.
        var probed = 0;
        var withScore = 0;
        foreach (var c in candidates.OrderByDescending(c => c.Count).Take(20))
        {
            if (ct.IsCancellationRequested) break;
            var r = await api.GetAsync($"/beatmaps/{c.Id}/scores/users/{opt.UserId}?mode={opt.Mode}", ct);
            probed++;

            if (r.Ok && r.Json is not null)
            {
                withScore++;
                var sc = r.Json.Value.TryGetProperty("score", out var se) ? se : r.Json.Value;
                var mods = ModsOf(sc);
                _discoveredScoredMap ??= c.Id;
                Console.WriteLine($"  {c.Id,8} {c.Count,4}x {Str(sc, "rank"),-3} {(mods.Count > 0 ? string.Join("+", mods) : "NoMod"),-12} {Trim(c.Title, 30)}");
                if (mods.Any(m => m.Equals("NF", StringComparison.OrdinalIgnoreCase)))
                {
                    _discoveredNfMap = c.Id;
                    Console.WriteLine($"  ^^ NoFail score found — using this for P4");
                    break;
                }
            }
            else
            {
                Console.WriteLine($"  {c.Id,8} {c.Count,4}x {r.StatusLine,-22} {Trim(c.Title, 30)}");
            }
            await Task.Delay(400, ct);
        }

        Add("P0", "Test map discovery", withScore > 0 ? Verdict.Pass : Verdict.Fail,
            $"probed {probed} played maps, {withScore} returned a score" +
            (_discoveredNfMap is not null ? $"; NF score on beatmap {_discoveredNfMap}" : "; no NF score seen") +
            (withScore == 0 ? ". Every played map returned 404 — see P4." : "."));
    }

    // ---------------------------------------------------------------------
    // P4 — THE RULE 4 PROBE. Are non-leaderboard passes (NF, low acc) visible?
    // ---------------------------------------------------------------------
    public async Task P4_NonLeaderboardScoreAsync(CancellationToken ct)
    {
        Header("P4", "Non-leaderboard passes are visible (Rule 4 depends on this)");

        var mapId = opt.NfBeatmapId ?? _discoveredNfMap ?? _discoveredScoredMap;
        if (opt.UserId is null || mapId is null)
        {
            Add("P4", "Non-leaderboard pass visible", Verdict.Skipped,
                "Pass --nf-beatmap <id> — a map you passed with NoFail and/or low accuracy, that is NOT on your leaderboard.");
            return;
        }

        var path = $"/beatmaps/{mapId}/scores/users/{opt.UserId}?mode={opt.Mode}";
        var r = await api.GetAsync(path, ct);
        await SaveAsync("p4-user-score", r);
        Console.WriteLine($"  GET {path} -> {r.StatusLine}");

        if (r.NotFound)
        {
            Add("P4", "Non-leaderboard pass visible (Rule 4)", Verdict.Fail,
                "404 — no score returned. If you definitely passed this map in this mode, the endpoint is " +
                "leaderboard-gated and your NF passes are INVISIBLE to backfill. Fallback: the recent-scores " +
                "poller becomes the primary source and historical coverage stays approximate.");
            return;
        }

        if (!r.Ok)
        {
            Add("P4", "Non-leaderboard pass visible (Rule 4)", Verdict.Fail,
                $"{r.StatusLine}. {(r.Unauthorized ? "Guest token rejected — the auth-code grant may be required for score endpoints." : "")} Body: {Head(r.RawBody)}");
            return;
        }

        var j = r.Json!.Value;
        var score = j.TryGetProperty("score", out var s) ? s : j;

        var mods = ModsOf(score);
        var grade = Str(score, "rank");
        var acc = Dbl(score, "accuracy");
        var playedAt = Str(score, "ended_at") ?? Str(score, "created_at");
        var passed = Bool(score, "passed");
        var position = j.TryGetProperty("position", out var p) && p.ValueKind is JsonValueKind.Number
            ? p.GetInt32() : (int?)null;

        var modStr = mods.Count > 0 ? string.Join("+", mods) : "NoMod";
        var hasNf = mods.Any(m => m.Equals("NF", StringComparison.OrdinalIgnoreCase));

        Add("P4", "Non-leaderboard pass visible (Rule 4)", hasNf ? Verdict.Pass : Verdict.Inconclusive,
            $"score returned: {grade} {acc * 100:F2}% mods={modStr} passed={passed} played={playedAt} " +
            $"leaderboard_position={position?.ToString() ?? "none"}. " +
            (hasNf
                ? "NF score IS returned — Rule 4 is fully implementable."
                : "Score returned but without NF. Re-run against a map you passed with NoFail to settle this properly."));

        if (position is null && r.Ok)
            Add("P4b", "Endpoint returns scores off the leaderboard", Verdict.Pass,
                "No leaderboard position in the response, yet a score came back — strong evidence the endpoint is NOT leaderboard-gated.");
    }

    // ---------------------------------------------------------------------
    // P5 — THE RULE 5 PROBE. Does /all exist and return timestamped scores?
    // ---------------------------------------------------------------------
    public async Task P5_AllScoresAsync(CancellationToken ct)
    {
        Header("P5", "All-scores variant exists (Rule 5 needs it to avoid false negatives)");

        var target = opt.QualifiedBeatmapId ?? opt.NfBeatmapId ?? _discoveredNfMap ?? _discoveredScoredMap;
        if (opt.UserId is null || target is null)
        {
            Add("P5", "/all variant exists", Verdict.Skipped,
                "Pass --qualified-beatmap <id> (ideally a map you played BEFORE it ranked) or --nf-beatmap <id>.");
            return;
        }

        var path = $"/beatmaps/{target}/scores/users/{opt.UserId}/all?mode={opt.Mode}";
        var r = await api.GetAsync(path, ct);
        await SaveAsync("p5-all-scores", r);
        Console.WriteLine($"  GET {path} -> {r.StatusLine}");

        if (!r.Ok)
        {
            var v = r.NotFound ? Verdict.Fail : Verdict.Inconclusive;
            Add("P5", "/all variant exists (Rule 5)", v,
                $"{r.StatusLine}. Endpoint unavailable — fall back to the best-score endpoint and accept " +
                "false negatives where a pre-rank best score hides a valid post-rank pass. Body: " + Head(r.RawBody));
            return;
        }

        var j = r.Json!.Value;
        var scores = j.TryGetProperty("scores", out var sc) && sc.ValueKind == JsonValueKind.Array
            ? sc
            : (j.ValueKind == JsonValueKind.Array ? j : default);

        if (scores.ValueKind != JsonValueKind.Array)
        {
            Add("P5", "/all variant exists (Rule 5)", Verdict.Inconclusive,
                $"200 OK but no scores array found. Body: {Head(r.RawBody)}");
            return;
        }

        var n = scores.GetArrayLength();
        var stamps = new List<string>();
        foreach (var s in scores.EnumerateArray().Take(10))
            if ((Str(s, "ended_at") ?? Str(s, "created_at")) is { } t)
                stamps.Add($"{t} [{(ModsOf(s) is { Count: > 0 } m ? string.Join("+", m) : "NoMod")}]");

        var allStamped = stamps.Count == Math.Min(n, 10);

        Add("P5", "/all variant exists (Rule 5)", n > 0 && allStamped ? Verdict.Pass : Verdict.Inconclusive,
            $"{n} score(s) returned, {stamps.Count} with timestamps. " +
            (n > 1
                ? "Multiple scores available — Rule 5 can select the earliest qualifying pass. "
                : "Only one score returned here; try a map you have played many times to confirm multiplicity. ") +
            (stamps.Count > 0 ? "Samples: " + string.Join(" | ", stamps.Take(4)) : ""));
    }

    // ---------------------------------------------------------------------
    // P6 — catalog: can we page ranked, and does Approved come back too?
    // ---------------------------------------------------------------------
    public async Task P6_CatalogAsync(CancellationToken ct)
    {
        Header("P6", "Catalog search paginates and covers Ranked + Approved (Rule 3)");

        var r = await api.GetAsync("/beatmapsets/search?s=ranked", ct);
        await SaveAsync("p6-search", r);
        Console.WriteLine($"  GET /beatmapsets/search?s=ranked -> {r.StatusLine}");

        if (!r.Ok)
        {
            Add("P6", "Catalog search usable", Verdict.Fail, $"{r.StatusLine}. Body: {Head(r.RawBody)}");
            return;
        }

        var j = r.Json!.Value;
        var total = j.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : (int?)null;
        var cursor = Str(j, "cursor_string");
        var sets = j.TryGetProperty("beatmapsets", out var bs) && bs.ValueKind == JsonValueKind.Array ? bs : default;
        var pageSize = sets.ValueKind == JsonValueKind.Array ? sets.GetArrayLength() : 0;

        var statuses = new HashSet<string>();
        var rankedDates = 0;
        if (sets.ValueKind == JsonValueKind.Array)
            foreach (var s in sets.EnumerateArray())
            {
                if (Str(s, "status") is { } st) statuses.Add(st);
                if (Str(s, "ranked_date") is not null) rankedDates++;
            }

        Add("P6", "Catalog search usable", Verdict.Pass,
            $"total={total?.ToString() ?? "n/a"}, page={pageSize} sets, cursor_string={(cursor is null ? "ABSENT" : "present")}, " +
            $"statuses seen: {string.Join("/", statuses)}, {rankedDates}/{pageSize} carry ranked_date");

        if (cursor is null)
            Add("P6b", "Cursor pagination available", Verdict.Fail,
                "No cursor_string — deep pagination may be capped. Prefer seeding the catalog from a data dump.");

        // Rule 3 wants Approved as well; confirm it is reachable as its own query.
        var ra = await api.GetAsync("/beatmapsets/search?s=approved", ct);
        await SaveAsync("p6-search-approved", ra);
        Console.WriteLine($"  GET /beatmapsets/search?s=approved -> {ra.StatusLine}");

        if (ra.Ok)
        {
            var ja = ra.Json!.Value;
            var totalA = ja.TryGetProperty("total", out var ta) && ta.ValueKind == JsonValueKind.Number ? ta.GetInt32() : (int?)null;
            Add("P6c", "Approved reachable separately (Rule 3)", Verdict.Pass,
                $"total={totalA?.ToString() ?? "n/a"} approved sets. Catalog sync needs BOTH sweeps, or a dump.");
        }
        else
        {
            Add("P6c", "Approved reachable separately (Rule 3)", Verdict.Inconclusive,
                $"{ra.StatusLine} — if 's=approved' is not a valid filter, read status from each set and filter client-side.");
        }
    }

    // ---------------------------------------------------------------------
    // P7 — rate limit headers, if any are exposed.
    // ---------------------------------------------------------------------
    public async Task P7_RateLimitAsync(CancellationToken ct)
    {
        Header("P7", "Rate-limit headers exposed");

        var r = await api.GetAsync("/beatmaps/129891", ct);
        if (r.RateHeaders.Count == 0)
            Add("P7", "Rate-limit headers exposed", Verdict.Inconclusive,
                "No rate-limit headers returned. Enforce the documented ~60 req/min ceiling client-side with a token bucket; " +
                "you will not get server-side feedback until a 429.");
        else
            Add("P7", "Rate-limit headers exposed", Verdict.Pass,
                string.Join(", ", r.RateHeaders.Select(kv => $"{kv.Key}={kv.Value}")));
    }

    // ------------------------- helpers -------------------------

    private static void Header(string id, string title)
    {
        Console.WriteLine();
        Console.WriteLine($"{id} — {title}");
        Console.WriteLine(new string('-', 74));
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    private static string Head(string body) =>
        body.Length <= 220 ? body.ReplaceLineEndings(" ") : body[..220].ReplaceLineEndings(" ") + "…";

    private static string? Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    private static int? Int(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32() : null;

    private static double Dbl(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble() : 0;

    private static bool? Bool(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v)
            && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean() : null;

    private static string? FindTimestampKey(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
        {
            var n = p.Name;
            if (p.Value.ValueKind == JsonValueKind.String &&
                (n.Contains("_at", StringComparison.OrdinalIgnoreCase) ||
                 n.Contains("date", StringComparison.OrdinalIgnoreCase) ||
                 n.Contains("time", StringComparison.OrdinalIgnoreCase)))
                return n;
        }
        return null;
    }

    /// <summary>Mods come back as string[] on stable and object[] with an "acronym" on lazer.</summary>
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
