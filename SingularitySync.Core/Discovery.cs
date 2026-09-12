using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SingularitySync.Core;

public static class Discovery
{
    public static async Task ServeAsync(Func<ServerInfo> info, CancellationToken ct)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, Protocol.DiscoveryPort));
        while (!ct.IsCancellationRequested)
        {
            var packet = await udp.ReceiveAsync(ct);
            if (Encoding.UTF8.GetString(packet.Buffer) != Protocol.DiscoveryQuery) continue;
            byte[] response = JsonSerializer.SerializeToUtf8Bytes(info(), Protocol.Json);
            await udp.SendAsync(response, packet.RemoteEndPoint, ct);
        }
    }
    public static async Task<List<DiscoveredServer>> FindAsync(TimeSpan duration, CancellationToken ct = default)
    {
        using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, 0)) { EnableBroadcast = true };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(duration);
        var addresses = new HashSet<IPAddress> { IPAddress.Broadcast, IPAddress.Loopback };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        foreach (var address in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
        {
            byte[] ip = address.Address.GetAddressBytes(), mask = address.IPv4Mask.GetAddressBytes();
            addresses.Add(new IPAddress(ip.Zip(mask, (a, m) => (byte)(a | ~m)).ToArray()));
        }
        var result = new Dictionary<string, DiscoveredServer>();
        byte[] query = Encoding.UTF8.GetBytes(Protocol.DiscoveryQuery);
        foreach (var address in addresses)
        {
            try { await udp.SendAsync(query, new IPEndPoint(address, Protocol.DiscoveryPort), timeout.Token); }
            catch (SocketException) { }
        }
        try
        {
            while (true)
            {
                var packet = await udp.ReceiveAsync(timeout.Token);
                try
                {
                    var info = JsonSerializer.Deserialize<ServerInfo>(packet.Buffer, Protocol.Json);
                    if (info is null || !Guid.TryParse(info.Id, out _) || !Guid.TryParse(info.FolderId, out _) || info.Port is < 1 or > 65535 || info.Macs is null) continue;
                    string address = packet.RemoteEndPoint.Address.ToString();
                    if (!result.ContainsKey(info.Id) || !IPAddress.IsLoopback(packet.RemoteEndPoint.Address)) result[info.Id] = new(info, address);
                }
                catch (JsonException) { }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return result.Values.OrderBy(s => s.Info.Name).ToList();
    }
}
