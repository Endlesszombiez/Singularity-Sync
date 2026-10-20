namespace SingularitySync.Core;

// Small atomic records preserve completed work between batched full-state checkpoints.
// The owning FolderStore lock and the caller's sync gate serialize all access.
public sealed class StateJournal<T> where T : class
{
    private readonly string directory;
    private int writes;
    public StateJournal(string metadata, string name)
    {
        directory = Path.Combine(metadata, name);
        FolderStore.EnsureNoLinks(directory);
        Directory.CreateDirectory(directory);
        writes = Directory.EnumerateFiles(directory, "*.json").Count();
    }
    public IEnumerable<T> ReadAll()
    {
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            FolderStore.EnsureNoLinks(path);
            yield return DiskJson.Read<T>(path) ?? throw new IOException("Invalid sync journal: " + path);
        }
    }
    public void Write(string relative, T value)
    {
        string key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(relative.ToUpperInvariant())));
        string path = Path.Combine(directory, key + ".json");
        FolderStore.EnsureNoLinks(path);
        DiskJson.Write(path, value);
        writes++;
    }
    public void Checkpoint<TState>(string statePath, TState state, bool force = false)
    {
        if (writes == 0 || (!force && writes < 128)) return;
        // If interrupted, either the old state plus records or the new state plus
        // remaining records reconstructs the same completed work.
        DiskJson.Write(statePath, state);
        foreach (string path in Directory.EnumerateFiles(directory, "*.json")) File.Delete(path);
        writes = 0;
    }
}

public sealed record ServerRevision(long Sequence, FileEntry Entry);
