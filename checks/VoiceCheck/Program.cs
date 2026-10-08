using System.Net;
using System.Text.Json;
using Assister.Windows.App.Services;

void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
var quiet = new byte[3200];
var talking = new byte[3200];
for (var i = 0; i < talking.Length; i += 2) { talking[i] = 0; talking[i + 1] = 8; }
var endpoint = new SpeechEndpoint();
Require(!endpoint.Add(talking) && endpoint.HeardSpeech, "Speech must start without ending recording.");
for (var i = 0; i < 9; i++) Require(!endpoint.Add(quiet), "Short speech pauses must not end recording.");
Require(!endpoint.Add(talking), "Resumed speech must reset trailing silence.");
for (var i = 0; i < 9; i++) Require(!endpoint.Add(quiet), "Silence timer did not reset.");
Require(endpoint.Add(quiet), "One second of trailing silence must stop recording.");
var empty = new SpeechEndpoint();
for (var i = 0; i < 79; i++) Require(!empty.Add(quiet), "No-speech timeout was too early.");
Require(empty.Add(quiet) && !empty.HeardSpeech, "No speech must end after 8 seconds.");
var bounded = new SpeechEndpoint();
for (var i = 0; i < 299; i++) Require(!bounded.Add(talking), "Maximum duration was too short.");
Require(bounded.Add(talking), "Continuous speech must stop at 30 seconds.");
using var listener = new HttpListener();
listener.Prefixes.Add("http://localhost:59880/");
listener.Start();
var attachmentId = Guid.NewGuid();
var conversationId = Guid.NewGuid();
var requestKey = Guid.NewGuid().ToString();
var fixture = Task.Run(async () =>
{
    var upload = await listener.GetContextAsync();
    Require(upload.Request.Url!.AbsolutePath == "/api/client/attachments" && upload.Request.ContentType == "audio/pcm", "Wrong audio upload contract.");
    using var pcm = new MemoryStream();
    await upload.Request.InputStream.CopyToAsync(pcm);
    Require(pcm.ToArray().SequenceEqual(talking), "Audio bytes changed during upload.");
    await upload.Response.OutputStream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { id = attachmentId }));
    upload.Response.Close();
    var submit = await listener.GetContextAsync();
    using var body = await JsonDocument.ParseAsync(submit.Request.InputStream);
    Require(submit.Request.Url!.AbsolutePath == $"/api/client/conversations/{conversationId}/interactions", "Wrong voice submission path.");
    Require(body.RootElement.GetProperty("audioAttachmentId").GetGuid() == attachmentId && body.RootElement.GetProperty("idempotencyKey").GetString() == requestKey && body.RootElement.GetProperty("speak").GetBoolean(), "Wrong voice submission fields.");
    await submit.Response.OutputStream.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { id = Guid.NewGuid(), conversationId, status = "pending", input = "Voice input", response = "", createdAt = DateTimeOffset.UtcNow, lastSequence = 0 }));
    submit.Response.Close();
});
using var client = new AssisterClient("http://localhost:59880/", new string('x', 24));
var id = await client.UploadVoiceAsync(talking, CancellationToken.None);
await client.SubmitVoiceAsync(conversationId, id, requestKey, CancellationToken.None);
await fixture.WaitAsync(TimeSpan.FromSeconds(5));
Console.WriteLine("PASS: speech/pause detection, no-speech timeout, 30-second limit, raw PCM upload and voice interaction contract.");
