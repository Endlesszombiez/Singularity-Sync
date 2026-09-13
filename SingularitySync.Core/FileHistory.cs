using System.Text;

namespace SingularitySync.Core;

/// <summary>Server-local snapshots. Call under the server's synchronization gate.</summary>
public sealed class FileHistory
{
    public const int Limit = 10;
    private readonly FolderStore store;
    public string Root { get; }
    public FileHistory(FolderStore store)
    {
        this.store = store;
        Root = Path.Combine(store.Metadata, "history");
        FolderStore.EnsureNoLinks(Root);
        Directory.CreateDirectory(Root);
    }

    public void Capture(FileEntry entry)
    {
        store.Resolve(entry.Path);
        if (entry.Deleted) return;
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(entry.Path.ToUpperInvariant())));
        string directory = Path.Combine(Root, key);
        FolderStore.EnsureNoLinks(directory);
        Directory.CreateDirectory(directory);
        string indexPath = Path.Combine(directory, "index.json");
        FolderStore.EnsureNoLinks(indexPath);
        var index = DiskJson.Read<HistoryIndex>(indexPath) ?? new(entry.Path, []);
        if (!index.Path.Equals(entry.Path, StringComparison.OrdinalIgnoreCase)) throw new IOException("History path mismatch.");
        foreach (var version in index.Versions) ValidateId(version.Id);
        var latest = index.Versions.LastOrDefault();
        if (latest is null || latest.Hash != entry.Hash || !File.Exists(SnapshotPath(directory, index.Path, latest.Id)))
        {
            string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfffffff", System.Globalization.CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            string snapshot = SnapshotPath(directory, index.Path, id);
            string staging = store.NewTemp();
            try
            {
                using (var source = store.OpenRead(entry.Path))
                using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024))
                {
                    source.CopyTo(output);
                    output.Position = 0;
                    if (output.Length != entry.Length || FolderStore.Hash(output) != entry.Hash)
                        throw new IOException("File changed while saving history: " + entry.Path);
                    output.Flush(true);
                }
                Directory.CreateDirectory(Path.GetDirectoryName(snapshot)!);
                File.Move(staging, snapshot);
                var versions = index.Versions.Append(new HistoryVersion(id, entry.Hash!, DateTime.UtcNow)).TakeLast(Limit).ToList();
                // Commit the index only after the verified snapshot is durable. Prune afterwards.
                index = new(index.Path, versions);
                DiskJson.Write(indexPath, index);
            }
            finally { if (File.Exists(staging)) File.Delete(staging); }
        }
        // Also cleans snapshots left unreferenced by an interrupted index write or prune.
        var retained = index.Versions.Select(v => v.Id).ToHashSet(StringComparer.Ordinal);
        foreach (string candidate in Directory.EnumerateDirectories(directory))
        {
            string id = Path.GetFileName(candidate);
            if (!IsId(id) || retained.Contains(id)) continue;
            FolderStore.EnsureNoLinks(candidate);
            string snapshot = SnapshotPath(directory, index.Path, id);
            if (File.Exists(snapshot)) File.Delete(snapshot);
            Directory.Delete(candidate, false);
        }
    }

    private static bool IsId(string id) => id.Length == 55 && id[8] == '-' && id[22] == '-' &&
        id[..8].All(char.IsAsciiDigit) && id[9..22].All(char.IsAsciiDigit) && Guid.TryParseExact(id[23..], "N", out _);
    private static void ValidateId(string id)
    {
        if (!IsId(id)) throw new IOException("Invalid history snapshot ID.");
    }
    private static string SnapshotPath(string directory, string path, string id)
    {
        ValidateId(id);
        string result = Path.Combine(directory, id, Path.GetFileName(path));
        FolderStore.EnsureNoLinks(result);
        return result;
    }
    public sealed record HistoryVersion(string Id, string Hash, DateTime CapturedUtc);
    public sealed record HistoryIndex(string Path, List<HistoryVersion> Versions);
}
