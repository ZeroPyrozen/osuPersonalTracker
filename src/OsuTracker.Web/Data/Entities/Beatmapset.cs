using System.ComponentModel.DataAnnotations;

namespace OsuTracker.Web.Data.Entities;

public class Beatmapset
{
    public long Id { get; set; }

    [MaxLength(512)] public string Artist { get; set; } = "";
    [MaxLength(512)] public string Title { get; set; } = "";
    [MaxLength(128)] public string Creator { get; set; } = "";
    public long CreatorUserId { get; set; }

    public BeatmapStatus Status { get; set; }

    /// <summary>Rule 5 compares every score's PlayedAt against this.</summary>
    public DateTimeOffset? RankedDate { get; set; }

    public DateTimeOffset? SubmittedDate { get; set; }
    public double Bpm { get; set; }
    [MaxLength(512)] public string? CoverUrl { get; set; }

    /// <summary>Reconciliation stamp — set to the run start on every sweep that sees this row.</summary>
    public DateTimeOffset SeenInRunAt { get; set; }

    public List<Beatmap> Beatmaps { get; set; } = [];
}
