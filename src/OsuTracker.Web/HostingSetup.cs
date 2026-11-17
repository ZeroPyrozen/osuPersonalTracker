namespace OsuTracker.Web;

/// <summary>
/// The two questions Program.cs asks of configuration before the host exists, kept here
/// so they can be tested without booting one.
/// </summary>
public static class HostingSetup
{
    public const string DataDirectoryKey = "Tracker:DataDirectory";
    public const string DatabaseFileName = "tracker.db";

    /// <summary>
    /// Where tracker.db lives. Tracker:DataDirectory if set (Tracker__DataDirectory as an
    /// environment variable), otherwise beside the app as it always has been.
    ///
    /// Beside the app is fine for `dotnet run`, and wrong for a published copy: there the
    /// one file that cannot be rebuilt sits among the ones every deploy overwrites, so a
    /// deploy has to copy around it and a backup of the binaries drags the database along.
    /// A relative value is read against the content root, not the working directory, which
    /// under systemd is whatever the unit happens to say.
    /// </summary>
    public static string DatabasePath(IConfiguration config, string contentRoot)
    {
        var dir = config[DataDirectoryKey];
        if (string.IsNullOrWhiteSpace(dir)) return Path.Combine(contentRoot, DatabaseFileName);

        var full = Path.GetFullPath(dir.Trim(), contentRoot);
        Directory.CreateDirectory(full);
        return Path.Combine(full, DatabaseFileName);
    }

    /// <summary>
    /// Whether this process listens on HTTPS at all. Behind a TLS-terminating proxy it
    /// does not: there is no port to redirect to, so the redirect middleware does nothing
    /// except log "Failed to determine the https port" on the first plain request.
    /// </summary>
    public static bool HasHttpsEndpoint(IConfiguration config)
    {
        static bool IsHttps(string? urls) =>
            urls?.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Any(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) == true;

        if (IsHttps(config["urls"])) return true;
        if (!string.IsNullOrWhiteSpace(config["https_port"]) || !string.IsNullOrWhiteSpace(config["https_ports"])) return true;

        return config.GetSection("Kestrel:Endpoints").GetChildren().Any(e => IsHttps(e["Url"]));
    }
}
