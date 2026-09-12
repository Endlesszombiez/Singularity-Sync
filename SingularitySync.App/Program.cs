namespace SingularitySync.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
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
                };
            }
            Application.Run(form);
        }
        catch (Exception e) { MessageBox.Show(e.Message, "Unable to start Singularity Sync", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
