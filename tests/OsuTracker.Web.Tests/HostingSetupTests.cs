using Microsoft.Extensions.Configuration;

namespace OsuTracker.Web.Tests;

public sealed class HostingSetupTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.Select(p => new KeyValuePair<string, string?>(p.Key, p.Value)))
            .Build();

    private static string TempRoot() =>
        Path.Combine(Path.GetTempPath(), "osutracker-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void With_nothing_configured_the_database_stays_beside_the_app()
    {
        var root = TempRoot();

        Assert.Equal(Path.Combine(root, "tracker.db"), HostingSetup.DatabasePath(Config(), root));
        // The default must not start creating directories: the content root already exists.
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void A_configured_data_directory_holds_the_database_and_is_created()
    {
        var root = TempRoot();
        var data = Path.Combine(TempRoot(), "data");
        try
        {
            var path = HostingSetup.DatabasePath(Config((HostingSetup.DataDirectoryKey, data)), root);

            Assert.Equal(Path.Combine(data, "tracker.db"), path);
            Assert.True(Directory.Exists(data));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(data)!, recursive: true);
        }
    }

    [Fact]
    public void A_relative_data_directory_is_read_against_the_content_root()
    {
        var root = TempRoot();
        try
        {
            var path = HostingSetup.DatabasePath(Config((HostingSetup.DataDirectoryKey, "../tracker-data")), Path.Combine(root, "app"));

            Assert.Equal(Path.Combine(root, "tracker-data", "tracker.db"), path);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("urls", "http://0.0.0.0:5080", false)]
    [InlineData("urls", "https://localhost:7090;http://localhost:5256", true)]
    [InlineData("urls", "http://localhost:5256; HTTPS://localhost:7090", true)]
    [InlineData("https_port", "443", true)]
    [InlineData("https_ports", "8443", true)]
    [InlineData("Kestrel:Endpoints:Web:Url", "http://*:5080", false)]
    [InlineData("Kestrel:Endpoints:Secure:Url", "https://*:5443", true)]
    public void Https_is_detected_from_wherever_an_endpoint_can_be_declared(string key, string value, bool expected)
    {
        Assert.Equal(expected, HostingSetup.HasHttpsEndpoint(Config((key, value))));
    }

    [Fact]
    public void No_endpoint_configuration_at_all_means_plain_http()
    {
        Assert.False(HostingSetup.HasHttpsEndpoint(Config()));
    }
}
