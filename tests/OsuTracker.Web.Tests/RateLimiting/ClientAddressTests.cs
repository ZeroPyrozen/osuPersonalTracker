using System.Net;

namespace OsuTracker.Web.Tests.RateLimiting;

public sealed class ClientAddressTests
{
    private static string Key(string address) => ClientAddress.PartitionKey(IPAddress.Parse(address));

    [Fact]
    public void Each_ipv4_address_is_its_own_client()
    {
        Assert.Equal("203.0.113.5", Key("203.0.113.5"));
        Assert.NotEqual(Key("203.0.113.5"), Key("203.0.113.6"));
    }

    [Fact]
    public void Every_address_in_one_ipv6_slash64_is_one_client()
    {
        // One home or server is normally handed a whole /64. Keyed by full address, it could
        // rotate through 2^64 of them and start every rate limit over each time.
        Assert.Equal(Key("2001:db8:1:2::1"), Key("2001:db8:1:2:ffff:ffff:ffff:fffe"));
        Assert.Equal(Key("2001:db8:1:2::1"), Key("2001:db8:1:2:1234:5678:9abc:def0"));
    }

    [Fact]
    public void Neighbouring_ipv6_networks_stay_apart()
    {
        Assert.NotEqual(Key("2001:db8:1:2::1"), Key("2001:db8:1:3::1"));
    }

    [Fact]
    public void A_v4_mapped_address_counts_as_the_ipv4_client_it_is()
    {
        Assert.Equal(Key("203.0.113.5"), Key("::ffff:203.0.113.5"));
    }

    [Fact]
    public void No_address_is_one_shared_unknown_client()
    {
        Assert.Equal("unknown", ClientAddress.PartitionKey(null));
    }
}
