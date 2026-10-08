using System.Text.Json;

namespace Assister.Windows.App.Services;

internal sealed record ToneCue(string Name, DateTimeOffset ExpiresAt, bool AfterSpeech)
{
    public static readonly HashSet<string> Names = ["awake", "confirmed", "intent-match", "ai-think", "ai-thought", "issue", "done", "goodbye", "error"];

    public static ToneCue? Read(JsonElement data, DateTimeOffset now)
    {
        if (data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || !Names.Contains(name.GetString()!) ||
            !data.TryGetProperty("expiresAt", out var expires) || expires.ValueKind != JsonValueKind.String ||
            !expires.TryGetDateTimeOffset(out var deadline) || deadline <= now) return null;
        var placement = data.TryGetProperty("placement", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "immediate";
        return placement is "immediate" or "after-response-audio" ? new(name.GetString()!, deadline, placement == "after-response-audio") : null;
    }
}
