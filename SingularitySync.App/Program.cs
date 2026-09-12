namespace SingularitySync.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // A stale login entry must not start sharing after the preference or mode changes.
        if (args.Contains("--startup"))
        {
            try
            {
                var settings = SingularitySync.Core.Settings.Load();
                if (!settings.StartServerAtLogin || settings.Mode != "Server") return;
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "Unable to load startup settings", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
        }
        // The kernel owns this lock for the lifetime of the process, including time in the tray.
        // A crash releases it automatically; no stale lockfile or process-name race is involved.
        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            if (args.Contains("--startup")) return;
            MessageBox.Show("Singularity Sync is already running.\nOpen it from the system tray, or choose Exit there before starting it again.",
                "Singularity Sync already running", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "Singularity Sync", MessageBoxButtons.OK, MessageBoxIcon.Error);
        try
        {
            bool preview = args.Length >= 2 && args[0] == "--smoke-test";
            var form = new MainForm(preview, args.Length >= 3 && args[2] == "client");
            if (preview)
            {
                form.Shown += async (_, _) =>
                {
                    await Task.Delay(500);
                    using var bitmap = new Bitmap(form.Width, form.Height);
                    form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
                    bitmap.Save(Path.GetFullPath(args[1]));
                    form.Close();
                    if (form.Visible || form.IsDisposed || form.ShowInTaskbar) throw new InvalidOperationException("Close-to-tray check failed.");
                    var popup = form.ShowTrayActivity();
                    await Task.Delay(150);
                    if (!popup.Visible) throw new InvalidOperationException("Tray activity panel did not open.");
                    var panel = (TrayActivityPanel)((ToolStripControlHost)popup.Items[0]).Control;
                    panel.RefreshActivity(new SingularitySync.Core.ActivitySnapshot(125_000_000, 450_000_000, 3_100_000, 12_400_000,
                        [new("1", "OFFICE-DESKTOP", "192.168.1.20", "Syncing"), new("2", "LIVING-ROOM", "192.168.1.21", "Idle"), new("3", "TRAVEL-LAPTOP", "192.168.1.22", "Offline")]), "Server running");
                    using var panelBitmap = new Bitmap(popup.Width, popup.Height);
                    popup.DrawToBitmap(panelBitmap, new Rectangle(Point.Empty, popup.Size));
                    panelBitmap.Save(Path.GetFullPath(args[1]) + ".tray.png");
                    popup.Close();
                    form.ShowTrayActivity();
                    form.RestoreFromTray();
                    if (popup.Visible) throw new InvalidOperationException("Restore did not dismiss tray panel.");
                    if (!form.Visible || !form.ShowInTaskbar) throw new InvalidOperationException("Tray restore check failed.");
                    await form.ExitAsync();
                    if (!form.IsDisposed) throw new InvalidOperationException("Tray exit check failed.");
                    File.WriteAllText(Path.GetFullPath(args[1]) + ".checks.txt", "PASS close to tray; tray panel open/reopen; restore dismisses panel; explicit exit");
                };
            }
            Application.Run(form);
        }
        catch (Exception e) { MessageBox.Show(e.Message, "Unable to start Singularity Sync", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
