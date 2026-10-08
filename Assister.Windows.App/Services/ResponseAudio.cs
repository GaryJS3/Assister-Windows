using System.IO;
using NAudio.Wave;

namespace Assister.Windows.App.Services;

internal static class ResponseAudio
{
    public static WaveFileReader Open(byte[] bytes)
    {
        var stream = new MemoryStream(bytes, writable: false);
        try
        {
            var reader = new WaveFileReader(stream);
            var format = reader.WaveFormat;
            if (format.Encoding != WaveFormatEncoding.Pcm || format.BitsPerSample != 16 ||
                format.Channels is < 1 or > 2 || format.SampleRate is < 8000 or > 96000 ||
                reader.Length <= 0 || reader.Length > 8 * 1024 * 1024 || reader.Length % format.BlockAlign != 0)
            {
                reader.Dispose();
                throw new InvalidDataException("Unsupported response WAV format.");
            }
            return reader;
        }
        catch { stream.Dispose(); throw; }
    }

    public static async Task PlayAsync(byte[] bytes, Func<Task> onStarted, CancellationToken cancellationToken)
    {
        using var reader = Open(bytes);
        using var output = new WaveOut();
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        output.PlaybackStopped += (_, args) =>
        {
            if (args.Exception is { } exception) finished.TrySetException(exception);
            else finished.TrySetResult();
        };
        output.Init(reader);
        cancellationToken.ThrowIfCancellationRequested();
        output.Play();
        using var registration = cancellationToken.Register(output.Stop);
        await onStarted();
        await finished.Task;
        cancellationToken.ThrowIfCancellationRequested();
    }
}
