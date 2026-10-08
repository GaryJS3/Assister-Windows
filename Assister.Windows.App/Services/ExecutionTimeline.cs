using System.Diagnostics;
using System.Text.Json;

namespace Assister.Windows.App.Services;

internal sealed class ExecutionTimeline
{
    private DateTimeOffset? _eventTime;
    private long _receivedAt;
    private long _sequence;
    public bool Terminal { get; private set; }
    public List<ExecutionStep> Steps { get; } = [];
    public DateTimeOffset Now => (_eventTime ?? DateTimeOffset.UtcNow) +
        (Terminal || _eventTime is null ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_receivedAt));

    public void Apply(JsonElement root)
    {
        if (!root.TryGetProperty("sequence", out var cursor) || !cursor.TryGetInt64(out var sequence) || sequence <= _sequence) return;
        _sequence = sequence;
        var type = Text(root, "type");
        if (!Terminal && root.TryGetProperty("timestamp", out var timestamp) && timestamp.ValueKind == JsonValueKind.String && timestamp.TryGetDateTimeOffset(out var time))
        {
            _eventTime = time;
            _receivedAt = Stopwatch.GetTimestamp();
        }
        if (type is "interaction.completed" or "interaction.failed" or "interaction.cancelled")
        {
            var now = _eventTime ?? Now;
            _eventTime = now;
            Terminal = true;
            foreach (var running in Steps.Where(step => step.Status == "running"))
            {
                running.FinishedAt = now;
                running.Status = type == "interaction.cancelled" ? "cancelled" : "interrupted";
            }
        }
        if (type is not ("step.started" or "step.updated" or "step.completed" or "step.failed") ||
            !root.TryGetProperty("data", out var data)) return;
        var id = Text(data, "stepId");
        if (id.Length == 0) return;
        var step = Steps.FirstOrDefault(value => value.Id == id);
        if (step is null) { step = new ExecutionStep(id); Steps.Add(step); }
        step.Label = Text(data, "label", step.Label);
        step.Kind = Text(data, "kind", step.Kind);
        step.ParentId = Text(data, "parentStepId", step.ParentId);
        if (type == "step.started") { step.StartedAt ??= _eventTime; step.Status = "running"; }
        if (type is "step.completed" or "step.failed")
        {
            step.Status = Text(data, "status", type[5..]);
            step.FinishedAt = _eventTime;
            if (data.TryGetProperty("durationMs", out var duration) && duration.TryGetDouble(out var milliseconds) && double.IsFinite(milliseconds) && milliseconds >= 0)
                step.DurationMs = milliseconds;
        }
    }

    private static string Text(JsonElement value, string name, string fallback = "") =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? fallback : fallback;
}

internal sealed class ExecutionStep(string id)
{
    public string Id { get; } = id;
    public string Label { get; set; } = "Step";
    public string Kind { get; set; } = "";
    public string ParentId { get; set; } = "";
    public string Status { get; set; } = "pending";
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public double? DurationMs { get; set; }
    public double? ElapsedMs(DateTimeOffset now) => DurationMs ?? (StartedAt is { } start ? Math.Max(0, ((FinishedAt ?? now) - start).TotalMilliseconds) : null);
}
