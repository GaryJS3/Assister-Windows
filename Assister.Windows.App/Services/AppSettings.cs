using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Assister.Windows.App.Services;

internal sealed class AppSettings
{
    public string Endpoint { get; set; } = "https://assister.example/";
    public Guid? ConversationId { get; set; }
    public bool WakeWordEnabled { get; set; }
    public string WakeWordModelDirectory { get; set; } = "";
    public string WakeWordKeywordsFile { get; set; } = "";

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Assister", "settings.json");

    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (IOException) { return new(); }
        catch (JsonException) { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, FilePath, overwrite: true);
    }
}

internal static class CredentialStore
{
    private const string Target = "Assister.Windows.ClientToken";
    private const uint Generic = 1;

    public static void Save(string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = Generic, TargetName = Target, CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob, Persist = 2, UserName = Environment.UserName
            };
            if (!CredWrite(ref credential, 0)) throw new InvalidOperationException("Windows could not save the client token in Credential Manager.", Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error()));
        }
        finally
        {
            for (var index = 0; index < bytes.Length; index++) Marshal.WriteByte(blob, index, 0);
            Marshal.FreeHGlobal(blob);
        }
        Array.Clear(bytes);
    }

    public static string? Read()
    {
        if (!CredRead(Target, Generic, 0, out var pointer)) return null;
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            var bytes = new byte[checked((int)credential.CredentialBlobSize)];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { Array.Clear(bytes); }
        }
        finally { CredFree(pointer); }
    }

    // Local developer convenience. This file is ignored by Git and is never copied into the app package.
    public static string? ReadDevelopmentToken()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "assister-dev-token.txt"));
        if (!File.Exists(path)) return null;
        var token = File.ReadAllText(path).Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredWrite(ref NativeCredential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", SetLastError = false)] private static extern void CredFree(IntPtr buffer);
}
