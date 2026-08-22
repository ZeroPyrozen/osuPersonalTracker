namespace OsuTracker.Web.OsuApi;

public sealed class OsuApiOptions
{
    public const string SectionName = "OsuApi";

    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public long UserId { get; set; }

    /// <summary>
    /// Requests per minute. Phase 1 measured: the advertised ceiling is 1200, but
    /// sustained paging above ~200/min gets the connection dropped. 60 ran clean.
    /// </summary>
    public int RequestsPerMinute { get; set; } = 60;
}
