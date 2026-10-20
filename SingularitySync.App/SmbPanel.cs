using System.Diagnostics;

namespace SingularitySync.App;

internal sealed class SmbPanel : UserControl
{
    private readonly SmbPreferences preferences;
    private readonly TextBox server = new() { Width = 235, PlaceholderText = "Server computer name" };
    private readonly TextBox share = new() { Width = 180 };
    private readonly TextBox user = new() { Width = 280, PlaceholderText = @"SERVER\SingularityTax" };
    private readonly TextBox password = new() { Width = 280, UseSystemPasswordChar = true };
    private readonly TextBox drive = new() { Width = 70, PlaceholderText = "X:" };
    private readonly TextBox result = new() { Width = 630, Height = 140, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
    private readonly CheckBox enabled = new() { Text = "Use direct access on this PC (stops and disables file sync)", AutoSize = true };
    private readonly FlowLayoutPanel content = new() { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new(10) };
    private readonly Func<bool, Task> changeMode;
    private bool busy;
    private readonly bool preview;
    public bool Busy => busy;

    public SmbPanel(SmbPreferences preferences, Func<bool, Task> changeMode, bool preview)
    {
        this.preferences = preferences; this.changeMode = changeMode; this.preview = preview;
        Dock = DockStyle.Fill; Controls.Add(content);
        TextLine("Open the server's actual files over SMB. App-created shares require encryption. No local database copies are synchronized. The server must stay awake and connected.");
        enabled.Checked = preferences.Enabled; content.Controls.Add(enabled);
        enabled.CheckedChanged += async (_, _) =>
        {
            if (busy || preview) return;
            await RunAsync(async () =>
            {
                try { await changeMode(enabled.Checked); }
                finally { enabled.Checked = preferences.Enabled; }
            });
        };
        Button("Set up this PC as server… (administrator)", () =>
        {
            RequireEnabled(); SmbSetup.OpenServerSetup();
            result.Text = "Server setup opens in its own administrator window. Record its server path, account and generated password for the workstations.";
            return Task.CompletedTask;
        });
        TextLine("Workstation connection — use the local account from server setup");
        server.Text = preferences.Server; share.Text = preferences.Share; user.Text = preferences.User; drive.Text = preferences.Drive;
        Row(new Label { Text = "Server", Width = 55 }, server, new Label { Text = "Share", Width = 50 }, share);
        Row(new Label { Text = "Account", Width = 75 }, user);
        Row(new Label { Text = "Password", Width = 75 }, password);
        Row(new Label { Text = "Optional drive", Width = 120 }, drive);
        TextLine("Use the path or drive letter required by your CrossLink installation. Saved passwords stay in Windows Credential Manager. Connect from your normal Windows session, not an elevated app.");
        var actions = Row();
        Button("Connect & save", ConnectAsync, actions);
        Button("Test access", async () =>
        {
            RequireEnabled(); var remote = SmbConnection.Remote(server.Text.Trim(), share.Text.Trim());
            await SmbConnection.CheckPortAsync(server.Text.Trim());
            result.Text = await Task.Run(() => SmbConnection.Probe(remote));
        }, actions);
        Button("Open folder", () =>
        {
            RequireEnabled(); var remote = SmbConnection.Remote(server.Text.Trim(), share.Text.Trim());
            Process.Start(new ProcessStartInfo(remote) { UseShellExecute = true }); return Task.CompletedTask;
        }, actions);
        var management = Row();
        Button("List connections", async () =>
        {
            result.Text = await SmbSetup.RunAsync(new { Action = "connections" });
            if (result.Text.Length == 0) result.Text = "No active SMB connections were reported for this Windows session.";
        }, management);
        Button("Disconnect selected…", async () =>
        {
            string remote = SmbConnection.Remote(server.Text.Trim(), share.Text.Trim(), allowAdministrative: true);
            string local = SmbConnection.ValidateDrive(drive.Text);
            if (MessageBox.Show(this, "Close CrossLink and files using this connection first. Disconnect " + (local.Length == 0 ? remote : local + " → " + remote) + "? Open files will not be forcibly closed.", "Disconnect", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            await Task.Run(() => SmbConnection.Disconnect(remote, local));
            result.Text = "Disconnected. Saved app credentials remain available.";
        }, management);
        Button("Forget saved password", () =>
        {
            SmbConnection.Remote(server.Text.Trim(), share.Text.Trim());
            SmbConnection.ForgetCredential(server.Text.Trim()); password.Clear();
            result.Text = "App credentials forgotten for this server. Existing Windows connections remain open until disconnected.";
            return Task.CompletedTask;
        }, management);
        content.Controls.Add(result);
        result.Text = "Use direct access for a supported CrossLink network installation. Configure the server once, then connect here from each workstation. Sync history does not back up live SMB files.";
    }

    public async Task RestoreConnectionAsync()
    {
        if (preferences.Enabled && preferences.Server.Length != 0)
            await RunAsync(ConnectAsync);
    }
    private void RequireEnabled()
    {
        if (!preferences.Enabled) throw new IOException("Enable 'Use direct access on this PC' first. This stops file synchronization.");
    }
    private async Task ConnectAsync()
    {
        RequireEnabled();
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
            throw new IOException("Open the main app normally, without 'Run as administrator', so CrossLink can use this Windows connection.");
        string host = server.Text.Trim(), remote = SmbConnection.Remote(host, share.Text.Trim()), local = SmbConnection.ValidateDrive(drive.Text);
        string account = user.Text.Trim(), secret = password.Text;
        if (secret.Length == 0)
        {
            var saved = SmbConnection.ReadCredential(host) ?? throw new IOException("Enter the password supplied during server setup.");
            if (account.Length != 0 && !account.Equals(saved.User, StringComparison.OrdinalIgnoreCase)) throw new IOException("The saved password belongs to a different account. Enter this account's password.");
            account = saved.User; secret = saved.Password;
        }
        await SmbConnection.CheckPortAsync(host);
        await Task.Run(() => SmbConnection.Connect(remote, local, account, secret));
        // Save only after Windows accepts these credentials; no password goes into app JSON.
        SmbConnection.SaveCredential(host, account, secret); password.Clear();
        preferences.Server = host; preferences.Share = share.Text.Trim(); preferences.User = account; preferences.Drive = local; preferences.Save();
        user.Text = account;
        result.Text = "Connected: " + (local.Length == 0 ? remote : local + " → " + remote) + Environment.NewLine + "Credentials saved. Use Test access before opening CrossLink. Closing this app leaves the Windows connection available.";
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (busy || preview) return; busy = true; content.Enabled = false;
        try { await action(); }
        catch (Exception e) { result.Text = e.Message; }
        finally { busy = false; content.Enabled = true; content.ScrollControlIntoView(result); }
    }
    private void TextLine(string text) => content.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new(625, 0), Margin = new(0, 5, 0, 8) });
    private FlowLayoutPanel Row(params Control[] controls)
    {
        var row = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new(0, 3, 0, 3) };
        row.Controls.AddRange(controls); content.Controls.Add(row); return row;
    }
    private void Button(string label, Func<Task> action, FlowLayoutPanel? parent = null)
    {
        var button = new Button { Text = label, AutoSize = true, MinimumSize = new(90, 32), Margin = new(0, 2, 8, 2) };
        button.Click += async (_, _) => await RunAsync(action); (parent ?? content).Controls.Add(button);
    }
}
