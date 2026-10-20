using System.ComponentModel;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using SingularitySync.Core;

namespace SingularitySync.App;

internal sealed class SmbPreferences
{
    public bool Enabled { get; set; }
    public string Server { get; set; } = "";
    public string Share { get; set; } = "CrossLink";
    public string User { get; set; } = "";
    public string Drive { get; set; } = "";
    private static string FileName => Path.Combine(Path.GetDirectoryName(Settings.SettingsPath)!, "smb.json");
    public static SmbPreferences Load() => DiskJson.Read<SmbPreferences>(FileName) ?? new();
    public void Save() => DiskJson.Write(FileName, this);
}

internal static class SmbConnection
{
    public static string Remote(string server, string share, bool allowAdministrative = false)
    {
        if (!Regex.IsMatch(server, @"\A[a-zA-Z0-9][a-zA-Z0-9.\-]{0,252}\z") || server.Contains(".."))
            throw new ArgumentException("Enter the server computer name or IPv4 address, without slashes or a port.");
        string sharePattern = allowAdministrative ? @"\A[a-zA-Z0-9][a-zA-Z0-9_\-]{0,39}\$?\z" : @"\A[a-zA-Z0-9][a-zA-Z0-9_\-]{0,39}\z";
        if (!Regex.IsMatch(share, sharePattern))
            throw new ArgumentException("Share names must contain 1–40 letters, numbers, underscores or hyphens.");
        return @"\\" + server + @"\" + share;
    }

    public static string ValidateDrive(string drive)
    {
        drive = drive.Trim().ToUpperInvariant();
        if (drive.Length != 0 && !Regex.IsMatch(drive, @"\A[D-Z]:\z"))
            throw new ArgumentException("Leave drive blank for a network path, or enter a drive such as X:.");
        return drive;
    }

    public static async Task CheckPortAsync(string server)
    {
        using var tcp = new TcpClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await tcp.ConnectAsync(server, 445, timeout.Token); }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        { throw new IOException("Cannot reach " + server + ":445. Check that the server is awake, on the trusted Private LAN, and SMB is allowed through its firewall."); }
    }

    public static void Connect(string remote, string drive, string user, string password)
    {
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password))
            throw new ArgumentException("Enter the dedicated server account and password, or load the saved credentials.");
        if (!user.Contains('\\') || user.Contains('@'))
            throw new ArgumentException(@"Use the server's local account shown during setup: SERVER\account.");
        var resource = new NetResource { Type = 1, RemoteName = remote, LocalName = drive.Length == 0 ? null : drive };
        int error = WNetAddConnection2(ref resource, password, user, drive.Length == 0 ? 0 : 1);
        if (error != 0) throw Error(error);
    }

    public static void Disconnect(string remote, string drive)
    {
        // Never force-close open database handles or disconnect other shares.
        if (drive.Length != 0)
        {
            var target = new StringBuilder(2048); int length = target.Capacity;
            int lookup = WNetGetConnection(drive, target, ref length);
            if (lookup != 0) throw Error(lookup);
            if (!target.ToString().Equals(remote, StringComparison.OrdinalIgnoreCase))
                throw new IOException("That drive points to a different share. Enter its exact server/share before disconnecting.");
        }
        int error = WNetCancelConnection2(drive.Length == 0 ? remote : drive, 1, false);
        if (error != 0 && error != 2250) throw Error(error);
    }

    public static Exception Error(int code) => new IOException(code switch
    {
        1219 => "Windows already has a connection to this server using another account. Close CrossLink and files on that server. Disconnect the conflicting connection shown in 'List connections', then retry. No connections were forcibly closed.",
        86 or 1326 => @"The server rejected the credentials. Use SERVER\local-account and the password created in server setup, not a Microsoft account or Windows PIN. Saved Windows connections can also conflict.",
        5 => "Access denied. The account needs both share access and folder permissions; server policy may also restrict network logon.",
        85 => "That drive letter is already in use. Choose a free letter or leave it blank.",
        53 or 67 => "The server or share was not found. Check the server name and the exact share name from server setup.",
        2401 or 2404 => "Files are still open on this connection. Close CrossLink and those files before disconnecting.",
        _ => new Win32Exception(code).Message
    } + $" (Windows error {code})");

    public static string Probe(string remote)
    {
        string test = Path.Combine(remote, ".singularity-access-test-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using var first = new FileStream(test, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
            first.WriteByte(42); first.Flush(true); first.Position = 0;
            if (first.ReadByte() != 42) throw new IOException("Read-back failed.");
            try
            {
                using var second = new FileStream(test, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                throw new IOException("The exclusive file access test failed.");
            }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { }
            return "Read, write and same-workstation exclusive-open checks passed. Test simultaneous CrossLink use from two PCs before production use. This is not a database compatibility certification.";
        }
        catch (UnauthorizedAccessException) { throw new IOException("Connected, but this account cannot create files in the share. Check both share and NTFS folder permissions."); }
    }

    private static string Target(string server) => "SingularitySync/SMB/" + server.ToLowerInvariant();
    public static void SaveCredential(string server, string user, string password)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(password);
        IntPtr blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var value = new Credential { Type = 1, TargetName = Target(server), UserName = user, CredentialBlob = blob,
                CredentialBlobSize = (uint)bytes.Length, Persist = 2 };
            if (!CredWrite(ref value, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Array.Clear(bytes); Marshal.Copy(bytes, 0, blob, bytes.Length); Marshal.FreeHGlobal(blob);
        }
    }

    public static (string User, string Password)? ReadCredential(string server)
    {
        if (!CredRead(Target(server), 1, 0, out var pointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 1168) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var value = Marshal.PtrToStructure<Credential>(pointer);
            return (value.UserName!, Marshal.PtrToStringUni(value.CredentialBlob, (int)value.CredentialBlobSize / 2) ?? "");
        }
        finally { CredFree(pointer); }
    }
    public static void ForgetCredential(string server)
    {
        if (!CredDelete(Target(server), 1, 0) && Marshal.GetLastWin32Error() != 1168)
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int Scope, Type, DisplayType, Usage;
        public string? LocalName, RemoteName, Comment, Provider;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags, Type;
        public string? TargetName, Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetAddConnection2(ref NetResource resource, string password, string user, int flags);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetCancelConnection2(string name, int flags, [MarshalAs(UnmanagedType.Bool)] bool force);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredDelete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr pointer);
}
