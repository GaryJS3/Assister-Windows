using System.Text.Json;
using Assister.Windows.App.Services;

void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
var now = DateTimeOffset.Parse("2026-10-07T23:00:00Z");
bool Reset(int age, int timeout = 60, bool hasMessages = true) => ConversationIdlePolicy.ShouldStartNew(hasMessages, now.AddSeconds(-age), now, timeout);
Require(!Reset(59) && !Reset(60) && Reset(61), "One-minute boundary is wrong.");
Require(!Reset(1000, 0), "Disabled policy must preserve conversation.");
Require(!Reset(1000, hasMessages: false), "Empty conversations must be reused.");
Require(!Reset(61, 120) && Reset(121, 120), "Custom timeout was ignored.");
Require(!Reset(-5), "Future activity must not reset conversation.");
Require(!ConversationIdlePolicy.ShouldStartNew(true, null, now, 60), "Unknown activity must not discard context.");
var settings = JsonSerializer.Deserialize<AppSettings>("{\"ConversationId\":\"00000000-0000-0000-0000-000000000001\"}")!;
Require(settings.WakeWordConversationTimeoutSeconds == 60, "Existing settings must adopt the default.");
Require(settings.IdleDisplayTimeoutSeconds == 180 && settings.AutoplayResponses, "Existing settings must adopt the clock/autoplay defaults.");
settings.IdleDisplayTimeoutSeconds = 300;
settings.AutoplayResponses = false;
var displaySettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
Require(displaySettings.IdleDisplayTimeoutSeconds == 300 && !displaySettings.AutoplayResponses, "Display preferences must persist.");
foreach (var timeout in new[] { 0, 60, 120, 86400 })
{
    settings.WakeWordConversationTimeoutSeconds = timeout;
    settings.LastConversationActivityUtc = now.AddSeconds(-61);
    var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
    Require(restored.WakeWordConversationTimeoutSeconds == timeout && restored.LastConversationActivityUtc == settings.LastConversationActivityUtc, "Idle settings/activity did not persist.");
    Require(ConversationIdlePolicy.ShouldStartNew(true, restored.LastConversationActivityUtc, now, timeout) == (timeout == 60), "Restored idle policy changed behavior.");
}
Console.WriteLine("PASS: idle boundary, disabled/custom timeout, empty conversation, future/missing activity, old settings defaults and persisted activity.");
