namespace SingularitySync.Core;

public sealed record PeerActivity(string Id, string Name, string Address, string Status);
public sealed record ActivitySnapshot(long Uploaded, long Downloaded, double UploadRate, double DownloadRate, IReadOnlyList<PeerActivity> Peers);

/// <summary>File payload counters and recent authenticated peer activity, scoped to an app session.</summary>
public sealed class SyncActivity(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, Peer> peers = new();
    private readonly (long Second, long Up, long Down)[] samples = new (long, long, long)[2];
    private long uploaded, downloaded;
    private sealed class Peer
    {
        public string Name = "", Address = "";
        public DateTimeOffset Seen;
        public int Transfers;
        public bool Offline;
    }
    public void Seen(string id, string? name, string address)
    {
        lock (gate)
        {
            if (!peers.TryGetValue(id, out var peer)) peers[id] = peer = new();
            if (!string.IsNullOrWhiteSpace(name)) peer.Name = CleanName(name);
            if (peer.Name.Length == 0) peer.Name = "Unknown PC";
            peer.Address = address; peer.Seen = clock.GetUtcNow(); peer.Offline = false;
        }
    }
    public static string CleanName(string name) => new(name.Where(c => !char.IsControl(c)).Take(80).ToArray());
    public IDisposable Begin(string id, string? name, string address, bool transferring = false)
    {
        Peer current;
        lock (gate)
        {
            Seen(id, name, address);
            current = peers[id];
            if (transferring) current.Transfers++;
        }
        return new Lease(() =>
        {
            lock (gate)
            {
                if (!peers.TryGetValue(id, out var peer) || !ReferenceEquals(peer, current)) return;
                if (transferring) peer.Transfers--;
                peer.Seen = clock.GetUtcNow();
            }
        });
    }
    public void Add(string id, int bytes, bool upload)
    {
        if (bytes <= 0) return;
        lock (gate)
        {
            if (upload) uploaded += bytes; else downloaded += bytes;
            long second = clock.GetUtcNow().ToUnixTimeSeconds();
            int index = (int)(second & 1);
            ref var sample = ref samples[index];
            if (sample.Second != second) sample = (second, 0, 0);
            if (upload) sample.Up += bytes; else sample.Down += bytes;
            if (peers.TryGetValue(id, out var peer)) peer.Seen = clock.GetUtcNow();
        }
    }
    public void Offline(string id) { lock (gate) { if (peers.TryGetValue(id, out var peer)) peer.Offline = true; } }
    public void Stop() { lock (gate) { foreach (var peer in peers.Values) peer.Offline = true; Array.Clear(samples); } }
    public void ClearPeers() { lock (gate) peers.Clear(); }
    public ActivitySnapshot Snapshot()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            long second = now.ToUnixTimeSeconds();
            return new(uploaded, downloaded, samples.Where(s => s.Second >= second - 1).Sum(s => s.Up) / 2d, samples.Where(s => s.Second >= second - 1).Sum(s => s.Down) / 2d,
                peers.Select(p => new PeerActivity(p.Key, p.Value.Name, p.Value.Address,
                    p.Value.Offline || now - p.Value.Seen > TimeSpan.FromSeconds(45) ? "Offline" : p.Value.Transfers > 0 ? "Syncing" : "Idle"))
                    .OrderBy(p => p.Status == "Offline").ThenBy(p => p.Name).ToArray());
        }
    }
    private sealed class Lease(Action release) : IDisposable
    {
        private Action? release = release;
        public void Dispose() => Interlocked.Exchange(ref release, null)?.Invoke();
    }
}

/// <summary>Counts streamed payload chunks without buffering whole files.</summary>
internal sealed class CountingStream(Stream inner, Action<int> count, bool leaveOpen = false) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override int Read(byte[] buffer, int offset, int size) { int n = inner.Read(buffer, offset, size); count(n); return n; }
    public override int Read(Span<byte> buffer) { int n = inner.Read(buffer); count(n); return n; }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    { int n = await inner.ReadAsync(buffer, ct); count(n); return n; }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int size, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, size), ct).AsTask();
    public override void Write(byte[] buffer, int offset, int size) { inner.Write(buffer, offset, size); count(size); }
    public override void Write(ReadOnlySpan<byte> buffer) { inner.Write(buffer); count(buffer.Length); }
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    { await inner.WriteAsync(buffer, ct); count(buffer.Length); }
    public override Task WriteAsync(byte[] buffer, int offset, int size, CancellationToken ct) => WriteAsync(buffer.AsMemory(offset, size), ct).AsTask();
    protected override void Dispose(bool disposing) { if (disposing && !leaveOpen) inner.Dispose(); base.Dispose(disposing); }
    public override async ValueTask DisposeAsync() { if (!leaveOpen) await inner.DisposeAsync(); GC.SuppressFinalize(this); }
}
