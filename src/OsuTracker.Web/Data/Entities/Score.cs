using System.ComponentModel.DataAnnotations;

namespace OsuTracker.Web.Data.Entities;

/// <summary>
/// Proof of a qualifying pass. The existence of a row means Passed — Rule 4 puts no
/// accuracy, grade or mod condition on it. Everything below PlayedAt is display only.
/// </summary>
public class Score
{
    public long BeatmapId { get; set; }
    public Beatmap? Beatmap { get; set; }

    public long ScoreId { get; set; }

    /// <summary>The load-bearing field. Rule 5 lives here.</summary>
    public DateTimeOffset PlayedAt { get; set; }

    public DateTimeOffset FetchedAt { get; set; }

    /// <summary>
    /// PlayedAt as Unix seconds, purely so dates can be compared in SQL.
    /// EF Core's SQLite provider cannot translate a DateTimeOffset comparison at all —
    /// not against another column, and not even against a parameter — so any query that
    /// needs "scores since X" has to go through an integer. PlayedAt stays as the
    /// display value and this stays its shadow; write both together, always.
    /// </summary>
    public long PlayedAtUnix { get; set; }

    /// <summary>
    /// Rule 5, decided once at ingest: PlayedAt >= the set's RankedDate. Evaluating it
    /// here rather than in every query keeps the date comparison out of SQL entirely —
    /// EF cannot translate a DateTimeOffset comparison across a two-table join on SQLite.
    /// The row is still kept when false, so the UI can explain why a heavily-played map
    /// counts as undone instead of just showing it as Untouched.
    /// </summary>
    public bool CountsAsPass { get; set; }

    // ---- display only. No status logic may read past this line. ----
    [MaxLength(4)] public string? Grade { get; set; }
    public double Accuracy { get; set; }
    public int? MaxCombo { get; set; }
    [MaxLength(256)] public string? Mods { get; set; }
    public bool IsLazer { get; set; }
}
