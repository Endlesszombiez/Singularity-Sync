using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace SingularitySync.Core;

public sealed class SyncServer : IAsyncDisposable
{
    private readonly FolderStore store;
    private readonly FileHistory history;
    private readonly Settings settings;
    private readonly Action<string> log;
    private readonly Action saveSettings;
    private readonly SemaphoreSlim gate = new(1, 1), wake = new(0, 1);
    private readonly CancellationTokenSource stop = new();
    private readonly object pairingLock;
    private readonly Dictionary<string, (DateTime Start, int Count)> attempts = new();
    private ServerState state;
    private TaskCompletionSource changed = NewSignal();
    private WebApplication? app;
    private FileSystemWatcher? watcher;
    private Task? monitor, discovery;
    public int Port { get; }
    public SyncActivity Activity { get; }
    public string PairingCode { get; private set; } = "";
    public DateTime CodeExpiresUtc { get; private set; }
    public ServerInfo Info => new(settings.DeviceId, Environment.MachineName, Protocol.MacAddresses(), Port, state.FolderId);
    private string StatePath => Path.Combine(store.Metadata, "server-state.json");
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public SyncServer(string folder, Settings settings, Action<string>? log = null, int port = Protocol.HttpPort, Action? saveSettings = null, SyncActivity? activity = null)
    {
        Activity = activity ?? new();
        this.settings = settings; pairingLock = settings; this.log = log ?? (_ => { }); this.saveSettings = saveSettings ?? settings.Save; Port = port;
        store = new(folder);
        try
        {
            history = new(store);
            state = DiskJson.Read<ServerState>(StatePath) ?? new();
            state.Files = new(state.Files, StringComparer.OrdinalIgnoreCase);
            DiskJson.Write(StatePath, state);
            RotatePairingCode();
        }
        catch { store.Dispose(); throw; }
    }
    public void RotatePairingCode()
    {
        lock (pairingLock)
        {
            PairingCode = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            CodeExpiresUtc = DateTime.UtcNow.AddMinutes(10);
        }
    }
    public void ForgetClients()
    {
        lock (pairingLock) { settings.Clients.Clear(); Activity.ClearPeers(); saveSettings(); }
        RotatePairingCode();
    }
    public async Task StartAsync(bool enableDiscovery = true)
    {
        await gate.WaitAsync();
        try { Scan(); } finally { gate.Release(); }
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => { k.ListenAnyIP(Port); k.Limits.MaxRequestBodySize = null; });
        app = builder.Build();
        app.Use(async (context, next) =>
        {
            try
            {
                if (context.Request.Path != "/info" && context.Request.Path != "/pair")
                {
                    string token = context.Request.Headers.Authorization.ToString();
                    string? peerId;
                    lock (pairingLock) peerId = token.StartsWith("Bearer ", StringComparison.Ordinal) ? settings.Clients.FirstOrDefault(p => p.Value == token[7..]).Key : null;
                    if (peerId is null) { context.Response.StatusCode = 401; return; }
                    if (context.Request.Headers["X-Sync-Folder"] != state.FolderId) { context.Response.StatusCode = 412; return; }
                    string? name = null;
                    string encodedName = context.Request.Headers["X-Sync-Name"].ToString();
                    if (encodedName.Length <= 1024) name = Uri.UnescapeDataString(encodedName);
                    using var presence = Activity.Begin(peerId, name, context.Connection.RemoteIpAddress?.ToString() ?? "", context.Request.Path == "/file");
                    context.Items["SyncPeerId"] = peerId;
                    await next(context);
                    return;
                }
                await next(context);
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or BadHttpRequestException)
            {
                log("Request deferred: " + e.Message);
                if (!context.Response.HasStarted) { context.Response.StatusCode = 409; await context.Response.WriteAsync("File unavailable or changed; retry."); }
            }
        });
        app.MapGet("/info", () => Info);
        app.MapPost("/pair", (PairRequest request, HttpContext context) =>
        {
            lock (pairingLock)
            {
                string ip = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                if (attempts.Count > 4096) attempts.Clear();
                attempts.TryGetValue(ip, out var attempt);
                if (DateTime.UtcNow - attempt.Start > TimeSpan.FromMinutes(1)) attempt = (DateTime.UtcNow, 0);
                attempts[ip] = (attempt.Start, attempt.Count + 1);
                if (attempt.Count >= 5) return Results.StatusCode(429);
                if (!Guid.TryParse(request.ClientId, out _) || DateTime.UtcNow > CodeExpiresUtc || request.Code != PairingCode) return Results.Unauthorized();
                string token = DiskJson.Token();
                settings.Clients[request.ClientId] = token;
                saveSettings();
                log("Paired a client at " + ip);
                return Results.Json(new PairResponse(Info, token), Protocol.Json);
            }
        });
        app.MapGet("/manifest", async () =>
        {
            await gate.WaitAsync(stop.Token);
            try { Scan(); return Results.Json(new Manifest(state.FolderId, state.Sequence, state.Files.Values.ToList()), Protocol.Json); }
            finally { gate.Release(); }
        });
        app.MapGet("/changes", async (long since, HttpContext context) =>
        {
            Task signal;
            await gate.WaitAsync(context.RequestAborted);
            try { if (state.Sequence != since) return Results.Ok(); signal = changed.Task; }
            finally { gate.Release(); }
            try { await signal.WaitAsync(TimeSpan.FromSeconds(25), context.RequestAborted); }
            catch (TimeoutException) { }
            return Results.Ok();
        });
        app.MapGet("/file", async (string path, string version, HttpContext context) =>
        {
            await gate.WaitAsync(context.RequestAborted);
            try
            {
                Scan();
                if (!state.Files.TryGetValue(path, out var entry) || entry.Deleted || entry.Version != version) return Results.Conflict();
                var stream = store.OpenRead(path);
                try
                {
                    if (FolderStore.Hash(stream) != entry.Hash) { stream.Dispose(); return Results.Conflict(); }
                    stream.Position = 0;
                    return Results.Stream(new CountingStream(stream, n => Activity.Add((string)context.Items["SyncPeerId"]!, n, true)), "application/octet-stream");
                }
                catch { stream.Dispose(); throw; }
            }
            finally { gate.Release(); }
        });
        app.MapPut("/file", async (string path, HttpContext context) =>
        {
            store.Resolve(path);
            string temp = store.NewTemp();
            try
            {
                await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, true))
                {
                    await using var counted = new CountingStream(output, n => Activity.Add((string)context.Items["SyncPeerId"]!, n, false), leaveOpen: true);
                    await context.Request.Body.CopyToAsync(counted, context.RequestAborted);
                }
                string hash;
                using (var input = File.OpenRead(temp)) hash = FolderStore.Hash(input);
                if (context.Request.Headers["X-Content-SHA256"] != hash) return Results.BadRequest("Content checksum mismatch.");
                await gate.WaitAsync(context.RequestAborted);
                try
                {
                    store.Invalidate(); Scan();
                    state.Files.TryGetValue(path, out var previous);
                    if (context.Request.Headers["X-Expected-Version"] != (previous?.Version ?? "missing")) return Results.Conflict();
                    store.Apply(path, temp, previous?.Hash, retainRecovery: false);
                    Scan();
                    log("Received " + path);
                    return Results.Json(state.Files[path], Protocol.Json);
                }
                finally { gate.Release(); }
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        });
        app.MapDelete("/file", async (string path, HttpContext context) =>
        {
            await gate.WaitAsync(context.RequestAborted);
            try
            {
                store.Invalidate(); Scan();
                state.Files.TryGetValue(path, out var previous);
                if (context.Request.Headers["X-Expected-Version"] != (previous?.Version ?? "missing")) return Results.Conflict();
                store.Apply(path, null, previous?.Hash, retainRecovery: false);
                Scan();
                log("Deleted " + path + " (file history retained)");
                return Results.Json(state.Files.GetValueOrDefault(path), Protocol.Json);
            }
            finally { gate.Release(); }
        });
        await app.StartAsync(stop.Token);
        watcher = new(store.Root) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
        watcher.Changed += OnChange; watcher.Created += OnChange; watcher.Deleted += OnChange; watcher.Renamed += OnChange;
        watcher.Error += (_, _) => Wake();
        watcher.EnableRaisingEvents = true;
        monitor = MonitorAsync();
        if (enableDiscovery) discovery = RunDiscoveryAsync();
        log($"Server ready on TCP {Port}; discovery on UDP {Protocol.DiscoveryPort}.");
    }
    private void OnChange(object sender, FileSystemEventArgs e)
    {
        if (!e.FullPath.StartsWith(store.Metadata + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { store.Invalidate(); Wake(); }
    }
    private void Wake() { try { wake.Release(); } catch (SemaphoreFullException) { } }
    private async Task RunDiscoveryAsync()
    {
        try { await Discovery.ServeAsync(() => Info, stop.Token); }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception e) { log("Discovery unavailable; manual IP pairing still works. " + e.Message); }
    }
    private async Task MonitorAsync()
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await wake.WaitAsync(TimeSpan.FromSeconds(2), stop.Token);
                await Task.Delay(150, stop.Token);
                await gate.WaitAsync(stop.Token);
                try { Scan(); } finally { gate.Release(); }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { break; }
            catch (Exception e) { log("Waiting for folder: " + e.Message); }
        }
    }
    private void Scan()
    {
        var current = store.Scan();
        // Seed existing files too, including when upgrading an existing server state.
        // A failed snapshot must not publish a revision that has no history.
        foreach (var file in current.Values) history.Capture(file);
        bool dirty = false;
        foreach (var file in current.Values)
        {
            if (!state.Files.TryGetValue(file.Path, out var old) || old.Hash != file.Hash)
            {
                state.Files[file.Path] = file with { Version = Guid.NewGuid().ToString("N") };
                dirty = true;
            }
        }
        foreach (var old in state.Files.Values.Where(f => !f.Deleted && !current.ContainsKey(f.Path)).ToList())
        {
            state.Files[old.Path] = old with { Hash = null, Length = 0, Version = Guid.NewGuid().ToString("N") };
            dirty = true;
        }
        if (dirty)
        {
            state.Sequence++;
            DiskJson.Write(StatePath, state);
            var previous = changed; changed = NewSignal(); previous.TrySetResult();
        }
    }
    public async ValueTask DisposeAsync()
    {
        stop.Cancel(); watcher?.Dispose();
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        if (monitor is not null) await monitor;
        if (discovery is not null) await discovery;
        Activity.Stop();
        store.Dispose(); stop.Dispose();
    }
}
