using System.IO;
using System.Threading.Channels;
using NAudio.Wave;
using SherpaOnnx;

namespace Assister.Windows.App.Services;

// One worker owns all native KWS objects. Capture callbacks only copy bounded PCM blocks.
internal sealed class WakeWordDetector
{
    public event Action<string>? Detected;
    public event Action<string>? StatusChanged;

    public static KeywordSpotterConfig CreateConfig(string directory, string keywords)
    {
        string Find(string pattern)
        {
            var files = Directory.GetFiles(directory, pattern);
            return files.OrderByDescending(path => path.Contains("int8", StringComparison.OrdinalIgnoreCase)).FirstOrDefault()
                ?? throw new ArgumentException($"Missing {pattern} in the wake word model folder.");
        }
        if (!Directory.Exists(directory)) throw new ArgumentException("Choose an extracted sherpa-onnx KWS model folder.");
        if (!File.Exists(keywords) || string.IsNullOrWhiteSpace(File.ReadAllText(keywords)))
            throw new ArgumentException("Choose a non-empty, tokenized sherpa-onnx keyword file.");
        var config = new KeywordSpotterConfig();
        config.FeatConfig.SampleRate = 16000;
        config.FeatConfig.FeatureDim = 80;
        config.ModelConfig.Transducer.Encoder = Find("encoder*.onnx");
        config.ModelConfig.Transducer.Decoder = Find("decoder*.onnx");
        config.ModelConfig.Transducer.Joiner = Find("joiner*.onnx");
        config.ModelConfig.Tokens = Find("tokens.txt");
        var vocabulary = File.ReadLines(config.ModelConfig.Tokens)
            .Select(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(token => token is not null).ToHashSet(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(keywords).Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var pieces = tokens.Where(token => !token.StartsWith('@') && !token.StartsWith(':') && !token.StartsWith('#')).ToArray();
            if (pieces.Length == 0 || pieces.Any(piece => !vocabulary.Contains(piece)))
                throw new ArgumentException("Keyword file contains tokens absent from this model. Use model-token sequences, not plain English text.");
        }
        config.ModelConfig.Provider = "cpu";
        config.ModelConfig.NumThreads = 2;
        config.KeywordsFile = Path.GetFullPath(keywords);
        return config;
    }

    public Task RunAsync(string directory, string keywords, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var config = CreateConfig(directory, keywords);
            using var spotter = new KeywordSpotter(config);
            using var stream = spotter.CreateStream();
            using var microphone = new WaveIn { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 };
            var audio = Channel.CreateBounded<float[]>(new BoundedChannelOptions(20)
            {
                SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
            });
            microphone.DataAvailable += (_, args) =>
            {
                var samples = new float[args.BytesRecorded / 2];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = (short)(args.Buffer[2 * i] | args.Buffer[2 * i + 1] << 8) / 32768f;
                if (!audio.Writer.TryWrite(samples))
                    audio.Writer.TryComplete(new IOException("Wake word processing fell behind. Restart listening in Settings."));
            };
            microphone.RecordingStopped += (_, args) => audio.Writer.TryComplete(args.Exception);
            microphone.StartRecording();
            StatusChanged?.Invoke("Wake word listening · local microphone");
            var lastDetection = DateTimeOffset.MinValue;
            try
            {
                await foreach (var samples in audio.Reader.ReadAllAsync(cancellationToken))
                {
                    stream.AcceptWaveform(16000, samples);
                    while (spotter.IsReady(stream))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        spotter.Decode(stream);
                        var keyword = spotter.GetResult(stream).Keyword;
                        if (string.IsNullOrEmpty(keyword)) continue;
                        spotter.Reset(stream);
                        if (DateTimeOffset.UtcNow - lastDetection < TimeSpan.FromSeconds(2)) continue;
                        lastDetection = DateTimeOffset.UtcNow;
                        Detected?.Invoke(keyword);
                    }
                }
                if (!cancellationToken.IsCancellationRequested) throw new IOException("Microphone stopped. Restart listening in Settings.");
            }
            finally { microphone.StopRecording(); }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) { StatusChanged?.Invoke($"Wake word unavailable: {exception.Message}"); }
    }, CancellationToken.None);
}
