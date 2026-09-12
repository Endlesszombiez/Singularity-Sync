using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace SingularitySync.Core;

public sealed class SyncClient : IAsyncDisposable
{
    private readonly FolderStore store;
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly Action<string> log;
    private readonly Action<Binding> saveBinding;
    private readonly SemaphoreSlim wake = new(0, 1), cycle = new(1, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly FileSystemWatcher watcher;
    private ClientState state;
    private Task? runner;
    private long sequence;
    public Binding Binding { get; private set; }
    private string StatePath => Path.Combine(store.Metadata, "client-state.json");
    public SyncClient(string folder, Binding binding, Action<string>? log = null, Action<Binding>? saveBinding = null)
    {
        Binding = binding; this.log = log ?? (_ => { }); this.saveBinding = saveBinding ?? (_ => { });
        store = new(folder);
        try
        {
            state = DiskJson.Read<ClientState>(StatePath) ?? new() { ServerId = binding.ServerId, FolderId = binding.FolderId };
            if (state.ServerId != binding.ServerId || state.FolderId != binding.FolderId)
                throw new IOException("This folder belongs to a different sync pairing. Choose a new client folder.");
            state.Files = new(state.Files, StringComparer.OrdinalIgnoreCase);
            DiskJson.Write(StatePath, state);
            watcher = new(store.Root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            watcher.Changed += OnChange; watcher.Created += OnChange; watcher.Deleted += OnChange; watcher.Renamed += OnChange;
            watcher.Error += (_, _) => Wake(); watcher.EnableRaisingEvents = true;
        }
        catch { store.Dispose(); http.Dispose(); throw; }
    }
    public static Uri Address(string address, int port, string path)
    {
        if (!IPAddress.TryParse(address.Trim(), out var ip)) throw new ArgumentException("Enter an IPv4 or IPv6 address, without http:// or a port.");
        return new UriBuilder("http", ip.ToString(), port, path).Uri;
    }
    public static async Task<Binding> PairAsync(string address, int port, string clientId, string code, string? expectedServerId = null, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var info = await http.GetFromJsonAsync<ServerInfo>(Address(address, port, "/info"), Protocol.Json, ct) ?? throw new IOException("Invalid server response.");
        if (expectedServerId is not null && info.Id != expectedServerId) throw new IOException("The server at this address changed. Search again.");
        using var response = await http.PostAsJsonAsync(Address(address, port, "/pair"), new PairRequest(clientId, code.Trim()), Protocol.Json, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new IOException("Pairing code is incorrect or expired. Generate a new code on the server.");
        if ((int)response.StatusCode == 429) throw new IOException("Too many pairing attempts. Wait one minute.");
        response.EnsureSuccessStatusCode();
        var pair = await response.Content.ReadFromJsonAsync<PairResponse>(Protocol.Json, ct) ?? throw new IOException("Invalid pairing response.");
        if (pair.Server.Id != info.Id || pair.Server.FolderId != info.FolderId) throw new IOException("Server identity changed during pairing.");
        return new(pair.Server.Id, pair.Server.FolderId, pair.Server.Macs, address.Trim(), port, pair.Token);
    }
    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(Address(Binding.Address, Binding.Port, "/"), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Binding.Token);
        request.Headers.Add("X-Sync-Folder", Binding.FolderId);
        return request;
    }
    private static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new IOException("Pairing was revoked. Stop and pair with the server again.");
        if (response.StatusCode == HttpStatusCode.PreconditionFailed) throw new IOException("The server is sharing a different folder. Choose a new client folder and pair again.");
        if (response.StatusCode == HttpStatusCode.Conflict) throw new IOException("A file changed during transfer; retrying safely.");
        response.EnsureSuccessStatusCode();
    }
    private bool Matches(ServerInfo info) => info.Id == Binding.ServerId && info.FolderId == Binding.FolderId &&
        (Binding.Macs.Length == 0 || info.Macs.Length == 0 || info.Macs.Intersect(Binding.Macs, StringComparer.OrdinalIgnoreCase).Any());
    public async Task EnsureConnectedAsync(CancellationToken ct)
    {
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var info = await http.GetFromJsonAsync<ServerInfo>(Address(Binding.Address, Binding.Port, "/info"), Protocol.Json, timeout.Token);
                if (info is not null && Matches(info)) return;
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { ct.ThrowIfCancellationRequested(); }
        }
        log("Searching LAN for the paired server…");
        var servers = await Discovery.FindAsync(TimeSpan.FromSeconds(1.5), ct);
        foreach (var server in servers.Where(s => Matches(s.Info)))
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var info = await http.GetFromJsonAsync<ServerInfo>(Address(server.Address, server.Info.Port, "/info"), Protocol.Json, timeout.Token);
                if (info is null || !Matches(info)) continue;
                Binding = Binding with { Address = server.Address, Port = server.Info.Port, Macs = info.Macs };
                saveBinding(Binding);
                log("Reconnected to " + info.Name + " at " + server.Address);
                return;
            }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { ct.ThrowIfCancellationRequested(); }
        }
        throw new IOException("Paired server is offline or cannot be discovered. Retrying automatically.");
    }
    public void Start() => runner ??= Task.Run(RunAsync);
    private void Wake() { try { wake.Release(); } catch (SemaphoreFullException) { } }
    private void OnChange(object sender, FileSystemEventArgs e)
    {
        if (!e.FullPath.StartsWith(store.Metadata + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { store.Invalidate(); Wake(); }
    }
    private async Task RunAsync()
    {
        bool wasOffline = true;
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await EnsureConnectedAsync(stop.Token);
                await SyncOnceAsync(stop.Token);
                if (wasOffline) { log("Connected. Watching for changes."); wasOffline = false; }
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                using var request = Request(HttpMethod.Get, "/changes?since=" + sequence);
                var remote = http.SendAsync(request, waiting.Token);
                var local = wake.WaitAsync(TimeSpan.FromSeconds(2), waiting.Token);
                await Task.WhenAny(remote, local);
                waiting.Cancel();
                try { using var response = await remote; Check(response); } catch (OperationCanceledException) when (waiting.IsCancellationRequested) { }
                try { await local; } catch (OperationCanceledException) when (waiting.IsCancellationRequested) { }
                await Task.Delay(150, stop.Token);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                wasOffline = true; log(e.Message);
                try { await Task.Delay(2000, stop.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }
    public async Task SyncOnceAsync(CancellationToken ct = default)
    {
        await cycle.WaitAsync(ct);
        try
        {
            using var request = Request(HttpMethod.Get, "/manifest");
            using var response = await http.SendAsync(request, ct); Check(response);
            var manifest = await response.Content.ReadFromJsonAsync<Manifest>(Protocol.Json, ct) ?? throw new IOException("Invalid manifest.");
            if (manifest.FolderId != Binding.FolderId) throw new IOException("Server folder identity changed.");
            var remote = manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            foreach (string path in remote.Keys) store.Resolve(path);
            var local = store.Scan();
            // Apply known deletions before creations so file-to-directory renames can settle.
            var paths = local.Keys.Union(remote.Keys, StringComparer.OrdinalIgnoreCase).Union(state.Files.Keys, StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => state.Files.GetValueOrDefault(p)?.Hash is not null &&
                    (local.GetValueOrDefault(p)?.Hash is null || remote.GetValueOrDefault(p)?.Hash is null) ? 0 : 1)
                .ThenByDescending(p => p.Count(c => c == '/')).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (string path in paths)
            {
                ct.ThrowIfCancellationRequested();
                local.TryGetValue(path, out var here); remote.TryGetValue(path, out var there); state.Files.TryGetValue(path, out var before);
                string? localHash = here?.Hash, remoteHash = there?.Hash;
                if (localHash == remoteHash) { Remember(path, there); continue; }
                bool localChanged = localHash != before?.Hash, remoteChanged = remoteHash != before?.Hash;
                if (localChanged && !remoteChanged)
                {
                    var updated = await UploadAsync(path, localHash, there?.Version, ct);
                    Remember(path, updated);
                }
                else
                {
                    if (localChanged && remoteChanged && localHash is not null)
                    {
                        string conflict = store.PreserveConflict(path, localHash);
                        log("Preserved competing edit: " + conflict);
                        // Publish the preserved edit before replacing the original; it survives disconnects and retries.
                        var uploaded = await UploadAsync(conflict, localHash, null, ct);
                        Remember(conflict, uploaded);
                    }
                    await DownloadAsync(path, there, localHash, ct);
                    Remember(path, there);
                }
            }
            sequence = manifest.Sequence;
        }
        finally { cycle.Release(); }
    }
    private void Remember(string path, FileEntry? entry)
    {
        var baseline = new Baseline(path, entry?.Hash, entry?.Version ?? "missing");
        if (state.Files.GetValueOrDefault(path) == baseline) return;
        state.Files[path] = baseline;
        DiskJson.Write(StatePath, state);
    }
    private async Task<FileEntry?> UploadAsync(string path, string? hash, string? expectedVersion, CancellationToken ct)
    {
        using var request = Request(hash is null ? HttpMethod.Delete : HttpMethod.Put, "/file?path=" + Uri.EscapeDataString(path));
        request.Headers.Add("X-Expected-Version", expectedVersion ?? "missing");
        FileStream? stream = null;
        try
        {
            if (hash is not null)
            {
                stream = store.OpenRead(path);
                if (FolderStore.Hash(stream) != hash) throw new IOException("File is still changing: " + path);
                stream.Position = 0;
                request.Content = new StreamContent(stream, 128 * 1024);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Headers.Add("X-Content-SHA256", hash);
            }
            else if (store.CurrentHash(path) is not null) throw new IOException("File was recreated during sync: " + path);
            using var response = await http.SendAsync(request, ct); Check(response);
            log((hash is null ? "Deleted on server: " : "Uploaded: ") + path);
            return await response.Content.ReadFromJsonAsync<FileEntry>(Protocol.Json, ct);
        }
        finally { stream?.Dispose(); }
    }
    private async Task DownloadAsync(string path, FileEntry? entry, string? expectedHash, CancellationToken ct)
    {
        if (entry?.Hash is null) { store.Apply(path, null, expectedHash); log("Deleted locally (recovery copy retained): " + path); return; }
        string temp = store.NewTemp();
        try
        {
            using var request = Request(HttpMethod.Get, "/file?path=" + Uri.EscapeDataString(path) + "&version=" + Uri.EscapeDataString(entry.Version));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); Check(response);
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                await response.Content.CopyToAsync(output, ct);
            using (var input = File.OpenRead(temp))
                if (input.Length != entry.Length || FolderStore.Hash(input) != entry.Hash) throw new IOException("Transfer checksum mismatch: " + path);
            store.Apply(path, temp, expectedHash);
            log("Downloaded: " + path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); watcher.Dispose();
        if (runner is not null) await runner;
        http.Dispose(); store.Dispose(); stop.Dispose();
    }
}
