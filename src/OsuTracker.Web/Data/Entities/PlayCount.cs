namespace OsuTracker.Web.Data.Entities;

/// <summary>
/// From most_played. Carries no timestamps — confirmed in Phase 1 — so Attempted
/// cannot be filtered by Rule 5 and stays approximate.
/// </summary>
public class PlayCount
{
    public long BeatmapId { get; set; }
    public Beatmap? Beatmap { get; set; }
    public int Count { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    /// <summary>
    /// What Count read when this map's scores were last fetched; null if they never were.
    /// A pass cannot appear without a play, so a map whose count has not moved since has
    /// nothing new to say — and one whose count has is worth exactly one more request.
    /// This is what lets the score job be re-run daily as a safety net, instead of
    /// re-asking about every map ever attempted.
    /// </summary>
    public int? CheckedCount { get; set; }
}
