using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;

namespace SingularitySync.Core;

public static class Protocol
{
    public const int HttpPort = 45831;
    public const int DiscoveryPort = 45832;
    public const string DiscoveryQuery = "SINGULARITY_SYNC_DISCOVER_V1";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static string[] MacAddresses() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .Select(n => n.GetPhysicalAddress().ToString()).Where(s => s.Length > 0).Distinct().ToArray();
}

public sealed record ServerInfo(string Id, string Name, string[] Macs, int Port, string FolderId);
public sealed record DiscoveredServer(ServerInfo Info, string Address)
{
    public override string ToString() => $"{Info.Name} — {Address}:{Info.Port}";
}
public sealed record FileEntry(string Path, string? Hash, long Length, string Version)
{
    public bool Deleted => Hash is null;
}
public sealed record Manifest(string FolderId, long Sequence, List<FileEntry> Files);
public sealed record PairRequest(string ClientId, string Code);
public sealed record PairResponse(ServerInfo Server, string Token);
public sealed record Binding(string ServerId, string FolderId, string[] Macs, string Address, int Port, string Token);
public sealed record Baseline(string Path, string? Hash, string Version);
public sealed class ClientState
{
    public string ServerId { get; set; } = "";
    public string FolderId { get; set; } = "";
    public Dictionary<string, Baseline> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
public sealed class ServerState
{
    public string FolderId { get; set; } = Guid.NewGuid().ToString("N");
    public long Sequence { get; set; }
    public Dictionary<string, FileEntry> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
public sealed class Settings
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N");
    public string Mode { get; set; } = "Server";
    public string Folder { get; set; } = "";
    public Binding? Binding { get; set; }
    public Dictionary<string, string> Clients { get; set; } = new();
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SingularitySync", "settings.json");
    public static Settings Load() => DiskJson.Read<Settings>(SettingsPath) ?? new();
    public void Save() { lock (this) DiskJson.Write(SettingsPath, this); }
}
public static class DiskJson
{
    public static T? Read<T>(string path) => File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Protocol.Json) : default;
    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Protocol.Json));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static string Token() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
}
