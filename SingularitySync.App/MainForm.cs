using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using SingularitySync.Core;

namespace SingularitySync.App;

public sealed class MainForm : Form
{
    private readonly Settings settings;
    private readonly Icon applicationIcon;
    private readonly ComboBox mode = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
    private readonly TextBox folder = new() { Dock = DockStyle.Fill, PlaceholderText = "Choose a folder on this computer" };
    private readonly Button browse = MakeButton("Browse…");
    private readonly Button start = MakeButton("Start server", true), stop = MakeButton("Stop");
    private readonly Label status = new() { Text = "Stopped", AutoSize = true, ForeColor = Color.FromArgb(80, 95, 115), Margin = new(12, 10, 0, 0) };
    private readonly Label code = new() { Text = "Start the server to generate a code", AutoSize = true, Font = new("Segoe UI", 15, FontStyle.Bold), Margin = new(0, 8, 0, 8) };
    private readonly Label addresses = new() { AutoSize = true, MaximumSize = new(680, 0), Text = "The server owns the shared folder. Paired clients can read, edit, and delete its files." };
    private readonly Button openHistory = MakeButton("Open file history");
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
    private readonly CheckBox startAtLogin = new() { Text = "Start server when I sign in to Windows", AutoSize = true, Margin = new(0, 8, 0, 8) };
    private SyncServer? server;
    private SyncClient? client;
    private bool busy, closing, exitRequested, trayHintShown;
    private readonly NotifyIcon tray = new();
    private readonly ContextMenuStrip trayMenu = new();
    private readonly ToolStripMenuItem trayServerToggle = new();
    private readonly SyncActivity networkActivity = new();
    private readonly ToolStripDropDown trayPopup = new() { Padding = Padding.Empty, AutoClose = true };
    private readonly TrayActivityPanel trayActivity;
    private readonly System.Windows.Forms.Timer trayClickTimer = new() { Interval = SystemInformation.DoubleClickTime };
    private string lastLog = "";
    private DateTime lastLogAt;
    private bool IsServer => mode.SelectedIndex == 0;
    private bool Running => server is not null || client is not null;
    public MainForm(bool preview = false, bool previewClient = false)
    {
        settings = preview ? new Settings() : Settings.Load();
        using (var iconStream = typeof(MainForm).Assembly.GetManifestResourceStream("SingularitySync.AppIcon")
            ?? throw new InvalidOperationException("Application icon is missing."))
            applicationIcon = new Icon(iconStream);
        Icon = applicationIcon;
        Text = "Singularity Sync";
        Font = new("Segoe UI", 10);
        BackColor = Color.FromArgb(245, 247, 251);
        ForeColor = Color.FromArgb(24, 35, 52);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new(800, 710); MinimumSize = new(740, 680); StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(24), ColumnCount = 1, RowCount = 4 };
        layout.RowStyles.Add(new(SizeType.Absolute, 76));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 54));
        layout.RowStyles.Add(new(SizeType.Absolute, 26));
        var heading = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        heading.Controls.Add(new Label { Text = "Singularity Sync", AutoSize = true, Font = new("Segoe UI", 22, FontStyle.Bold), Margin = Padding.Empty });
        heading.Controls.Add(new Label { Text = "One shared folder, across your LAN.", AutoSize = true, ForeColor = Color.FromArgb(85, 100, 120), Margin = new(0, 4, 0, 0) });
        layout.Controls.Add(heading, 0, 0);
        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new(18, 8) };
        var setup = new TabPage("Setup") { BackColor = Color.White, Padding = new(20), AutoScroll = true };
        var logs = new TabPage("Activity") { BackColor = Color.White, Padding = new(16) };
        logs.Controls.Add(activity); tabs.TabPages.AddRange([setup, logs]);
        var content = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 6 };
        foreach (int height in new[] { 30, 44, 30, 48, 1, 280 }) content.RowStyles.Add(new(SizeType.Absolute, height));
        mode.Items.AddRange(["Server", "Client"]); mode.SelectedIndex = preview ? (previewClient ? 1 : 0) : (settings.Mode == "Client" ? 1 : 0);
        content.Controls.Add(Caption("This computer"), 0, 0);
        content.Controls.Add(Row(mode, new Label { Text = "Server shares · Client connects", AutoSize = true, ForeColor = Color.FromArgb(85, 100, 120), Margin = new(14, 6, 0, 0) }), 0, 1);
        content.Controls.Add(Caption("Local folder"), 0, 2);
        var folderRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        folderRow.ColumnStyles.Add(new(SizeType.Percent, 100)); folderRow.ColumnStyles.Add(new(SizeType.Absolute, 108));
        folderRow.Controls.Add(folder, 0, 0); folderRow.Controls.Add(browse, 1, 0); folder.Text = settings.Folder;
        content.Controls.Add(folderRow, 0, 3);
        content.Controls.Add(new Panel { Dock = DockStyle.Fill, BackColor = Color.FromArgb(226, 232, 240) }, 0, 4);
        var modePanel = new Panel { Dock = DockStyle.Fill, Padding = new(0, 18, 0, 0), Margin = Padding.Empty };
        modePanel.Controls.Add(serverPanel); modePanel.Controls.Add(clientPanel);
        var serverContent = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        startAtLogin.Checked = settings.StartServerAtLogin;
        serverContent.Controls.Add(startAtLogin);
        serverContent.Controls.Add(Caption("Invite a client"));
        serverContent.Controls.Add(new Label { Text = "Start sharing, then enter this code on the client computer.", AutoSize = true, Margin = new(0, 4, 0, 8) });
        serverContent.Controls.Add(code);
        addresses.MaximumSize = new(620, 0); addresses.Margin = new(0, 8, 0, 12);
        serverContent.Controls.Add(addresses);
        serverContent.Controls.Add(Row(rotate, revoke, openHistory)); serverPanel.Controls.Add(serverContent);
        var clientContent = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        foreach (int height in new[] { 28, 44, 28, 80, 50 }) clientContent.RowStyles.Add(new(SizeType.Absolute, height));
        clientContent.Controls.Add(Caption("Connect to a server"), 0, 0);
        var discoveryRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty };
        discoveryRow.ColumnStyles.Add(new(SizeType.Percent, 100)); discoveryRow.ColumnStyles.Add(new(SizeType.Absolute, 116));
        servers.Dock = DockStyle.Fill; discoveryRow.Controls.Add(servers); discoveryRow.Controls.Add(search);
        clientContent.Controls.Add(discoveryRow, 0, 1);
        clientContent.Controls.Add(new Label { Text = "Select a server above, or enter its address below.", AutoSize = true, ForeColor = Color.FromArgb(85, 100, 120) }, 0, 2);
        var fields = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, Margin = Padding.Empty };
        fields.ColumnStyles.Add(new(SizeType.Percent, 100)); fields.ColumnStyles.Add(new(SizeType.Absolute, 88)); fields.ColumnStyles.Add(new(SizeType.Absolute, 118)); fields.ColumnStyles.Add(new(SizeType.Absolute, 118));
        fields.RowStyles.Add(new(SizeType.Absolute, 26)); fields.RowStyles.Add(new(SizeType.Absolute, 42));
        fields.Controls.Add(Caption("Server IP"), 0, 0); fields.Controls.Add(Caption("Port"), 1, 0); fields.Controls.Add(Caption("Pairing code"), 2, 0);
        address.Dock = port.Dock = pairCode.Dock = DockStyle.Fill;
        fields.Controls.Add(address, 0, 1); fields.Controls.Add(port, 1, 1); fields.Controls.Add(pairCode, 2, 1); fields.Controls.Add(pair, 3, 1);
        clientContent.Controls.Add(fields, 0, 3); paired.MaximumSize = new(620, 0); clientContent.Controls.Add(paired, 0, 4);
        clientPanel.Controls.Add(clientContent); content.Controls.Add(modePanel, 0, 5);
        setup.Controls.Add(content); layout.Controls.Add(tabs, 0, 1);
        layout.Controls.Add(Row(start, stop, status), 0, 2);
        layout.Controls.Add(new Label { Text = "Closing this window keeps sync in the tray.  ·  Trusted LAN / unencrypted", Dock = DockStyle.Fill, Font = new("Segoe UI", 9), ForeColor = Color.FromArgb(85, 100, 120) }, 0, 3);
        Controls.Add(layout);
        trayActivity = new TrayActivityPanel(RestoreFromTray);
        trayPopup.Items.Add(new ToolStripControlHost(trayActivity) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = trayActivity.Size });
        trayMenu.Items.Add("Network activity", null, (_, _) => ShowTrayActivity());
        trayMenu.Items.Add("Open Singularity Sync", null, (_, _) => RestoreFromTray());
        trayMenu.Items.Add(trayServerToggle);
        trayServerToggle.Click += async (_, _) =>
        {
            if (!IsServer || string.IsNullOrWhiteSpace(folder.Text) || busy || closing || exitRequested) return;
            await RunActionAsync(server is null ? StartAsync : StopAsync);
        };
        trayMenu.Opening += (_, _) => UpdateTrayServerMenu();
        folder.TextChanged += (_, _) => UpdateTrayServerMenu();
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add("Exit", null, async (_, _) => await ExitAsync());
        tray.Icon = applicationIcon;
        tray.Text = "Singularity Sync · Stopped";
        tray.ContextMenuStrip = trayMenu;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) trayClickTimer.Start(); };
        tray.DoubleClick += (_, _) => { trayClickTimer.Stop(); RestoreFromTray(); };
        trayClickTimer.Tick += (_, _) => { trayClickTimer.Stop(); if (trayPopup.Visible) trayPopup.Close(); else ShowTrayActivity(); };
        tray.Visible = true;
        FormClosed += (_, _) => { tray.Visible = false; tray.Dispose(); trayMenu.Dispose(); expiryTimer.Dispose(); trayClickTimer.Dispose(); trayPopup.Dispose(); applicationIcon.Dispose(); };
        mode.SelectedIndexChanged += async (_, _) =>
        {
            UpdateUi();
            if (!preview) await RunActionAsync(() => { settings.Mode = IsServer ? "Server" : "Client"; settings.Save(); return Task.CompletedTask; });
            if (!IsServer) await SearchAsync();
        };
        startAtLogin.CheckedChanged += async (_, _) =>
        {
            if (preview || startAtLogin.Checked == settings.StartServerAtLogin) return;
            await RunActionAsync(() =>
            {
                bool previous = settings.StartServerAtLogin;
                string previousFolder = settings.Folder, previousMode = settings.Mode;
                try
                {
                    if (startAtLogin.Checked) ValidateFolder();
                    WindowsStartup.SetEnabled(startAtLogin.Checked, Application.ExecutablePath);
                    settings.StartServerAtLogin = startAtLogin.Checked;
                    if (startAtLogin.Checked)
                    {
                        settings.Mode = "Server";
                        settings.Folder = Path.GetFullPath(folder.Text.Trim());
                    }
                    try { settings.Save(); }
                    catch
                    {
                        WindowsStartup.SetEnabled(previous, Application.ExecutablePath);
                        throw;
                    }
                }
                catch
                {
                    settings.StartServerAtLogin = previous;
                    settings.Folder = previousFolder; settings.Mode = previousMode;
                    startAtLogin.Checked = previous;
                    throw;
                }
                return Task.CompletedTask;
            });
        };
        browse.Click += (_, _) => { using var dialog = new FolderBrowserDialog { Description = "Choose the local folder to synchronize", UseDescriptionForTitle = true }; if (Directory.Exists(folder.Text)) dialog.SelectedPath = folder.Text; if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath; };
        openHistory.Click += async (_, _) => await RunActionAsync(() =>
        {
            ValidateFolder();
            string path = Path.Combine(Path.GetFullPath(folder.Text.Trim()), FolderStore.MetadataName, "history");
            FolderStore.EnsureNoLinks(path);
            if (!Directory.Exists(path)) throw new IOException("Start the server once to create file history.");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            return Task.CompletedTask;
        });
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
        expiryTimer.Tick += (_, _) => { UpdateCode(); UpdateTrayActivity(); }; expiryTimer.Start();
        FormClosing += (_, e) =>
        {
            if (closing) return;
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                ShowInTaskbar = false;
                if (!trayHintShown && !preview)
                {
                    trayHintShown = true;
                    tray.ShowBalloonTip(3000, "Singularity Sync is in the tray", "Sync continues while this window is closed. Use the tray menu to open the app or exit.", ToolTipIcon.Info);
                }
            }
            // Windows shutdown/logoff must not be canceled to hide the window.
        };
        if (settings.Binding is { } binding) { address.Text = binding.Address; port.Value = binding.Port; }
        UpdateUi();
        Shown += async (_, _) =>
        {
            if (preview) return;
            if (IsServer && settings.StartServerAtLogin) await RunActionAsync(StartAsync);
            else if (!IsServer && settings.Binding is not null) await RunActionAsync(StartAsync);
            else if (!IsServer) await SearchAsync();
        };
    }
    private static Label Caption(string text) => new() { Text = text, AutoSize = true, Font = new("Segoe UI", 10, FontStyle.Bold), Margin = Padding.Empty };
    internal ToolStripDropDown ShowTrayActivity()
    {
        UpdateTrayActivity();
        var point = Cursor.Position;
        var area = Screen.FromPoint(point).WorkingArea;
        trayPopup.Show(new Point(Math.Clamp(point.X - trayPopup.Width, area.Left, Math.Max(area.Left, area.Right - trayPopup.Width)),
            Math.Clamp(point.Y - trayPopup.Height, area.Top, Math.Max(area.Top, area.Bottom - trayPopup.Height))));
        return trayPopup;
    }
    private void UpdateTrayActivity()
    {
        var snapshot = networkActivity.Snapshot();
        int count = snapshot.Peers.Count(p => p.Status != "Offline");
        string summary = Running ? $"↓ {TrayActivityPanel.Bytes(snapshot.DownloadRate)}/s · ↑ {TrayActivityPanel.Bytes(snapshot.UploadRate)}/s · {count} PCs connected" : "Stopped";
        string tooltip = "Singularity Sync · " + summary;
        tray.Text = tooltip[..Math.Min(127, tooltip.Length)];
        trayActivity.RefreshActivity(snapshot, status.Text);
    }
    internal void RestoreFromTray()
    {
        trayPopup.Close();
        ShowInTaskbar = true;
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }
    internal async Task ExitAsync()
    {
        if (closing) return;
        exitRequested = true;
        if (busy) return; // Finish pairing/start/stop before disposing its resources.
        busy = true; UpdateUi();
        try { await StopAsync(); }
        catch (Exception e) { Log("Shutdown: " + e.Message); }
        finally { closing = true; Close(); }
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
    private void UpdateTrayServerMenu()
    {
        trayServerToggle.Visible = IsServer && !string.IsNullOrWhiteSpace(folder.Text);
        trayServerToggle.Text = server is null ? "Start server" : "Stop server";
        trayServerToggle.Enabled = !busy && !closing && !exitRequested;
    }
    private void UpdateUi()
    {
        serverPanel.Visible = IsServer; clientPanel.Visible = !IsServer;
        mode.Enabled = folder.Enabled = browse.Enabled = !Running && !busy;
        start.Text = IsServer ? "Start server" : "Start sync";
        start.Enabled = !Running && !busy && (IsServer || settings.Binding is not null);
        stop.Enabled = Running && !busy;
        startAtLogin.Enabled = !busy;
        openHistory.Enabled = !busy;
        pair.Enabled = search.Enabled = address.Enabled = port.Enabled = pairCode.Enabled = servers.Enabled = !Running && !busy;
        rotate.Enabled = revoke.Enabled = server is not null && !busy;
        status.Text = busy ? "Working…" : server is not null ? "Server running" : client is not null ? "Sync active · see activity" : "Stopped";
        UpdateTrayActivity();
        UpdateTrayServerMenu();
        paired.Text = settings.Binding is { } b ? $"Remembered server: {b.Address}:{b.Port} · Sync resumes automatically when this client opens." : "Not paired yet. Choose a local folder and pair once.";
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
        networkActivity.ClearPeers();
        settings.Mode = IsServer ? "Server" : "Client"; settings.Folder = Path.GetFullPath(folder.Text.Trim()); settings.Save();
        if (IsServer)
        {
            var instance = await Task.Run(() => new SyncServer(settings.Folder, settings, Log, activity: networkActivity));
            try { await Task.Run(() => instance.StartAsync()); server = instance; }
            catch { await instance.DisposeAsync(); throw; }
            var ips = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)).Select(a => a.Address.ToString());
            addresses.Text = "Manual connection: " + string.Join(" / ", ips) + $" · TCP {Protocol.HttpPort}\nGive the pairing code to each client. Codes expire; existing pairings remain valid.";
        }
        else
        {
            var binding = settings.Binding ?? throw new IOException("Pair with a server first.");
            client = await Task.Run(() => new SyncClient(settings.Folder, binding, Log, b => { lock (settings) { settings.Binding = b; settings.Save(); } }, activity: networkActivity));
            client.Start(); Log("Sync started. Connecting to the remembered server…");
        }
    }
    private async Task StopAsync()
    {
        if (client is not null) { await client.DisposeAsync(); client = null; }
        if (server is not null) { await server.DisposeAsync(); server = null; }
        networkActivity.Stop();
        Log("Stopped.");
    }
    private async Task RunActionAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true; UpdateUi();
        try { await action(); }
        catch (Exception e) { Log(e.Message); MessageBox.Show(this, e.Message, "Singularity Sync", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        finally { busy = false; UpdateUi(); if (exitRequested) await ExitAsync(); }
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
