using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OsuTracker.Web.Sync;

/// <summary>
/// Turns a failure into something safe to write down. SyncJob.Error is rendered on /sync,
/// which is anonymous and — behind the Funnel — public, so whatever lands in it is
/// published. An exception message is the wrong shape for that: a SQLite failure carries
/// the full database path, and the osu! token request interpolates the entire OAuth
/// response body into its message.
///
/// The full exception still goes to the logger at every call site, so nothing is lost —
/// it moves to `journalctl --user-unit=osu-tracker`, where only the operator can read it.
/// </summary>
public static class SyncError
{
    public static string Describe(Exception ex) => ex switch
    {
        // Checked before OperationCanceledException, which it derives from. HttpClient
        // raises this for its own timeout as well as for a real cancellation, and from
        // out here the two are genuinely indistinguishable.
        TaskCanceledException => "cancelled or timed out",
        OperationCanceledException => "cancelled",

        // Status codes are safe and are the first thing worth knowing; the URL and body
        // that come with them are not.
        HttpRequestException { StatusCode: { } code } => $"osu! API returned {(int)code}",
        HttpRequestException => "could not reach the osu! API",

        JsonException => "unexpected response shape from the osu! API",
        SqliteException => "database error",

        // The type alone says enough to know where to look without quoting anything the
        // exception was carrying.
        _ => ex.GetType().Name
    };
}
