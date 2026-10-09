using System.Net;
using System.Net.Sockets;

namespace Aibysitter.Web.Infrastructure;

/// <summary>Rate-limit partition key for a client address. IPv6 clients are keyed by /64, the usual per-subscriber allocation.</summary>
public static class ClientKey
{
    public const string Unknown = "unknown";

    public static string For(IPAddress? address)
    {
        if (address is null)
        {
            return Unknown;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }
}
