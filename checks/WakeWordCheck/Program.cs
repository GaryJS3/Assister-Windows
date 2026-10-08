using Assister.Windows.App.Services;
using SherpaOnnx;
using NAudio.Wave;

if (args.Length == 1 && args[0] == "--setup")
{
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    try
    {
        await WakeWordSetup.InstallAsync(new RejectProgress(), cancelled.Token);
        throw new Exception("Cancelled setup was accepted.");
    }
    catch (OperationCanceledException) { }
    await WakeWordSetup.InstallAsync(new InlineProgress(), CancellationToken.None);
    if (!WakeWordSetup.IsInstalled()) throw new Exception("Installed model is incomplete.");
    var unexpectedDownload = new RejectProgress();
    await WakeWordSetup.InstallAsync(unexpectedDownload, CancellationToken.None);
    var corrupt = Path.GetTempFileName();
    var output = Path.Combine(Path.GetTempPath(), "wake-word-check-" + Guid.NewGuid().ToString("N"));
    try
    {
        File.WriteAllText(corrupt, "corrupt archive");
        try
        {
            WakeWordSetup.ExtractVerifiedArchive(corrupt, output, CancellationToken.None);
            throw new Exception("Corrupt model archive was accepted.");
        }
        catch (IOException) { }
        if (Directory.Exists(output)) throw new Exception("Corrupt archive wrote installation files.");
    }
    finally { File.Delete(corrupt); }
    Console.WriteLine("PASS: managed installation ready, existing installation reused, bad checksum rejected before extraction.");
    return;
}

if (args.Length is < 1 or > 2) throw new ArgumentException("Pass the extracted official GigaSpeech KWS model folder and optionally a keyword file.");
var directory = Path.GetFullPath(args[0]);
try
{
    WakeWordDetector.CreateConfig(Path.Combine(directory, "missing"), "missing");
    throw new Exception("Missing model was accepted.");
}
catch (ArgumentException) { }
var config = WakeWordDetector.CreateConfig(directory, args.Length == 2 ? args[1] : Path.Combine(directory, "test_wavs", "test_keywords.txt"));
using var spotter = new KeywordSpotter(config);
using var stream = spotter.CreateStream();
var detections = new List<string>();
void Feed(float[] samples)
{
    stream.AcceptWaveform(16000, samples);
    while (spotter.IsReady(stream))
    {
        spotter.Decode(stream);
        var result = spotter.GetResult(stream).Keyword;
        if (string.IsNullOrEmpty(result)) continue;
        detections.Add(result);
        spotter.Reset(stream);
    }
}
Feed(new float[16000 * 2]);
if (detections.Count != 0) throw new Exception("Silence caused a detection.");
foreach (var file in Directory.GetFiles(Path.Combine(directory, "test_wavs"), "*.wav"))
{
    using var wave = new WaveFileReader(file);
    if (wave.WaveFormat.SampleRate != 16000 || wave.WaveFormat.Channels != 1) throw new Exception("Unexpected fixture format.");
    var provider = wave.ToSampleProvider();
    var buffer = new float[1600];
    int count;
    while ((count = provider.Read(buffer.AsSpan())) > 0) Feed(buffer[..count]);
    Feed(new float[16000]);
}
if (detections.Count == 0) throw new Exception("No keywords detected from the official fixtures.");
Console.WriteLine($"PASS: invalid configuration rejected, silence ignored, streaming native KWS detected {string.Join(", ", detections)}.");

sealed class InlineProgress : IProgress<string>
{
    public void Report(string value) => Console.WriteLine(value);
}

sealed class RejectProgress : IProgress<string>
{
    public void Report(string value)
    {
        if (!value.StartsWith("Ready")) throw new Exception("An installed engine was downloaded again.");
    }
}
