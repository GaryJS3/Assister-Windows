using System.Diagnostics;
using System.IO;
using NAudio.Wave;

namespace Assister.Windows.App.Services;

internal sealed class SpeechEndpoint
{
    private int _samples;
    private int _quietSamples;
    private int _speechSamples;
    public bool HeardSpeech => _speechSamples >= 1600;
    public bool Add(ReadOnlySpan<byte> pcm)
    {
        double energy = 0;
        var count = pcm.Length / 2;
        for (var i = 0; i < count; i++)
        {
            var sample = (short)(pcm[2 * i] | pcm[2 * i + 1] << 8) / 32768d;
            energy += sample * sample;
        }
        _samples += count;
        var speaking = count > 0 && Math.Sqrt(energy / count) >= 0.012;
        if (speaking) { _speechSamples += count; _quietSamples = 0; }
        else _quietSamples += count;
        return _samples >= 16000 * 30 || (HeardSpeech && _quietSamples >= 16000) || (!HeardSpeech && _samples >= 16000 * 8);
    }
}

internal sealed record VoiceRecording(byte[] Pcm, bool HeardSpeech);

internal sealed class VoiceRecorder
{
    private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Stop() => _stop.TrySetResult();

    public Task<VoiceRecording> RecordAsync(CancellationToken cancellationToken) => Task.Run(async () =>
    {
        using var microphone = new WaveIn { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 };
        using var pcm = new MemoryStream();
        var endpoint = new SpeechEndpoint();
        var gate = new object();
        var stopped = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        microphone.DataAvailable += (_, args) =>
        {
            lock (gate)
            {
                if (_stop.Task.IsCompleted) return;
                var count = Math.Min(args.BytesRecorded, 960000 - (int)pcm.Length);
                pcm.Write(args.Buffer, 0, count);
                if (endpoint.Add(args.Buffer.AsSpan(0, count))) Stop();
            }
        };
        microphone.RecordingStopped += (_, args) => stopped.TrySetResult(args.Exception);
        cancellationToken.ThrowIfCancellationRequested();
        microphone.StartRecording();
        try
        {
            var elapsed = Stopwatch.StartNew();
            while (!_stop.Task.IsCompleted && !stopped.Task.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool heard;
                lock (gate) heard = endpoint.HeardSpeech;
                if (elapsed.Elapsed >= TimeSpan.FromSeconds(30) || (!heard && elapsed.Elapsed >= TimeSpan.FromSeconds(8))) break;
                await Task.Delay(50, cancellationToken);
            }
        }
        finally
        {
            Stop();
            microphone.StopRecording();
            await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (await stopped.Task is { } error) throw new IOException("Microphone recording failed.", error);
        lock (gate) return new VoiceRecording(pcm.ToArray(), endpoint.HeardSpeech);
    }, CancellationToken.None);
}
