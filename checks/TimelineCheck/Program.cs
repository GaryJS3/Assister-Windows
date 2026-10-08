using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Assister.Windows.App;
using Assister.Windows.App.Controls;
using Assister.Windows.App.Services;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var timeline = new ExecutionTimeline();
        void Apply(long sequence, string type, string time, object data)
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(new { sequence, type, timestamp = time, data }));
            timeline.Apply(json.RootElement);
        }
        void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS " + label); }
        Apply(1, "step.started", "2026-10-07T12:00:00Z", new { stepId = "model", label = "Understand the request", kind = "LanguageModel" });
        Apply(2, "step.started", "2026-10-07T12:00:01Z", new { stepId = "tool", parentStepId = "model", label = "Look up the living room lights", kind = "ToolCall" });
        Check(timeline.Steps[0].ElapsedMs(timeline.Now) >= 1000, "server-relative live clock");
        Apply(3, "step.completed", "2026-10-07T12:00:02Z", new { stepId = "tool", durationMs = 850.0 });
        Check(timeline.Steps[1].ElapsedMs(timeline.Now) == 850, "authoritative duration and preserved label/parent");
        Apply(3, "step.started", "2026-10-07T12:00:03Z", new { stepId = "tool" });
        Check(timeline.Steps.Count == 2 && timeline.Steps[1].Status == "completed", "duplicate replay ignored");
        Apply(4, "interaction.cancelled", "2026-10-07T12:00:04Z", new { });
        var frozen = timeline.Now;
        Check(timeline.Terminal && timeline.Now == frozen && timeline.Steps[0].ElapsedMs(frozen) == 4000, "terminal freezes running steps");
        var unknown = new ExecutionStep("unknown");
        Check(unknown.ElapsedMs(frozen) is null, "missing start stays unknown");
        unknown.StartedAt = frozen.AddSeconds(1);
        Check(unknown.ElapsedMs(frozen) == 0, "negative elapsed clamped");

        var window = new MainWindow(); // Do not Show: prevents Loaded startup I/O and microphone use.
        var bubble = typeof(MainWindow).GetMethod("AddBubble", BindingFlags.Instance | BindingFlags.NonPublic)!;
        bubble.Invoke(window, ["YOU", "Can you turn on the living room lights?", true, false]);
        bubble.Invoke(window, ["ASSISTER", "The living room lights are on. I set them to a warm, comfortable brightness.", false, false]);
        var messages = (StackPanel)window.FindName("MessagesPanel");
        messages.Children.Add(new ExecutionTimelineView(timeline, true, true, _ => { }));
        ((TextBlock)window.FindName("ConversationTitle")).Text = "Living room lights";
        ((TextBox)window.FindName("MessageInput")).Text = "Ask Assister…";
        var root = (Grid)window.Content;
        Check(root.ColumnDefinitions.Count == 2, "one sidebar plus conversation");
        Check(window.FontSize == 18 && ((TextBox)window.FindName("MessageInput")).FontSize == 22, "distance-readable text");
        Directory.CreateDirectory("artifacts/ui");
        foreach (var (width, height) in new[] { (1280, 820), (900, 1000) })
        {
            root.Width = width; root.Height = height;
            root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create($"artifacts/ui/conversation-{width}.png"); encoder.Save(file);
        }
        Console.WriteLine("PASS WPF layout renders at 1280x820 and 900x1000");
    }
}
