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
}
