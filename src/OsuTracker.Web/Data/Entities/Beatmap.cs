using System.ComponentModel.DataAnnotations;

namespace OsuTracker.Web.Data.Entities;

/// <summary>
/// The unit of progress. Rule 2: converts are excluded, so every beatmap has exactly
/// one native mode and this table needs no composite key with the ruleset.
/// </summary>
public class Beatmap
{
    public long Id { get; set; }
    public long BeatmapsetId { get; set; }
    public Beatmapset? Beatmapset { get; set; }

    /// <summary>Native mode. Never a convert.</summary>
    public GameMode Mode { get; set; }

    [MaxLength(256)] public string DifficultyName { get; set; } = "";
    public double StarRating { get; set; }
    public int TotalLength { get; set; }
    public int? MaxCombo { get; set; }

    public double Cs { get; set; }
    public double Ar { get; set; }
    public double Od { get; set; }
    public double Hp { get; set; }

    public BeatmapStatus Status { get; set; }
    public DateTimeOffset SeenInRunAt { get; set; }

    public Score? Score { get; set; }
    public PlayCount? PlayCount { get; set; }
}
