using System.Net;
using System.Text.Json;
using Assister.Windows.App.Services;

void Require(bool value, string message) { if (!value) throw new Exception(message); }
var now = DateTimeOffset.UtcNow;
JsonElement Cue(string name, DateTimeOffset expiry, string placement = "immediate") =>
    JsonSerializer.SerializeToElement(new { name, expiresAt = expiry, placement, url = "https://untrusted.example/tone.wav" });
foreach (var name in ToneCue.Names)
    Require(ToneCue.Read(Cue(name, now.AddSeconds(5)), now)?.Name == name, "Canonical tone rejected.");
Require(ToneCue.Read(Cue("confirmed", now), now) is null, "Expired tone accepted.");
Require(ToneCue.Read(Cue("../other", now.AddSeconds(5)), now) is null, "Unknown name accepted.");
Require(ToneCue.Read(Cue("confirmed", now.AddSeconds(5), "unknown"), now) is null, "Unknown placement accepted.");
Require(ToneCue.Read(JsonSerializer.SerializeToElement(new { name = "confirmed", expiresAt = 10 }), now) is null, "Malformed expiry accepted.");
Require(ToneCue.Read(Cue("goodbye", now.AddSeconds(5), "after-response-audio"), now)?.AfterSpeech == true, "Deferred placement lost.");
Require(JsonSerializer.Deserialize<AppSettings>("{}")!.ProcessingSoundsEnabled, "Existing settings must enable tones.");
var muted = new AppSettings { ProcessingSoundsEnabled = false };
Require(!JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(muted))!.ProcessingSoundsEnabled, "Mute preference lost.");
using var listener = new HttpListener();
listener.Prefixes.Add("http://localhost:59883/");
listener.Start();
var fixture = Task.Run(async () =>
{
    var request = await listener.GetContextAsync();
    Require(request.Request.Url!.AbsolutePath == "/api/voice/tones/confirmed.wav", "Wrong tone path.");
    Require(request.Request.Headers["Authorization"] == "Bearer " + new string('x', 32), "Missing bearer.");
    await request.Response.OutputStream.WriteAsync(new byte[] { 1, 2, 3 });
    request.Response.Close();
});
using var client = new AssisterClient("http://localhost:59883/", new string('x', 32));
Require((await client.DownloadToneAsync("confirmed", CancellationToken.None)).SequenceEqual(new byte[] { 1, 2, 3 }), "Download altered bytes.");
await fixture;
Console.WriteLine("PASS: canonical cues, expiry, malformed/unknown cues, deferred placement, default-on and mute persistence, same-origin authenticated tone download.");
