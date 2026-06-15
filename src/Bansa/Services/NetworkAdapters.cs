using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Bansa.Services;

// ════════════════════════════════════════════════════════════════════════════
//  NetworkAdapters  —  enumerate active IPv4 interfaces for source-IP binding
// ════════════════════════════════════════════════════════════════════════════

/// <summary>One bindable network interface and the IPv4 address apps bind to.</summary>
public sealed record NetAdapter(string Name, string Description, IPAddress Ipv4, NetworkInterfaceType Type)
{
    public bool IsWifi     => Type == NetworkInterfaceType.Wireless80211;
    public bool IsEthernet => Type is NetworkInterfaceType.Ethernet
                                   or NetworkInterfaceType.GigabitEthernet
                                   or NetworkInterfaceType.FastEthernetT
                                   or NetworkInterfaceType.FastEthernetFx;

    public string Kind    => IsWifi ? "Wi-Fi" : IsEthernet ? "Ethernet" : "Other";
    public string Display => $"{Kind} · {Name} · {Ipv4}";
}

public static class NetworkAdapters
{
    /// <summary>Active, non-loopback interfaces that have a routable IPv4 address.</summary>
    public static List<NetAdapter> ListActiveIpv4()
    {
        var result = new List<NetAdapter>();

        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback
                                        or NetworkInterfaceType.Tunnel) continue;

            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                var ip = ua.Address;
                if (ip.AddressFamily != AddressFamily.InterNetwork) continue;
                if (IPAddress.IsLoopback(ip)) continue;

                // Skip APIPA / link-local 169.254.x.x — not a usable source.
                var b = ip.GetAddressBytes();
                if (b[0] == 169 && b[1] == 254) continue;

                result.Add(new NetAdapter(ni.Name, ni.Description, ip, ni.NetworkInterfaceType));
                break; // one IPv4 per adapter is enough to bind to
            }
        }

        // Real adapters (Wi-Fi / Ethernet) first, virtual / VPN last.
        return result
            .OrderByDescending(a => a.IsEthernet || a.IsWifi)
            .ThenBy(a => a.Kind)
            .ThenBy(a => a.Name)
            .ToList();
    }
}
