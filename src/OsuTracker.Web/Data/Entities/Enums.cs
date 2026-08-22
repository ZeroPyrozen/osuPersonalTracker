namespace OsuTracker.Web.Data.Entities;

/// <summary>Matches the osu! API ruleset ids exactly, so no translation layer is needed.</summary>
public enum GameMode
{
    Osu = 0,
    Taiko = 1,
    Fruits = 2,
    Mania = 3
}

/// <summary>
/// Mirrors the API's status ints. Rule 3: only Ranked and Approved are counted.
/// Departed is ours, not the API's — it marks a map that was counted and has since
/// left, so we keep the row and its score without keeping it in the denominator.
/// </summary>
public enum BeatmapStatus
{
    Departed = -3,
    Graveyard = -2,
    Wip = -1,
    Pending = 0,
    Ranked = 1,
    Approved = 2,
    Qualified = 3,
    Loved = 4
}

public static class StatusRules
{
    /// <summary>Rule 3, in one place. Everything that counts, counts here.</summary>
    public static bool Counts(this BeatmapStatus s) =>
        s is BeatmapStatus.Ranked or BeatmapStatus.Approved;

    public static readonly int[] CountedInts =
    [
        (int)BeatmapStatus.Ranked,
        (int)BeatmapStatus.Approved
    ];

    public static BeatmapStatus Parse(int raw) =>
        Enum.IsDefined(typeof(BeatmapStatus), raw) ? (BeatmapStatus)raw : BeatmapStatus.Pending;
}

/// <summary>The three-rung ladder from Rule 4. Derived at query time, never stored.</summary>
public enum CompletionState
{
    Untouched = 0,
    Attempted = 1,
    Passed = 2
}
