using System.ComponentModel.DataAnnotations;

namespace OsuTracker.Web.Data.Entities;

public enum SyncJobState { Idle = 0, Running = 1, Paused = 2, Failed = 3, Completed = 4 }

/// <summary>The least glamorous table here, and the one that decides whether a long job survives a crash.</summary>
public class SyncJob
{
    [MaxLength(64)] public string Name { get; set; } = "";
    public SyncJobState State { get; set; }

    /// <summary>Opaque resume token — a cursor_string for catalog, an offset for playcounts.</summary>
    [MaxLength(1024)] public string? Cursor { get; set; }

    public int ItemsDone { get; set; }
    public int ItemsTotal { get; set; }

    public DateTimeOffset? LastRunAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }

    /// <summary>Start of the currently running (or last) sweep — the reconciliation watermark.</summary>
    public DateTimeOffset? RunStartedAt { get; set; }

    [MaxLength(2048)] public string? Error { get; set; }
}
