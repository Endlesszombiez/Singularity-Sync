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
            try
            {
                var packet = await udp.ReceiveAsync(ct);
                if (Encoding.UTF8.GetString(packet.Buffer) != Protocol.DiscoveryQuery) continue;
                byte[] response = JsonSerializer.SerializeToUtf8Bytes(info(), Protocol.Json);
                await udp.SendAsync(response, packet.RemoteEndPoint, ct);
            }
            catch (SocketException)
            {
                // A vanished client or transient adapter error must not stop discovery.
                await Task.Delay(100, ct);
            }
        }
    }
    public static async Task<List<DiscoveredServer>> FindAsync(TimeSpan duration, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(duration);
        var endpoints = new HashSet<(IPAddress Local, IPAddress Target)>
        {
            (IPAddress.Loopback, IPAddress.Loopback),
            (IPAddress.Any, IPAddress.Broadcast)
        };
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            try
            {
                foreach (var address in nic.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)))
                {
                    byte[] ip = address.Address.GetAddressBytes(), mask = address.IPv4Mask.GetAddressBytes();
                    if (mask.All(b => b == 0) || mask.All(b => b == 255)) continue;
                    var broadcast = new IPAddress(ip.Zip(mask, (a, m) => (byte)(a | ~m)).ToArray());
                    endpoints.Add((address.Address, broadcast));
                }
            }
            catch (NetworkInformationException) { } // An adapter may disappear during enumeration.
        }
        var result = new Dictionary<string, DiscoveredServer>();
        byte[] query = Encoding.UTF8.GetBytes(Protocol.DiscoveryQuery);
        await Task.WhenAll(endpoints.Select(endpoint => ProbeAsync(endpoint.Local, endpoint.Target)));
        ct.ThrowIfCancellationRequested();
        return result.Values.OrderBy(s => s.Info.Name).ToList();

        async Task ProbeAsync(IPAddress local, IPAddress target)
        {
            try
            {
                // Bind each probe to its adapter so VPN/virtual adapter routes cannot take all broadcasts.
                using var udp = new UdpClient(new IPEndPoint(local, 0)) { EnableBroadcast = true };
                var destination = new IPEndPoint(target, Protocol.DiscoveryPort);
                Task<UdpReceiveResult> receive = udp.ReceiveAsync(timeout.Token).AsTask();
                while (!timeout.IsCancellationRequested)
                {
                    await udp.SendAsync(query, destination, timeout.Token);
                    var retry = Task.Delay(400, timeout.Token);
                    while (await Task.WhenAny(receive, retry) == receive)
                    {
                        var packet = await receive;
                        try
                        {
                            var info = JsonSerializer.Deserialize<ServerInfo>(packet.Buffer, Protocol.Json);
                            if (info is not null && Guid.TryParse(info.Id, out _) && Guid.TryParse(info.FolderId, out _) && info.Port is >= 1 and <= 65535 && info.Macs is not null)
                            {
                                lock (result)
                                {
                                    if (!result.ContainsKey(info.Id) || !IPAddress.IsLoopback(packet.RemoteEndPoint.Address))
                                        result[info.Id] = new(info, packet.RemoteEndPoint.Address.ToString());
                                }
                            }
                        }
                        catch (JsonException) { }
                        receive = udp.ReceiveAsync(timeout.Token).AsTask();
                    }
                }
                try { await receive; } catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            catch (SocketException) { } // One unavailable adapter must not cancel the other probes.
        }
    }
}
