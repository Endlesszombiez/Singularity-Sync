using System.Security.Cryptography;

namespace SingularitySync.Core;

public sealed class FolderStore : IDisposable
{
    public const string MetadataName = ".singularity-sync";
    private readonly FileStream ownership;
    private readonly Dictionary<string, (long Length, long Ticks, string Hash)> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object cacheLock = new();
    private DateTime lastFullScan = DateTime.MinValue;
    public string Root { get; }
    public string Metadata { get; }
    public FolderStore(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(Root)) throw new IOException("The sync folder does not exist.");
        EnsureNoLinks(Root);
        Metadata = Path.Combine(Root, MetadataName);
        EnsureNoLinks(Metadata);
        Directory.CreateDirectory(Metadata);
        EnsureNoLinks(Path.Combine(Metadata, "folder.lock"));
        ownership = new FileStream(Path.Combine(Metadata, "folder.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    public string Resolve(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 2000 || relative.Contains('\\') || Path.IsPathRooted(relative))
            throw new IOException("Invalid relative file path.");
        var parts = relative.Split('/');
        foreach (string part in parts)
        {
            string stem = part.Split('.')[0];
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Equals(MetadataName, StringComparison.OrdinalIgnoreCase) ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Any(c => c < 32) ||
                part.IndexOfAny([':', '*', '?', '"', '<', '>', '|']) >= 0 ||
                new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem, StringComparer.OrdinalIgnoreCase) ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && "123456789¹²³".Contains(stem[3])))
                throw new IOException("Unsafe file path.");
        }
        string full = Path.GetFullPath(Path.Combine(Root, Path.Combine(parts)));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Path escapes sync folder.");
        EnsureNoLinks(full);
        return full;
    }
    public static void EnsureNoLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Symlinks and junctions are not supported: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    public void Invalidate() { lock (cacheLock) cache.Clear(); }
    public Dictionary<string, FileEntry> Scan()
    {
        if (!Directory.Exists(Root)) throw new IOException("Sync folder unavailable; deletions have been paused.");
        EnsureNoLinks(Root);
        lock (cacheLock)
        {
            if (DateTime.UtcNow - lastFullScan > TimeSpan.FromSeconds(60)) { cache.Clear(); lastFullScan = DateTime.UtcNow; }
            var result = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            Walk(Root, result);
            foreach (var key in cache.Keys.Except(result.Keys, StringComparer.OrdinalIgnoreCase).ToArray()) cache.Remove(key);
            return result;
        }
    }
    private void Walk(string dir, Dictionary<string, FileEntry> result)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(dir))
        {
            if (Path.GetFileName(path).Equals(MetadataName, StringComparison.OrdinalIgnoreCase)) continue;
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Remove the symlink/junction from the sync folder: " + path);
            if ((attributes & FileAttributes.Directory) != 0) { Walk(path, result); continue; }
            string relative = Path.GetRelativePath(Root, path).Replace('\\', '/');
            Resolve(relative);
            var info = new FileInfo(path);
            long length = info.Length, ticks = info.LastWriteTimeUtc.Ticks;
            if (!cache.TryGetValue(relative, out var cached) || cached.Length != length || cached.Ticks != ticks)
            {
                using var stream = OpenRead(relative);
                string hash = Hash(stream);
                info.Refresh();
                if (info.Length != length || info.LastWriteTimeUtc.Ticks != ticks) throw new IOException("File is still changing: " + relative);
                cached = (length, ticks, hash);
                cache[relative] = cached;
            }
            result.Add(relative, new(relative, cached.Hash, cached.Length, ""));
        }
    }
    public FileStream OpenRead(string path) => new(Resolve(path), FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan);
    public static string Hash(Stream stream) => Convert.ToHexString(SHA256.HashData(stream));
    public string? CurrentHash(string path)
    {
        string full = Resolve(path);
        if (!File.Exists(full)) return null;
        using var stream = OpenRead(path);
        return Hash(stream);
    }
    public string NewTemp()
    {
        EnsureNoLinks(Metadata);
        string dir = Path.Combine(Metadata, "tmp");
        EnsureNoLinks(dir);
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N"));
    }
    public void Apply(string relative, string? temp, string? expectedHash)
    {
        string target = Resolve(relative);
        if (CurrentHash(relative) != expectedHash) throw new IOException("File changed during sync; retrying: " + relative);
        if (File.Exists(target))
        {
            string backup = Path.Combine(Metadata, "recovery", DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N"), relative.Replace('/', Path.DirectorySeparatorChar));
            EnsureNoLinks(backup);
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            if (temp is null) File.Move(target, backup);
            else File.Replace(temp, target, backup);
        }
        else if (temp is not null)
        {
            // A former directory may now be a file; only remove an empty directory.
            if (Directory.Exists(target)) Directory.Delete(target, false);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Resolve(relative);
            File.Move(temp, target);
        }
        if (temp is null)
        {
            // Empty directories are scaffolding, not sync entries. Clear them after deletions.
            for (string? dir = Path.GetDirectoryName(target); dir is not null && !dir.Equals(Root, StringComparison.OrdinalIgnoreCase); dir = Path.GetDirectoryName(dir))
            {
                try { Directory.Delete(dir, false); }
                catch (IOException) { break; }
                catch (UnauthorizedAccessException) { break; }
            }
        }
        Invalidate();
    }
    public string PreserveConflict(string relative, string expectedHash)
    {
        string name = Path.GetFileNameWithoutExtension(relative);
        if (name.Length > 80) name = name[..80];
        string extension = Path.GetExtension(relative);
        if (extension.Length > 30) extension = extension[..30];
        string conflict = (Path.GetDirectoryName(relative)?.Replace('\\', '/') is { Length: > 0 } dir ? dir + "/" : "") +
            name + " (conflict " + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8] + ")" + extension;
        using var source = OpenRead(relative);
        if (Hash(source) != expectedHash) throw new IOException("File changed before conflict could be saved.");
        source.Position = 0;
        using var dest = new FileStream(Resolve(conflict), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        source.CopyTo(dest);
        return conflict;
    }
    public void Dispose() => ownership.Dispose();
}
