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
    public DateTimeOffset? RankedDate
    {
        get => _rankedDate;
        set { _rankedDate = value; RankedDateUnix = value?.ToUnixTimeSeconds(); }
    }

    private DateTimeOffset? _rankedDate;

    /// <summary>
    /// RankedDate as Unix seconds, purely so it can be sorted in SQL. SQLite stores a
    /// DateTimeOffset as text carrying its own offset, so EF refuses to put one in an
    /// ORDER BY at all — "browse, newest ranked first" threw rather than sorted. Same
    /// shadow-column trick as <see cref="Score.PlayedAtUnix"/>, except the setter above
    /// keeps the pair in step so no write site can forget.
    /// </summary>
    public long? RankedDateUnix { get; private set; }

    public DateTimeOffset? SubmittedDate { get; set; }
    public double Bpm { get; set; }
    [MaxLength(512)] public string? CoverUrl { get; set; }

    /// <summary>Reconciliation stamp — set to the run start on every sweep that sees this row.</summary>
    public DateTimeOffset SeenInRunAt { get; set; }

    public List<Beatmap> Beatmaps { get; set; } = [];
}
