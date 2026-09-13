using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using SingularitySync.Core;
using SingularitySync.App;

if (args.Length == 2 && args[0] == "--mutex-child")
{
    using var ownership = SingleInstance.TryAcquire(args[1]);
    Console.WriteLine(ownership is null ? "blocked" : "acquired");
    Console.ReadLine();
    return;
}

var root = Path.Combine(Path.GetTempPath(), "SingularitySync-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int passed = 0;
SyncServer? server = null;
SyncClient? client1 = null, client2 = null;
string Folder(string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
void Pass(string label) { passed++; Console.WriteLine("PASS " + label); }
void Assert(bool condition, string label) { if (!condition) throw new Exception("FAIL " + label); Pass(label); }
async Task Throws(Func<Task> action, string label)
{
    try { await action(); } catch (Exception e) when (e is IOException or HttpRequestException) { Pass(label); return; }
    throw new Exception("FAIL " + label + " (no exception)");
}
int FreePort()
{
    var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
}
async Task Eventually(Func<bool> condition, string label, int milliseconds = 8000)
{
    var sw = Stopwatch.StartNew();
    while (!condition() && sw.ElapsedMilliseconds < milliseconds) await Task.Delay(50);
    Assert(condition(), label + $" ({sw.ElapsedMilliseconds} ms)");
}
async Task CheckSingleInstance(bool crash)
{
    string name = @"Global\SingularitySync.Tests." + Guid.NewGuid().ToString("N");
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
    if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(typeof(SingleInstance).Assembly.Location);
    start.ArgumentList.Add("--mutex-child"); start.ArgumentList.Add(name);
    using var child = Process.Start(start)!;
    try
    {
        string? ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (ready != "acquired") throw new Exception("Could not start singleton test holder: " + ready);
        // Keep the kernel object alive to exercise abandoned-mutex recovery after a crash.
        using var observer = Mutex.OpenExisting(name);
        using (var duplicate = SingleInstance.TryAcquire(name))
            Assert(duplicate is null, "second process blocked" + (crash ? " before crash" : " while running"));
        if (crash) child.Kill();
        else await child.StandardInput.WriteLineAsync("exit");
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        using var next = SingleInstance.TryAcquire(name);
        Assert(next is not null, crash ? "singleton lock recovers after process crash" : "singleton lock released after normal exit");
    }
    finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
}
try
{
    // Simulate a lost first packet and an unrelated UDP payload before a valid reply.
    using (var discoveryStop = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
    using (var responder = new UdpClient(new IPEndPoint(IPAddress.Loopback, Protocol.DiscoveryPort)))
    {
        var advertised = new ServerInfo(Guid.NewGuid().ToString("N"), "Retry test", [], 45831, Guid.NewGuid().ToString("N"));
        var replies = Task.Run(async () =>
        {
            await responder.ReceiveAsync(discoveryStop.Token);
            var packet = await responder.ReceiveAsync(discoveryStop.Token);
            await responder.SendAsync("invalid json"u8.ToArray(), packet.RemoteEndPoint, discoveryStop.Token);
            byte[] payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(advertised, Protocol.Json);
            await responder.SendAsync(payload, packet.RemoteEndPoint, discoveryStop.Token);
            await responder.SendAsync(payload, packet.RemoteEndPoint, discoveryStop.Token);
        });
        var found = await Discovery.FindAsync(TimeSpan.FromSeconds(1.5));
        await replies;
        Assert(found.Count(s => s.Info.Id == advertised.Id) == 1, "discovery retries lost probes, ignores malformed replies, and deduplicates servers");
    }
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        try { await Discovery.FindAsync(TimeSpan.FromSeconds(1), cancelled.Token); throw new Exception("Discovery ignored cancellation"); }
        catch (OperationCanceledException) { Pass("discovery propagates caller cancellation"); }
    }
    Assert((await Discovery.FindAsync(TimeSpan.FromMilliseconds(50))).Count == 0, "discovery timeout without responders returns an empty list");
    string hf = Folder("history-unit");
    string nested = Path.Combine(hf, "nested");
    Directory.CreateDirectory(nested);
    string tracked = Path.Combine(nested, "document.txt");
    string historyRoot = Path.Combine(hf, FolderStore.MetadataName, "history");
    using (var historyStore = new FolderStore(hf))
    {
        var history = new FileHistory(historyStore);
        for (int i = 0; i < 12; i++)
        {
            File.WriteAllText(tracked, "version " + i);
            historyStore.Invalidate();
            history.Capture(historyStore.Scan()["nested/document.txt"]);
        }
        var snapshots = Directory.GetFiles(historyRoot, "document.txt", SearchOption.AllDirectories);
        Assert(snapshots.Length == 10 && snapshots.Select(File.ReadAllText).ToHashSet().SetEquals(Enumerable.Range(2, 10).Select(i => "version " + i)), "history retains exactly the ten newest contents");
        history.Capture(historyStore.Scan()["nested/document.txt"]);
        Assert(Directory.GetFiles(historyRoot, "document.txt", SearchOption.AllDirectories).Length == 10, "unchanged scan does not duplicate history");
        File.WriteAllText(tracked, "pending version");
        historyStore.Invalidate();
        var stale = historyStore.Scan()["nested/document.txt"];
        File.WriteAllText(tracked, "changed during capture");
        await Throws(() => { history.Capture(stale); return Task.CompletedTask; }, "changing file cannot commit an unverified snapshot");
        Assert(Directory.GetFiles(historyRoot, "document.txt", SearchOption.AllDirectories).Length == 10, "failed capture preserves previous history");
        Assert(historyStore.Scan().Count == 1, "history excluded from file scan");
        string oldest = snapshots.Single(p => File.ReadAllText(p) == "version 2");
        using (var locked = new FileStream(oldest, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            historyStore.Invalidate();
            await Throws(() => { history.Capture(historyStore.Scan()["nested/document.txt"]); return Task.CompletedTask; }, "locked oldest snapshot defers pruning");
            Assert(File.ReadAllText(oldest) == "version 2" && Directory.GetFiles(historyRoot, "document.txt", SearchOption.AllDirectories).Any(p => File.ReadAllText(p) == "changed during capture"), "failed prune retains old copy and committed new snapshot");
        }
        history.Capture(historyStore.Scan()["nested/document.txt"]);
        Assert(Directory.GetFiles(historyRoot, "document.txt", SearchOption.AllDirectories).Length == 10, "next scan retries interrupted pruning without a duplicate snapshot");
    }
    using (var historyStore = new FolderStore(hf))
    {
        var history = new FileHistory(historyStore);
        history.Capture(historyStore.Scan()["nested/document.txt"]);
        Assert(Directory.GetFiles(historyRoot, "document.txt", SearchOption.AllDirectories).Length == 10, "history retention survives restart");
        File.Delete(tracked);
        Assert(historyStore.Scan().Count == 0 && Directory.GetFiles(historyRoot, "document.txt", SearchOption.AllDirectories).Length == 10, "deletion retains saved snapshots");
    }
    var clock = new TestClock();
    var telemetry = new SyncActivity(clock);
    using (telemetry.Begin("peer", "Office PC", "192.0.2.1", true))
    {
        Parallel.For(0, 1000, _ => telemetry.Add("peer", 100, true));
        telemetry.Add("peer", 200, false);
        var sample = telemetry.Snapshot();
        Assert(sample.Uploaded == 100_000 && sample.Downloaded == 200 && sample.UploadRate == 50_000 && sample.DownloadRate == 100, "concurrent byte counters and rate calculation");
        Assert(sample.Peers.Single().Status == "Syncing", "active transfer shows syncing");
    }
    Assert(telemetry.Snapshot().Peers.Single().Status == "Idle", "completed transfer shows idle");
    clock.Advance(TimeSpan.FromSeconds(3));
    Assert(telemetry.Snapshot().UploadRate == 0 && telemetry.Snapshot().Uploaded == 100_000, "idle rates decay without losing session totals");
    clock.Advance(TimeSpan.FromSeconds(43));
    Assert(telemetry.Snapshot().Peers.Single().Status == "Offline", "silent peers expire after 45 seconds");
    telemetry.Seen("peer", "Renamed PC", "192.0.2.2");
    Assert(telemetry.Snapshot().Peers.Single() is { Name: "Renamed PC", Status: "Idle", Address: "192.0.2.2" }, "peer reconnect updates name and address");
    telemetry.Stop();
    Assert(telemetry.Snapshot().Peers.Single().Status == "Offline" && telemetry.Snapshot().Uploaded == 100_000, "stop marks peers offline and retains totals");
    await CheckSingleInstance(false);
    await CheckSingleInstance(true);
    string sf = Folder("server"), cf1 = Folder("client1"), cf2 = Folder("client2");
    File.WriteAllText(Path.Combine(sf, "hello.txt"), "server original");
    File.WriteAllText(Path.Combine(cf1, "local.txt"), "client original");
    var settings = new Settings();
    string settingsPath = Path.Combine(root, "settings.json");
    int port = FreePort();
    server = new(sf, settings, port: port, saveSettings: () => DiskJson.Write(settingsPath, settings));
    await server.StartAsync();
    await Throws(async () => await SyncClient.PairAsync("127.0.0.1", port, Guid.NewGuid().ToString("N"), "wrong"), "incorrect pairing code rejected");
    var b1 = await SyncClient.PairAsync("127.0.0.1", port, Guid.NewGuid().ToString("N"), server.PairingCode);
    var b2 = await SyncClient.PairAsync("127.0.0.1", port, Guid.NewGuid().ToString("N"), server.PairingCode);
    Pass("one-time pairing issued two independent tokens");
    using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
    using (var unauthorized = await http.GetAsync("/manifest")) Assert(unauthorized.StatusCode == HttpStatusCode.Unauthorized, "unpaired manifest access denied");
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", b1.Token);
    http.DefaultRequestHeaders.Add("X-Sync-Folder", b1.FolderId);
    client1 = new(cf1, b1); client2 = new(cf2, b2);
    await client1.EnsureConnectedAsync(CancellationToken.None);
    await client1.SyncOnceAsync(); await client2.SyncOnceAsync();
    Assert(File.ReadAllText(Path.Combine(cf1, "hello.txt")) == "server original" && File.ReadAllText(Path.Combine(sf, "local.txt")) == "client original" && File.Exists(Path.Combine(cf2, "local.txt")), "initial two-way merge and fan-out");
    Assert(Directory.GetFiles(Path.Combine(sf, FolderStore.MetadataName, "history"), "hello.txt", SearchOption.AllDirectories).Any(p => File.ReadAllText(p) == "server original"), "server seeds existing files into history");
    Assert(!Directory.Exists(Path.Combine(cf1, FolderStore.MetadataName, "history")) && !Directory.Exists(Path.Combine(cf2, FolderStore.MetadataName, "history")), "clients neither create nor receive history");
    var initialServer = server.Activity.Snapshot();
    var initialClient1 = client1.Activity.Snapshot();
    var initialClient2 = client2.Activity.Snapshot();
    Assert(initialServer.Peers.Count == 2 && initialServer.Peers.All(p => p.Name == Environment.MachineName && p.Status == "Idle"), "authenticated clients report PC names and idle status");
    Assert(initialClient1.Peers.Single().Name == Environment.MachineName, "client shows server PC name");
    Assert(initialClient1.Uploaded == new FileInfo(Path.Combine(cf1, "local.txt")).Length && initialClient1.Downloaded == new FileInfo(Path.Combine(sf, "hello.txt")).Length, "initial counters count file payload only");
    Assert(initialServer.Uploaded == initialClient1.Downloaded + initialClient2.Downloaded && initialServer.Downloaded == initialClient1.Uploaded + initialClient2.Uploaded, "server counters match both clients in opposite directions");
    Directory.CreateDirectory(Path.Combine(cf1, "nested"));
    byte[] bytes = RandomNumberGenerator.GetBytes(4 * 1024 * 1024);
    File.WriteAllBytes(Path.Combine(cf1, "nested", "日本語 #%.bin"), bytes);
    File.WriteAllBytes(Path.Combine(cf1, "empty.txt"), []);
    await client1.SyncOnceAsync(); await client2.SyncOnceAsync();
    Assert(File.ReadAllBytes(Path.Combine(cf2, "nested", "日本語 #%.bin")).SequenceEqual(bytes) && new FileInfo(Path.Combine(cf2, "empty.txt")).Length == 0, "binary streaming, nested Unicode paths, and zero-byte files");
    Assert(client1.Activity.Snapshot().Uploaded - initialClient1.Uploaded == bytes.Length && client2.Activity.Snapshot().Downloaded - initialClient2.Downloaded == bytes.Length, "streaming counters count binary and empty files exactly once");
    File.WriteAllText(Path.Combine(sf, "hello.txt"), "server edited");
    await client1.SyncOnceAsync();
    Assert(File.ReadAllText(Path.Combine(cf1, "hello.txt")) == "server edited", "server edits reach client");
    Assert(Directory.GetFiles(Path.Combine(sf, FolderStore.MetadataName, "history"), "hello.txt", SearchOption.AllDirectories).Select(File.ReadAllText).ToHashSet().SetEquals(["server original", "server edited"]), "direct server edits preserve both observed versions");
    Assert(Directory.GetFiles(Path.Combine(cf1, FolderStore.MetadataName, "recovery"), "hello.txt", SearchOption.AllDirectories).Any(p => File.ReadAllText(p) == "server original"), "client recovery behavior is unchanged");
    File.WriteAllText(Path.Combine(cf1, "local.txt"), "client edited");
    await client1.SyncOnceAsync();
    Assert(File.ReadAllText(Path.Combine(sf, "local.txt")) == "client edited", "client edits reach server");
    Assert(Directory.EnumerateFiles(Path.Combine(sf, ".singularity-sync", "history"), "local.txt", SearchOption.AllDirectories).Any(p => File.ReadAllText(p) == "client original"), "server retains overwritten version");
    File.WriteAllText(Path.Combine(sf, "hello.txt"), "server conflict winner");
    File.WriteAllText(Path.Combine(cf1, "hello.txt"), "offline client edit");
    await client1.SyncOnceAsync();
    Assert(File.ReadAllText(Path.Combine(cf1, "hello.txt")) == "server conflict winner" && Directory.GetFiles(sf, "hello (conflict *").Any(p => File.ReadAllText(p) == "offline client edit"), "concurrent edits preserve and publish client conflict copy");
    File.Delete(Path.Combine(cf1, "hello.txt"));
    await client1.SyncOnceAsync(); await client2.SyncOnceAsync();
    Assert(!File.Exists(Path.Combine(sf, "hello.txt")) && !File.Exists(Path.Combine(cf2, "hello.txt")), "client deletion propagates to server and peers");
    Assert(Directory.GetFiles(Path.Combine(sf, FolderStore.MetadataName, "history"), "hello.txt", SearchOption.AllDirectories).Any(p => File.ReadAllText(p) == "server conflict winner"), "client deletion retains final server snapshot");
    Assert(!Directory.Exists(Path.Combine(sf, FolderStore.MetadataName, "recovery")), "server does not accumulate duplicate unlimited recovery copies");
    File.Delete(Path.Combine(sf, "local.txt"));
    File.WriteAllText(Path.Combine(cf1, "local.txt"), "offline versus deletion");
    await client1.SyncOnceAsync();
    Assert(!File.Exists(Path.Combine(cf1, "local.txt")) && Directory.GetFiles(sf, "local (conflict *").Any(p => File.ReadAllText(p) == "offline versus deletion"), "server deletion versus local edit preserves local contents");
    File.WriteAllText(Path.Combine(sf, "delete-edit.txt"), "baseline");
    await client1.SyncOnceAsync();
    File.Delete(Path.Combine(cf1, "delete-edit.txt"));
    File.WriteAllText(Path.Combine(sf, "delete-edit.txt"), "remote survives");
    await client1.SyncOnceAsync();
    Assert(File.ReadAllText(Path.Combine(cf1, "delete-edit.txt")) == "remote survives", "server edit wins over concurrent client deletion");
    File.Move(Path.Combine(cf1, "delete-edit.txt"), Path.Combine(cf1, "renamed.txt"));
    await client1.SyncOnceAsync();
    Assert(!File.Exists(Path.Combine(sf, "delete-edit.txt")) && File.ReadAllText(Path.Combine(sf, "renamed.txt")) == "remote survives", "rename propagates");
    Directory.CreateDirectory(Path.Combine(cf1, "shape"));
    File.WriteAllText(Path.Combine(cf1, "shape", "child.txt"), "nested");
    await client1.SyncOnceAsync();
    Directory.Delete(Path.Combine(cf1, "shape"), true);
    File.WriteAllText(Path.Combine(cf1, "shape"), "now a file");
    await client1.SyncOnceAsync();
    Assert(File.ReadAllText(Path.Combine(sf, "shape")) == "now a file", "directory replaced by a file");
    File.Delete(Path.Combine(cf1, "shape"));
    Directory.CreateDirectory(Path.Combine(cf1, "shape"));
    File.WriteAllText(Path.Combine(cf1, "shape", "child.txt"), "now nested again");
    await client1.SyncOnceAsync();
    Assert(File.ReadAllText(Path.Combine(sf, "shape", "child.txt")) == "now nested again", "file replaced by a directory");
    var manifest = (await http.GetFromJsonAsync<Manifest>("/manifest", Protocol.Json))!;
    var entry = manifest.Files.Single(f => f.Path == "renamed.txt");
    using (var stale = new HttpRequestMessage(HttpMethod.Put, "/file?path=renamed.txt"))
    {
        stale.Headers.Add("X-Expected-Version", "stale"); stale.Headers.Add("X-Content-SHA256", Convert.ToHexString(SHA256.HashData("bad"u8)));
        stale.Content = new StringContent("bad");
        using var response = await http.SendAsync(stale);
        Assert(response.StatusCode == HttpStatusCode.Conflict && File.ReadAllText(Path.Combine(sf, "renamed.txt")) == "remote survives", "stale writes cannot overwrite newer files");
    }
    using (var corrupt = new HttpRequestMessage(HttpMethod.Put, "/file?path=renamed.txt"))
    {
        corrupt.Headers.Add("X-Expected-Version", entry.Version); corrupt.Headers.Add("X-Content-SHA256", "incorrect"); corrupt.Content = new StringContent("bad");
        using var response = await http.SendAsync(corrupt);
        Assert(response.StatusCode == HttpStatusCode.BadRequest && File.ReadAllText(Path.Combine(sf, "renamed.txt")) == "remote survives", "bad transfer checksum rejected without changing destination");
    }
    using (var wrongFolder = new HttpRequestMessage(HttpMethod.Get, "/manifest"))
    {
        wrongFolder.Headers.Add("X-Sync-Folder", "wrong");
        using var response = await http.SendAsync(wrongFolder);
        Assert(response.StatusCode == HttpStatusCode.PreconditionFailed, "folder identity prevents syncing a different server folder");
    }
    using (var traversal = new HttpRequestMessage(HttpMethod.Delete, "/file?path=" + Uri.EscapeDataString("../escape.txt")))
    {
        traversal.Headers.Add("X-Expected-Version", "missing");
        using var response = await http.SendAsync(traversal);
        Assert(response.StatusCode == HttpStatusCode.Conflict && !File.Exists(Path.Combine(root, "escape.txt")), "network path traversal blocked");
    }
    long receivedBeforeInterruption = server.Activity.Snapshot().Downloaded;
    using (var socket = new TcpClient())
    {
        await socket.ConnectAsync(IPAddress.Loopback, port);
        string header = $"PUT /file?path=renamed.txt HTTP/1.1\r\nHost: localhost\r\nAuthorization: Bearer {b1.Token}\r\nX-Sync-Folder: {b1.FolderId}\r\nX-Expected-Version: {entry.Version}\r\nX-Content-SHA256: unused\r\nContent-Length: 100000\r\n\r\npartial";
        await socket.GetStream().WriteAsync(System.Text.Encoding.ASCII.GetBytes(header));
        await Eventually(() => Directory.EnumerateFiles(Path.Combine(sf, ".singularity-sync", "tmp")).Any(), "interrupted upload enters staging");
        await Eventually(() => server.Activity.Snapshot().Downloaded == receivedBeforeInterruption + 7, "partial transfer is counted before completion");
        Assert(server.Activity.Snapshot().Peers.Any(p => p.Status == "Syncing"), "in-flight network upload shows syncing");
    }
    await Eventually(() => !Directory.EnumerateFiles(Path.Combine(sf, ".singularity-sync", "tmp")).Any(), "interrupted upload staging is cleaned up");
    await Eventually(() => server.Activity.Snapshot().Peers.All(p => p.Status == "Idle"), "interrupted transfer releases syncing status");
    Assert(File.ReadAllText(Path.Combine(sf, "renamed.txt")) == "remote survives", "interrupted transfer leaves destination intact");
    File.WriteAllText(Path.Combine(sf, "simultaneous.txt"), "baseline");
    await client1.SyncOnceAsync(); await client2.SyncOnceAsync();
    File.WriteAllText(Path.Combine(cf1, "simultaneous.txt"), "edit one");
    File.WriteAllText(Path.Combine(cf2, "simultaneous.txt"), "edit two");
    async Task Attempt(SyncClient client) { try { await client.SyncOnceAsync(); } catch (IOException) { } }
    await Task.WhenAll(Attempt(client1), Attempt(client2));
    await client1.SyncOnceAsync(); await client2.SyncOnceAsync(); await client1.SyncOnceAsync();
    var contents = Directory.GetFiles(sf, "simultaneous*").Select(File.ReadAllText).ToHashSet();
    Assert(contents.Contains("edit one") && contents.Contains("edit two"), "simultaneous client uploads preserve both edits");
    using (var paths = new FolderStore(Folder("path-tests")))
    {
        foreach (string bad in new[] { "../escape", "/absolute", "a/../../b", "a\\b", ".singularity-sync/server-state.json", "a/.SINGULARITY-SYNC/key", "CON", "COM1.txt", "a:stream", "trailing.", "trailing ", "a//b" })
            await Throws(() => { paths.Resolve(bad); return Task.CompletedTask; }, "reject unsafe path " + bad);
        File.WriteAllText(paths.Resolve("race.txt"), "latest");
        string staged = paths.NewTemp(); File.WriteAllText(staged, "old download");
        await Throws(() => { paths.Apply("race.txt", staged, "outdated"); return Task.CompletedTask; }, "local edit during download cannot be replaced");
        Assert(File.ReadAllText(paths.Resolve("race.txt")) == "latest", "local race preserves latest contents");
        await Throws(() => { using var duplicate = new FolderStore(paths.Root); return Task.CompletedTask; }, "second process cannot own the same folder");
    }
    var discovered = await Discovery.FindAsync(TimeSpan.FromSeconds(1));
    Assert(discovered.Any(s => s.Info.Id == settings.DeviceId && s.Info.Macs.SequenceEqual(server.Info.Macs)), "LAN discovery advertises stable ID and MAC addresses");
    await client2.DisposeAsync(); client2 = null;
    Binding? relocated = null;
    client2 = new(cf2, b2 with { Address = "127.0.0.2", Port = FreePort() }, saveBinding: b => relocated = b);
    await client2.EnsureConnectedAsync(CancellationToken.None);
    Assert(relocated?.ServerId == b2.ServerId && relocated.Port == port, "stale endpoint rediscovered by server ID and MAC, then persisted");
    await client2.SyncOnceAsync();
    Pass("saved pairing token works after endpoint change");
    await client1.DisposeAsync(); client1 = null;
    await client2.DisposeAsync(); client2 = null;
    await server.DisposeAsync(); server = null;
    File.WriteAllText(Path.Combine(cf1, "offline.txt"), "written while offline");
    settings = DiskJson.Read<Settings>(settingsPath)!;
    server = new(sf, settings, port: port, saveSettings: () => DiskJson.Write(settingsPath, settings)); await server.StartAsync();
    client1 = new(cf1, b1); await client1.EnsureConnectedAsync(CancellationToken.None); await client1.SyncOnceAsync();
    Assert(File.ReadAllText(Path.Combine(sf, "offline.txt")) == "written while offline", "restart preserves identity, authorization, and offline baseline");
    Assert(Directory.GetFiles(Path.Combine(sf, FolderStore.MetadataName, "history"), "hello.txt", SearchOption.AllDirectories).Any(p => File.ReadAllText(p) == "server original"), "deleted file history survives server restart");
    client1.Start();
    var timer = Stopwatch.StartNew();
    File.WriteAllText(Path.Combine(sf, "watch-server.txt"), "watcher push");
    await Eventually(() => File.Exists(Path.Combine(cf1, "watch-server.txt")), "server watcher wakes connected client");
    File.WriteAllText(Path.Combine(cf1, "watch-client.txt"), "watcher upload");
    await Eventually(() => File.Exists(Path.Combine(sf, "watch-client.txt")), "client watcher uploads automatically");
    await client1.DisposeAsync(); client1 = null;
    server.ForgetClients();
    Assert(server.Activity.Snapshot().Peers.Count == 0, "revocation removes peers from activity");
    client1 = new(cf1, b1);
    await Throws(() => client1.SyncOnceAsync(), "revoked token is rejected");
    Console.WriteLine($"\nAll {passed} checks passed.");
}
finally
{
    if (client1 is not null) await client1.DisposeAsync();
    if (client2 is not null) await client2.DisposeAsync();
    if (server is not null) await server.DisposeAsync();
    // Only delete the uniquely generated temporary test directory.
    if (Path.GetDirectoryName(root) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) && Path.GetFileName(root).StartsWith("SingularitySync-tests-")) Directory.Delete(root, true);
}

sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan duration) => now += duration;
}
