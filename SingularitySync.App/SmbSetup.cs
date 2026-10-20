using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SingularitySync.App;

internal static class SmbSetup
{
    public static async Task<string> RunAsync(object request)
    {
        using var stream = typeof(SmbSetup).Assembly.GetManifestResourceStream("SingularitySync.SmbServer.ps1")!;
        using var reader = new StreamReader(stream);
        string script = await reader.ReadToEndAsync().ConfigureAwait(false);
        string system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        string executable = Path.Combine(system, Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess ? "Sysnative" : "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-EncodedCommand"); info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(info) ?? throw new IOException("Unable to start Windows server configuration.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        // Password is passed through a private stdin pipe, never command-line arguments or a script file.
        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request)).ConfigureAwait(false);
        process.StandardInput.Close();
        await process.WaitForExitAsync().ConfigureAwait(false);
        string result = await output.ConfigureAwait(false), failure = await error.ConfigureAwait(false);
        if (process.ExitCode != 0) throw new IOException(failure.Length == 0 ? "Windows configuration failed." : failure.Trim());
        return result.Trim();
    }

    public static void OpenServerSetup()
    {
        var info = new ProcessStartInfo(Application.ExecutablePath) { UseShellExecute = true, Verb = "runas" };
        info.ArgumentList.Add("--smb-server-setup");
        Process.Start(info)?.Dispose();
    }
}

internal sealed class SmbServerForm : Form
{
    private readonly TextBox folder = new() { Width = 560 };
    private readonly TextBox share = new() { Text = "CrossLink", Width = 240 };
    private readonly TextBox user = new() { Text = "SingularityTax", Width = 240 };
    private readonly TextBox password = new() { Width = 560, UseSystemPasswordChar = true, Text = "Ss9!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) };
    private readonly TextBox output = new() { Multiline = true, ReadOnly = true, Width = 640, Height = 175, ScrollBars = ScrollBars.Vertical };
    private readonly FlowLayoutPanel content = new() { Dock = DockStyle.Fill, Padding = new(20), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true };
    private bool busy;
    public SmbServerForm()
    {
        Text = "Direct access — server setup (administrator)"; Font = new("Segoe UI", 10);
        ClientSize = new(720, 690); StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(content);
        AddText("Create an encrypted Windows share for direct access. Close CrossLink on all PCs during setup. Choose its supported server folder, never a synchronized copy.");
        AddText("Local server folder"); content.Controls.Add(folder);
        AddButton("Browse…", () => { using var dialog = new FolderBrowserDialog(); if (dialog.ShowDialog(this) == DialogResult.OK) folder.Text = dialog.SelectedPath; return Task.CompletedTask; });
        AddText("Share name"); content.Controls.Add(share);
        AddText("New dedicated local account (not an administrator)"); content.Controls.Add(user);
        AddText("Generated password — record it for workstation setup"); content.Controls.Add(password);
        var show = new CheckBox { Text = "Show password", AutoSize = true };
        show.CheckedChanged += (_, _) => password.UseSystemPasswordChar = !show.Checked; content.Controls.Add(show);
        AddButton("Create server access", async () =>
        {
            SmbConnection.Remote(Environment.MachineName, share.Text.Trim());
            output.Text = await SmbSetup.RunAsync(new { Action = "create", Folder = folder.Text.Trim(), Share = share.Text.Trim(), User = user.Text.Trim(), Password = password.Text });
        });
        AddButton("Remove managed server access…", async () =>
        {
            if (MessageBox.Show(this, "Remove this app's share, local account and firewall rule? Close CrossLink on every workstation first. Files will remain on disk.", "Remove server access", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            output.Text = await SmbSetup.RunAsync(new { Action = "remove", Folder = folder.Text.Trim(), Share = share.Text.Trim(), User = user.Text.Trim() });
        });
        content.Controls.Add(output);
        FormClosing += (_, e) => { if (busy) e.Cancel = true; };
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) throw new IOException("Open server setup using its administrator button in Direct access.");
    }
    private void AddText(string text) => content.Controls.Add(new Label { Text = text, AutoSize = true, MaximumSize = new(640, 0), Margin = new(0, 6, 0, 4) });
    private void AddButton(string text, Func<Task> action)
    {
        var button = new Button { Text = text, AutoSize = true, MinimumSize = new(180, 32) };
        button.Click += async (_, _) =>
        {
            busy = true; content.Enabled = false; output.Text = "Working…";
            try { await action(); }
            catch (Exception e) { output.Text = e.Message; }
            finally { busy = false; content.Enabled = true; content.ScrollControlIntoView(output); }
        };
        content.Controls.Add(button);
    }
}
