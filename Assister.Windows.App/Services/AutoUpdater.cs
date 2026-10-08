using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;

namespace Assister.Windows.App.Services;

internal static class AutoUpdater
{
    internal static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Assister");
    internal static readonly string InstalledPath = Path.Combine(Root, "App", "Assister.Windows.App.exe");
    internal static string CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version!.ToString();
    internal static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Root);
            var path = Path.Combine(Root, "update.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1048576) File.Move(path, path + ".old", true);
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {message}{Environment.NewLine}");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static bool Bootstrap()
    {
        if (!string.IsNullOrEmpty(Assembly.GetExecutingAssembly().Location)) return false;
        var executable = Environment.ProcessPath!;
        if (string.Equals(executable, InstalledPath, StringComparison.OrdinalIgnoreCase)) return false;
        Directory.CreateDirectory(Path.GetDirectoryName(InstalledPath)!);
        if (!File.Exists(InstalledPath) || !Version.TryParse(FileVersionInfo.GetVersionInfo(InstalledPath).FileVersion, out var installedVersion) || installedVersion < Version.Parse(CurrentVersion))
            File.Copy(executable, InstalledPath, true);
        Launch(InstalledPath);
        return true;
    }

    internal static async Task<string?> DownloadAsync(string endpoint, CancellationToken token)
    {
        Log($"Checking Windows updates from version {CurrentVersion}.");
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var origin) || origin.AbsolutePath != "/" || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0) throw new ArgumentException("Save a valid server origin before checking for updates.");
        if (origin.Scheme != "https" && !(origin.Scheme == "http" && AssisterClient.IsPrivateDevelopmentHost(origin.Host))) throw new ArgumentException("Updates require HTTPS or a permitted development LAN server.");
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = origin, Timeout = TimeSpan.FromMinutes(10) };
        // Public releases never receive chat or publisher credentials.
        var check = await http.GetFromJsonAsync<UpdateCheck>($"api/updates/assister/windows-x64/check?currentVersion={CurrentVersion}", token);
        if (check is not { UpdateAvailable: true, Release: { } release }) return null;
        if (release.AppId != "assister" || release.Platform != "windows-x64" || !Version.TryParse(release.Version, out var version) || version <= Version.Parse(CurrentVersion) || release.Size is <= 0 or > 536870912 || release.Sha256.Length != 64) throw new InvalidDataException("Invalid Windows release metadata.");
        var url = new Uri(origin, release.DownloadUrl);
        if (url.Scheme != origin.Scheme || url.Authority != origin.Authority || url.UserInfo.Length != 0) throw new InvalidDataException("Update download must use the configured server origin.");
        var directory = Path.Combine(Root, "Updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Assister.Windows.App.exe");
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using (var output = File.Create(path))
            {
                var buffer = new byte[81920];
                long size = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, token)) != 0)
                {
                    size += count;
                    if (size > release.Size) throw new InvalidDataException("Update exceeds advertised size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
                if (size != release.Size) throw new InvalidDataException("Update size mismatch.");
            }
            Log("Download completed; verifying update.");
            await using var file = File.OpenRead(path);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
            if (!hash.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Update SHA-256 mismatch.");
            if (!Version.TryParse(FileVersionInfo.GetVersionInfo(path).FileVersion, out var actualVersion) || actualVersion != version) throw new InvalidDataException("EXE version does not match release metadata.");
            Log($"Verified Windows update {version}.");
            return path;
        }
        catch { File.Delete(path); throw; }
    }

    internal static void BeginInstall(string staged)
    {
        var helper = Path.Combine(Path.GetDirectoryName(staged)!, "UpdateHelper.exe");
        File.Copy(Environment.ProcessPath!, helper, true);
        var info = new ProcessStartInfo(helper) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(staged)! };
        info.ArgumentList.Add("--apply-update");
        info.ArgumentList.Add(Environment.ProcessId.ToString());
        info.ArgumentList.Add(staged);
        if (Process.Start(info) is null) throw new IOException("Cannot start update helper.");
    }

    internal static bool ApplyUpdate(string[] args)
    {
        if (args.Length != 3 || args[0] != "--apply-update" || !int.TryParse(args[1], out var pid)) return false;
        var backup = InstalledPath + ".previous";
        var replaced = false;
        try
        {
            try { using var parent = Process.GetProcessById(pid); if (!parent.WaitForExit(60000)) throw new IOException("App did not exit for update."); }
            catch (ArgumentException) { }
            var staged = args[2];
            if (!Path.GetFullPath(staged).StartsWith(Path.Combine(Root, "Updates") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Invalid update staging location.");
            File.Replace(staged, InstalledPath, backup, true);
            replaced = true;
            Launch(InstalledPath);
            Log("Update installed and restart requested.");
        }
        catch (Exception exception)
        {
            Log($"Update installation failed: {exception.GetType().Name}: {exception.Message}");
            if (replaced && File.Exists(backup)) File.Copy(backup, InstalledPath, true);
            Launch(InstalledPath);
        }
        return true;
    }

    private static void Launch(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! }) is null) throw new IOException("Restart returned no process.");
                return;
            }
            catch (System.ComponentModel.Win32Exception exception) when (attempt < 10)
            {
                Log($"Restart delayed by Windows error {exception.NativeErrorCode}; retrying.");
                Thread.Sleep(1000);
            }
        }
    }
    private sealed record UpdateCheck(bool UpdateAvailable, Release? Release);
    private sealed record Release(string AppId, string Platform, string Version, long Size, string Sha256, string DownloadUrl);
}
