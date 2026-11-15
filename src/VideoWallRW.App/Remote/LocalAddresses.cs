using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace VideoWallRW.App.Remote;

/// <summary>Endereços IPv4 desta máquina na rede local, pra mostrar ao operador.</summary>
public static class LocalAddresses
{
    public sealed record Entry(string Address, string InterfaceName);

    /// <summary>
    /// Placas ativas com IPv4 (sem loopback, túnel ou 169.254 — sem DHCP). As que têm
    /// gateway (a rede de verdade do estúdio) vêm primeiro; cabo antes de Wi-Fi.
    /// </summary>
    public static IReadOnlyList<Entry> Get()
    {
        var result = new List<(Entry Entry, int Rank)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = nic.GetIPProperties();
                bool hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                                                                  !g.Address.Equals(System.Net.IPAddress.Any));
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    string ip = ua.Address.ToString();
                    if (ip.StartsWith("169.254.", StringComparison.Ordinal)) continue;
                    int rank = (hasGateway ? 0 : 10) + (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 1 : 0);
                    result.Add((new Entry(ip, nic.Name), rank));
                }
            }
        }
        catch (NetworkInformationException) { /* sem acesso às placas: lista vazia */ }
        return result.OrderBy(r => r.Rank).Select(r => r.Entry).ToList();
    }
}
