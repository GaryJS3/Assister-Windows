using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using SharpCompress.Readers;

namespace Assister.Windows.App.Services;

internal static class WakeWordSetup
{
    internal const string ModelName = "sherpa-onnx-kws-zipformer-gigaspeech-3.3M-2024-01-01";
    internal const string ArchiveHash = "F170013B4716E41B62B9BFD809687C207CEF798EF9BC6534D524E17AF9B6561A";
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Assister", "WakeWord");
    public static string ModelDirectory => Path.Combine(Root, "managed-" + ModelName);
    public static string KeywordsFile => Path.Combine(ModelDirectory, "keywords.txt");

    public static bool IsInstalled()
    {
        try { WakeWordDetector.CreateConfig(ModelDirectory, KeywordsFile); return true; }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException) { return false; }
    }

    public static async Task InstallAsync(IProgress<string> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsInstalled()) { progress.Report("Ready · say “light up” after saving"); return; }
        Directory.CreateDirectory(Root);
        var staging = Path.Combine(Root, "setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var archive = Path.Combine(staging, "model.tar.bz2");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var response = await client.GetAsync($"https://github.com/k2-fsa/sherpa-onnx/releases/download/kws-models/{ModelName}.tar.bz2", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = File.Create(archive))
            {
                var buffer = new byte[81920];
                long total = 0;
                int count;
                var lastReport = DateTimeOffset.MinValue;
                while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    total += count;
                    if (total > 100 * 1024 * 1024) throw new IOException("Wake word model download exceeds the size limit.");
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    if (DateTimeOffset.UtcNow - lastReport < TimeSpan.FromMilliseconds(200)) continue;
                    lastReport = DateTimeOffset.UtcNow;
                    progress.Report($"Downloading engine · {total / 1048576d:F1} MB");
                }
            }
            progress.Report("Verifying and unpacking engine…");
            var extracted = Path.Combine(staging, "model");
            await Task.Run(() => ExtractVerifiedArchive(archive, extracted, cancellationToken), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Publish only a fully verified installation; never replace custom models/keywords.
            var previous = Path.Combine(staging, "previous");
            if (Directory.Exists(ModelDirectory)) Directory.Move(ModelDirectory, previous);
            try { Directory.Move(extracted, ModelDirectory); }
            catch
            {
                if (Directory.Exists(previous)) Directory.Move(previous, ModelDirectory);
                throw;
            }
            progress.Report("Ready · say “light up” after saving");
        }
        finally { Directory.Delete(staging, recursive: true); }
    }

    internal static void ExtractVerifiedArchive(string archive, string destination, CancellationToken cancellationToken)
    {
        using (var file = File.OpenRead(archive))
        {
            var hash = Convert.ToHexString(SHA256.HashData(file));
            if (hash != ArchiveHash) throw new IOException("Wake word model checksum mismatch. Retry the download.");
        }
        Directory.CreateDirectory(destination);
        using var source = File.OpenRead(archive);
        using var reader = ReaderFactory.OpenReader(source);
        while (reader.MoveToNextEntry())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (reader.Entry.IsDirectory) continue;
            var key = reader.Entry.Key ?? "";
            if (!key.StartsWith(ModelName + "/", StringComparison.Ordinal)) continue;
            var name = key[(ModelName.Length + 1)..];
            // Flatten only the known root model files; no archive paths are used for output.
            if (name.Contains('/') || name.Contains('\\')) continue;
            if (!(name == "tokens.txt" || name.EndsWith(".onnx", StringComparison.Ordinal))) continue;
            if (name.Contains(':') || name is "." or "..") throw new IOException("Invalid model archive entry.");
            using var entry = reader.OpenEntryStream();
            using var output = File.Create(Path.Combine(destination, name));
            var buffer = new byte[81920];
            long total = 0;
            int count;
            while ((count = entry.Read(buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                total += count;
                if (total > 100 * 1024 * 1024) throw new IOException("Extracted model exceeds the size limit.");
                output.Write(buffer, 0, count);
            }
        }
        File.WriteAllText(Path.Combine(destination, "keywords.txt"), "▁ L IGHT ▁UP @light_up\n", new UTF8Encoding(false));
        WakeWordDetector.CreateConfig(destination, Path.Combine(destination, "keywords.txt"));
    }
}
