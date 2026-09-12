using SingularitySync.Core;

namespace SingularitySync.App;

internal sealed class TrayActivityPanel : UserControl
{
    private readonly Label state = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Label rates = new() { Dock = DockStyle.Fill, AutoSize = true, Font = new("Segoe UI", 14, FontStyle.Bold) };
    private readonly Label totals = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly Label connected = new() { Dock = DockStyle.Fill, AutoSize = true };
    private readonly ListView peers = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, MultiSelect = false };
    private PeerActivity[] previous = [];
    public TrayActivityPanel(Action open)
    {
        Font = new("Segoe UI", 10); BackColor = Color.White; ForeColor = Color.FromArgb(24, 35, 52);
        AutoScaleMode = AutoScaleMode.Dpi; Size = new(440, 340); Padding = new(16);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 7 };
        foreach (int height in new[] { 28, 38, 28, 30 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 26)); layout.RowStyles.Add(new(SizeType.Absolute, 32));
        peers.Columns.Add("PC name", 160); peers.Columns.Add("Address", 135); peers.Columns.Add("Status", 75);
        layout.Controls.Add(state, 0, 0); layout.Controls.Add(rates, 0, 1); layout.Controls.Add(totals, 0, 2);
        layout.Controls.Add(connected, 0, 3); layout.Controls.Add(peers, 0, 4);
        layout.Controls.Add(new Label { Text = "Sync file data · totals since app launch", Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.DimGray }, 0, 5);
        var button = new Button { Text = "Open Singularity Sync", AutoSize = true, Dock = DockStyle.Fill };
        button.Click += (_, _) => open(); layout.Controls.Add(button, 0, 6); Controls.Add(layout);
    }
    public static string Bytes(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = 0; while (bytes >= 1000 && unit < units.Length - 1) { bytes /= 1000; unit++; }
        return $"{bytes:0.#} {units[unit]}";
    }
    public void RefreshActivity(ActivitySnapshot snapshot, string status)
    {
        state.Text = "Singularity Sync · " + status;
        rates.Text = $"↓ {Bytes(snapshot.DownloadRate)}/s     ↑ {Bytes(snapshot.UploadRate)}/s";
        totals.Text = $"Received {Bytes(snapshot.Downloaded)} · Sent {Bytes(snapshot.Uploaded)}";
        int count = snapshot.Peers.Count(p => p.Status != "Offline");
        connected.Text = snapshot.Peers.Count == 0 ? "No PCs connected" : $"{count} PC{(count == 1 ? "" : "s")} connected";
        if (previous.SequenceEqual(snapshot.Peers)) return;
        previous = snapshot.Peers.ToArray();
        peers.BeginUpdate();
        try
        {
            peers.Items.Clear();
            foreach (var peer in previous)
                peers.Items.Add(new ListViewItem([peer.Name, peer.Address, peer.Status]) { ForeColor = peer.Status == "Offline" ? Color.DimGray : ForeColor });
        }
        finally { peers.EndUpdate(); }
    }
}
