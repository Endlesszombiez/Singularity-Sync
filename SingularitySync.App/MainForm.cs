using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SingularitySync.Core;

namespace SingularitySync.App;

public sealed class MainForm : Form
{
    private readonly Settings settings;
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly TextBox folder = new() { Dock = DockStyle.Fill, PlaceholderText = "Choose a folder on this computer" };
    private readonly Button browse = MakeButton("Browse…");
    private readonly Button start = MakeButton("Start server", true), stop = MakeButton("Stop");
    private readonly Label status = new() { Text = "Stopped", AutoSize = true, ForeColor = Color.FromArgb(80, 95, 115), Margin = new(12, 10, 0, 0) };
    private readonly Label code = new() { Text = "Start the server to generate a code", AutoSize = true, Font = new("Segoe UI", 15, FontStyle.Bold), Margin = new(0, 8, 0, 8) };
    private readonly Label addresses = new() { AutoSize = true, MaximumSize = new(680, 0), Text = "The server owns the shared folder. Paired clients can read, edit, and delete its files." };
    private readonly Button rotate = MakeButton("New pairing code"), revoke = MakeButton("Forget all clients");
    private readonly ComboBox servers = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 465 };
    private readonly Button search = MakeButton("Search LAN");
    private readonly TextBox address = new() { Width = 210, PlaceholderText = "Server IP address" };
    private readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535, Value = Protocol.HttpPort, Width = 85 };
    private readonly TextBox pairCode = new() { Width = 120, PlaceholderText = "6-digit code", MaxLength = 6 };
    private readonly Button pair = MakeButton("Pair & start", true);
    private readonly Label paired = new() { AutoSize = true, MaximumSize = new(680, 0) };
    private readonly Panel serverPanel = new() { Dock = DockStyle.Fill };
    private readonly Panel clientPanel = new() { Dock = DockStyle.Fill };
    private readonly TextBox activity = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Font = new("Consolas", 9) };
    private readonly System.Windows.Forms.Timer expiryTimer = new() { Interval = 1000 };
    private SyncServer? server;
    private SyncClient? client;
    private bool busy, closing;
    private string lastLog = "";
    private DateTime lastLogAt;
    private bool IsServer => mode.SelectedIndex == 0;
    private bool Running => server is not null || client is not null;
    public MainForm(bool preview = false, bool previewClient = false)
    {
        settings = preview ? new Settings() : Settings.Load();
        Text = "Singularity Sync";
        Font = new("Segoe UI", 10);
        BackColor = Color.FromArgb(245, 247, 251);
        ForeColor = Color.FromArgb(24, 35, 52);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new(820, 790); MinimumSize = new(780, 810); StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(28), ColumnCount = 1, RowCount = 10 };
        foreach (int height in new[] { 45, 34, 45, 28, 42, 215, 51, 30 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.RowStyles.Add(new(SizeType.Percent, 100)); layout.RowStyles.Add(new(SizeType.Absolute, 48));
        layout.Controls.Add(new Label { Text = "Singularity Sync", AutoSize = true, Font = new("Segoe UI", 23, FontStyle.Bold), Margin = Padding.Empty }, 0, 0);
        layout.Controls.Add(new Label { Text = "Your files. Every LAN computer. One shared folder.", AutoSize = true, ForeColor = Color.FromArgb(85, 100, 120) }, 0, 1);
        mode.Items.AddRange(["Server", "Client"]); mode.SelectedIndex = preview ? (previewClient ? 1 : 0) : (settings.Mode == "Client" ? 1 : 0);
        layout.Controls.Add(Row(new Label { Text = "This computer", AutoSize = true, Margin = new(0, 8, 15, 0) }, mode), 0, 2);
        layout.Controls.Add(new Label { Text = "LOCAL SYNC FOLDER", AutoSize = true, Font = new("Segoe UI", 9, FontStyle.Bold) }, 0, 3);
        var folderRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        folderRow.ColumnStyles.Add(new(SizeType.Percent, 100)); folderRow.ColumnStyles.Add(new(SizeType.Absolute, 112));
        folderRow.Controls.Add(folder, 0, 0); folderRow.Controls.Add(browse, 1, 0); folder.Text = settings.Folder;
        layout.Controls.Add(folderRow, 0, 4);
        var modePanel = new Panel { Dock = DockStyle.Fill, Margin = new(0, 12, 0, 8), BackColor = Color.White, Padding = new(14) };
        modePanel.Controls.Add(serverPanel); modePanel.Controls.Add(clientPanel);
        var serverContent = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        serverContent.Controls.Add(new Label { Text = "SERVER · ONE-TIME PAIRING", Font = new("Segoe UI", 9, FontStyle.Bold), AutoSize = true });
        serverContent.Controls.Add(code); serverContent.Controls.Add(addresses); serverContent.Controls.Add(Row(rotate, revoke)); serverPanel.Controls.Add(serverContent);
        var clientContent = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        clientContent.Controls.Add(new Label { Text = "CLIENT · FIND YOUR SERVER", Font = new("Segoe UI", 9, FontStyle.Bold), AutoSize = true });
        clientContent.Controls.Add(Row(servers, search));
        clientContent.Controls.Add(new Label { Text = "Select a discovered server, or enter its IP and port. Use the code shown on the server.", AutoSize = true, Margin = new(0, 6, 0, 6) });
        clientContent.Controls.Add(Row(address, port, pairCode, pair)); clientContent.Controls.Add(paired); clientPanel.Controls.Add(clientContent);
        layout.Controls.Add(modePanel, 0, 5);
        layout.Controls.Add(Row(start, stop, status), 0, 6);
        layout.Controls.Add(new Label { Text = "ACTIVITY", AutoSize = true, Font = new("Segoe UI", 9, FontStyle.Bold) }, 0, 7);
        layout.Controls.Add(activity, 0, 8);
        layout.Controls.Add(new Label { Text = "Trusted LAN only · File traffic and pairing tokens are unencrypted.\nOverwritten and deleted files are kept in .singularity-sync\\recovery inside your sync folder.", Dock = DockStyle.Fill, Font = new("Segoe UI", 9), ForeColor = Color.FromArgb(85, 100, 120), Padding = new(0, 8, 0, 0) }, 0, 9);
        Controls.Add(layout);
        mode.SelectedIndexChanged += async (_, _) => { UpdateUi(); if (!IsServer) await SearchAsync(); };
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = "Choose the local folder to synchronize", UseDescriptionForTitle = true }; if (Directory.Exists(folder.Text)) dialog.SelectedPath = folder.Text; if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath; };
        search.Click += async (_, _) => await SearchAsync();
        servers.SelectedIndexChanged += (_, _) => { if (servers.SelectedItem is DiscoveredServer s) { address.Text = s.Address; port.Value = s.Info.Port; } };
        start.Click += async (_, _) => await RunActionAsync(StartAsync);
        stop.Click += async (_, _) => await RunActionAsync(StopAsync);
        pair.Click += async (_, _) => await RunActionAsync(async () =>
        {
            ValidateFolder();
            string? expected = servers.SelectedItem is DiscoveredServer s && s.Address == address.Text.Trim() && s.Info.Port == (int)port.Value ? s.Info.Id : null;
            var binding = await SyncClient.PairAsync(address.Text, (int)port.Value, settings.DeviceId, pairCode.Text, expected);
            settings.Binding = binding; settings.Save(); pairCode.Clear();
            Log("Paired successfully. This computer will remember the server.");
            await StartAsync();
        });
        rotate.Click += (_, _) => { server?.RotatePairingCode(); UpdateCode(); };
        revoke.Click += (_, _) => { server?.ForgetClients(); Log("All saved client authorizations revoked."); UpdateCode(); };
        expiryTimer.Tick += (_, _) => UpdateCode(); expiryTimer.Start();
        FormClosing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true;
            if (busy) { Log("Wait for the current operation before closing."); return; }
            busy = true; UpdateUi();
            try { await StopAsync(); } finally { closing = true; expiryTimer.Dispose(); Close(); }
        };
        if (settings.Binding is { } binding) { address.Text = binding.Address; port.Value = binding.Port; }
        UpdateUi();
        Shown += async (_, _) => { if (!preview && !IsServer) await SearchAsync(); };
    }
    private static Button MakeButton(string text, bool primary = false) => new()
    {
        Text = text, UseMnemonic = false, AutoSize = true, Height = 34, MinimumSize = new(100, 34), FlatStyle = FlatStyle.Flat,
        BackColor = primary ? Color.FromArgb(37, 99, 235) : Color.FromArgb(238, 242, 248),
        ForeColor = primary ? Color.White : Color.FromArgb(24, 35, 52), Margin = new(0, 0, 8, 4), Cursor = Cursors.Hand
    };
    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Dock = DockStyle.Top, Margin = new(0, 4, 0, 4) };
        row.Controls.AddRange(controls); return row;
    }
    private void UpdateUi()
    {
        serverPanel.Visible = IsServer; clientPanel.Visible = !IsServer;
        mode.Enabled = folder.Enabled = browse.Enabled = !Running && !busy;
        start.Text = IsServer ? "Start server" : "Start sync";
        start.Enabled = !Running && !busy && (IsServer || settings.Binding is not null);
        stop.Enabled = Running && !busy;
        pair.Enabled = search.Enabled = address.Enabled = port.Enabled = pairCode.Enabled = servers.Enabled = !Running && !busy;
        rotate.Enabled = revoke.Enabled = server is not null && !busy;
        status.Text = busy ? "Working…" : server is not null ? "Server running" : client is not null ? "Sync active · see activity" : "Stopped";
        paired.Text = settings.Binding is { } b ? $"Remembered server: {b.Address}:{b.Port} · Start sync to reconnect without a code." : "Not paired yet. Choose a local folder and pair once.";
        UpdateCode();
    }
    private void UpdateCode()
    {
        if (server is null) { code.Text = "Start the server to generate a code"; return; }
        var remaining = server.CodeExpiresUtc - DateTime.UtcNow;
        code.Text = remaining > TimeSpan.Zero ? $"{server.PairingCode}   ·   expires in {remaining:mm\\:ss}" : "Code expired · Generate a new code";
    }
    private async Task SearchAsync()
    {
        if (busy || Running) return;
        await RunActionAsync(async () =>
        {
            Log("Searching the local network…");
            var found = await Discovery.FindAsync(TimeSpan.FromSeconds(1.5));
            servers.Items.Clear(); foreach (var item in found) servers.Items.Add(item);
            if (servers.Items.Count > 0) servers.SelectedIndex = 0;
            Log(found.Count == 0 ? "No servers found. Start the server and allow Private-network firewall access, or enter its IP manually." : $"Found {found.Count} server(s).");
        });
    }
    private void ValidateFolder()
    {
        if (!Directory.Exists(folder.Text.Trim())) throw new IOException("Choose an existing local folder first.");
        var root = Path.GetFullPath(folder.Text.Trim());
        if (root == Path.GetPathRoot(root)) throw new IOException("Choose a folder instead of an entire drive.");
    }
    private async Task StartAsync()
    {
        ValidateFolder();
        settings.Mode = IsServer ? "Server" : "Client"; settings.Folder = Path.GetFullPath(folder.Text.Trim()); settings.Save();
        if (IsServer)
        {
            var instance = await Task.Run(() => new SyncServer(settings.Folder, settings, Log));
            try { await Task.Run(() => instance.StartAsync()); server = instance; }
            catch { await instance.DisposeAsync(); throw; }
            var ips = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)).Select(a => a.Address.ToString());
            addresses.Text = "Manual connection: " + string.Join(" / ", ips) + $" · TCP {Protocol.HttpPort}\nGive the pairing code to each client. Codes expire; existing pairings remain valid.";
        }
        else
        {
            var binding = settings.Binding ?? throw new IOException("Pair with a server first.");
            client = await Task.Run(() => new SyncClient(settings.Folder, binding, Log, b => { lock (settings) { settings.Binding = b; settings.Save(); } }));
            client.Start(); Log("Sync started. Connecting to the remembered server…");
        }
    }
    private async Task StopAsync()
    {
        if (client is not null) { await client.DisposeAsync(); client = null; }
        if (server is not null) { await server.DisposeAsync(); server = null; }
        Log("Stopped.");
    }
    private async Task RunActionAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true; UpdateUi();
        try { await action(); }
        catch (Exception e) { Log(e.Message); MessageBox.Show(this, e.Message, "Singularity Sync", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        finally { busy = false; UpdateUi(); }
    }
    private void Log(string message)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired) { try { BeginInvoke(() => Log(message)); } catch (InvalidOperationException) { } return; }
        if (message == lastLog && DateTime.UtcNow - lastLogAt < TimeSpan.FromSeconds(10)) return;
        lastLog = message; lastLogAt = DateTime.UtcNow;
        if (activity.TextLength > 60_000) activity.Text = activity.Text[^30_000..];
        activity.AppendText($"{DateTime.Now:HH:mm:ss}  {message}{Environment.NewLine}");
    }
}
