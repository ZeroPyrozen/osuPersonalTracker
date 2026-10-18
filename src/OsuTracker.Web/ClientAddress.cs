using System.Net;
using System.Net.Sockets;

namespace OsuTracker.Web;

/// <summary>
/// Who a request counts as when it is rate limited.
/// </summary>
public static class ClientAddress
{
    /// <summary>
    /// An IPv4 address is one client. An IPv6 address is not: one home or server is normally
    /// handed a whole /64, and keying on the full address let a single client rotate through
    /// 2^64 of them and start every limit over each time. So IPv6 is bucketed by its /64 —
    /// the same deal as everyone behind one NAT sharing an IPv4 address.
    /// </summary>
    public static string PartitionKey(IPAddress? ip)
    {
        if (ip is null) return "unknown";
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily != AddressFamily.InterNetworkV6) return ip.ToString();

        Span<byte> bytes = stackalloc byte[16];
        ip.TryWriteBytes(bytes, out _);
        bytes[8..].Clear();
        return $"{new IPAddress(bytes)}/64";
    }
}
