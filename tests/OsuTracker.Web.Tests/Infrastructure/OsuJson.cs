using System.Globalization;
using System.Text.Json;

namespace OsuTracker.Web.Tests.Infrastructure;

/// <summary>
/// Response bodies in the shapes osu! API v2 (x-api-version 20240529) returns, cut down to
/// the fields the sync jobs read.
/// </summary>
public static class OsuJson
{
    public static string Iso(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    public static object Set(long id, long[] mapIds, int mode = 2, int ranked = 1, DateTimeOffset? rankedDate = null) => new Dictionary<string, object?>
    {
        ["id"] = id,
        ["artist"] = $"Artist {id}",
        ["title"] = $"Title {id}",
        ["creator"] = "Mapper",
        ["user_id"] = 1,
        ["ranked"] = ranked,
        ["ranked_date"] = Iso(rankedDate ?? Seed.RankedLongAgo),
        ["submitted_date"] = Iso((rankedDate ?? Seed.RankedLongAgo).AddDays(-30)),
        ["bpm"] = 180,
        ["covers"] = new Dictionary<string, object?> { ["cover"] = $"https://assets.ppy.sh/beatmaps/{id}/covers/cover.jpg" },
        ["beatmaps"] = mapIds.Select(m => new Dictionary<string, object?>
        {
            ["id"] = m,
            ["mode_int"] = mode,
            ["version"] = $"diff {m}",
            ["difficulty_rating"] = 3.5,
            ["total_length"] = 120,
            ["max_combo"] = 500,
            ["cs"] = 4,
            ["ar"] = 9,
            ["accuracy"] = 8,
            ["drain"] = 6,
            ["ranked"] = ranked,
            ["convert"] = false
        }).ToArray()
    };

    public static string SearchPage(string? cursor, params object[] sets) => JsonSerializer.Serialize(new Dictionary<string, object?>
    {
        ["beatmapsets"] = sets,
        ["cursor_string"] = cursor
    });

    public static string MostPlayed(params (long BeatmapId, int Count)[] rows) =>
        JsonSerializer.Serialize(rows.Select(r => new Dictionary<string, object?>
        {
            ["beatmap_id"] = r.BeatmapId,
            ["count"] = r.Count,
            ["beatmap"] = new Dictionary<string, object?> { ["id"] = r.BeatmapId }
        }));

    public static Dictionary<string, object?> Score(long id, long beatmapId, DateTimeOffset endedAt,
        double accuracy = 0.95, bool passed = true, bool lazer = false, string rank = "A", params string[] mods)
    {
        var score = new Dictionary<string, object?>
        {
            ["id"] = id,
            ["beatmap_id"] = beatmapId,
            ["beatmap"] = new Dictionary<string, object?> { ["id"] = beatmapId },
            ["ended_at"] = Iso(endedAt),
            ["rank"] = rank,
            ["accuracy"] = accuracy,
            ["max_combo"] = 321,
            ["passed"] = passed,
            ["mods"] = mods.Select(m => new Dictionary<string, object?> { ["acronym"] = m }).ToArray()
        };
        if (lazer) score["build_id"] = 7500;
        return score;
    }

    public static string AllScores(params Dictionary<string, object?>[] scores) =>
        JsonSerializer.Serialize(new Dictionary<string, object?> { ["scores"] = scores });

    public static string Array(params Dictionary<string, object?>[] scores) => JsonSerializer.Serialize(scores);
}
