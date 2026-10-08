using System.Net;
using System.Text.Json;
using NAudio.Wave;
using Assister.Windows.App.Services;

void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
byte[] Wav(int bits)
{
    using var buffer = new MemoryStream();
    using (var writer = new WaveFileWriter(new IgnoreDisposeStream(buffer), new WaveFormat(16000, bits, 1))) writer.Write(new byte[3200]);
    return buffer.ToArray();
}
var valid = Wav(16);
using (var reader = ResponseAudio.Open(valid)) Require(reader.Length == 3200, "Valid PCM was not preserved.");
foreach (var invalid in new[] { new byte[44], Wav(8), valid[..30] })
{
    var rejected = false;
    try { using var reader = ResponseAudio.Open(invalid); }
    catch (Exception) { rejected = true; }
    Require(rejected, "Invalid WAV was accepted.");
}
using var listener = new HttpListener();
listener.Prefixes.Add("http://localhost:59881/");
listener.Start();
var interactionId = Guid.NewGuid();
var playbackId = Guid.NewGuid();
var fixture = Task.Run(async () =>
{
    for (var i = 0; i < 6; i++)
    {
        var context = await listener.GetContextAsync();
        Require(context.Request.Headers["Authorization"] == "Bearer " + new string('x', 32), "Missing authentication.");
        var path = context.Request.Url!.AbsolutePath;
        if (i == 0)
        {
            Require(path == $"/api/client/interactions/{interactionId}/audio", "Wrong audio route.");
            context.Response.ContentType = "audio/wav";
            await context.Response.OutputStream.WriteAsync(valid);
        }
        else if (i < 4)
        {
            Require(path == $"/api/client/interactions/{interactionId}/playback" && context.Request.HttpMethod == "POST", "Wrong playback route.");
            using var document = await JsonDocument.ParseAsync(context.Request.InputStream);
            Require(document.RootElement.GetProperty("playbackId").GetGuid() == playbackId, "Playback identity changed.");
            Require(document.RootElement.GetProperty("state").GetString() == new[] { "started", "completed", "failed" }[i - 1], "Wrong playback state.");
        }
        else if (i == 4)
        {
            Require(path.EndsWith("/events"), "Wrong history route.");
            await context.Response.OutputStream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new[] { new { type = "tts.audio" } }));
        }
        else
        {
            context.Response.ContentLength64 = 9 * 1024 * 1024;
            await context.Response.OutputStream.WriteAsync(new byte[1]);
            await context.Response.OutputStream.FlushAsync();
            context.Response.Abort();
            continue;
        }
        context.Response.Close();
    }
});
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
using var client = new AssisterClient("http://localhost:59881", new string('x', 32));
Require((await client.DownloadAudioAsync(interactionId, timeout.Token)).SequenceEqual(valid), "Download changed WAV bytes.");
foreach (var state in new[] { "started", "completed", "failed" }) await client.ReportPlaybackAsync(interactionId, playbackId, state, timeout.Token);
Require(await client.HasAudioAsync(interactionId, timeout.Token), "Historical audio was not restored.");
var oversizedRejected = false;
try { await client.DownloadAudioAsync(interactionId, timeout.Token); }
catch (InvalidDataException) { oversizedRejected = true; }
Require(oversizedRejected, "Oversized audio was accepted.");
await fixture.WaitAsync(timeout.Token);
Console.WriteLine("PASS: PCM WAV validation, malformed/unsupported WAV rejection, authenticated download/report/history contracts, oversized response rejection.");

sealed class IgnoreDisposeStream(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
}
