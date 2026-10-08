using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Assister.Windows.App;
using Assister.Windows.App.Controls;
using Assister.Windows.App.Services;

internal static class Program
{
    private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); Console.WriteLine("PASS " + label); }
    [STAThread]
    private static void Main()
    {
        Check(!IdleDisplayPolicy.ShouldShow(TimeSpan.FromSeconds(179), 180, false, false) && IdleDisplayPolicy.ShouldShow(TimeSpan.FromSeconds(180), 180, false, false), "three-minute boundary");
        Check(!IdleDisplayPolicy.ShouldShow(TimeSpan.FromHours(1), 0, false, false), "disabled clock");
        Check(!IdleDisplayPolicy.ShouldShow(TimeSpan.FromHours(1), 180, true, false) && !IdleDisplayPolicy.ShouldShow(TimeSpan.FromHours(1), 180, false, true), "busy and draft protection");
        Check(IdleDisplayPolicy.ShouldShow(TimeSpan.FromSeconds(5), 5, false, false), "custom clock timeout");

        var main = new MainWindow(); // Not shown: app Loaded never starts networking/audio/updater.
        var root = (Grid)main.Content;
        var scroll = (DragScrollViewer)main.FindName("ConversationScroll");
        Check(scroll.PanningMode == PanningMode.VerticalOnly && scroll.VerticalScrollBarVisibility == ScrollBarVisibility.Hidden, "touch panning enabled; scrollbar hidden without disabling scrolling");
        Check(main.FindName("ProtocolLabel") is null && main.FindName("AutoplayResponses") is null, "subtitle and footer autoplay removed");
        var settingsButtons = Descendants(root).OfType<Button>().Count(button => button.Content?.ToString()?.Contains("Settings") == true);
        Check(settingsButtons == 1, "single sidebar Settings button");

        var privateFlags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Call(string method, params object[] arguments) => typeof(MainWindow).GetMethod(method, privateFlags)!.Invoke(main, arguments);
        var messages = (StackPanel)main.FindName("MessagesPanel");
        for (var index = 0; index < 12; index++) Call("AddBubble", "ASSISTER", $"Message {index + 1}: The conversation stays readable and can be dragged to review earlier responses.", false, false);
        var count = messages.Children.Count;
        typeof(MainWindow).GetField("_lastDisplayActivity", privateFlags)!.SetValue(main, Stopwatch.GetTimestamp() - 181L * Stopwatch.Frequency);
        Call("UpdateIdleDisplay");
        Check(((Border)main.FindName("IdleClockPane")).Visibility == Visibility.Visible && ((Grid)main.FindName("ConversationPane")).Visibility == Visibility.Collapsed, "timeout replaces conversation with clock");
        Call("UpdateIdleClock", new DateTimeOffset(2026, 10, 7, 20, 45, 0, TimeSpan.FromHours(-4)));
        ((TextBlock)main.FindName("IdleWakePhrase")).Text = "“light up”";
        Directory.CreateDirectory("artifacts/ui");
        Render(root, 1280, 820, "idle-clock");
        Render(root, 900, 1000, "idle-clock-portrait");
        Call("WakeConversation");
        Check(((Border)main.FindName("IdleClockPane")).Visibility == Visibility.Collapsed && messages.Children.Count == count, "wake restores existing conversation");
        ((TextBox)main.FindName("MessageInput")).Text = "Unsent draft";
        typeof(MainWindow).GetField("_lastDisplayActivity", privateFlags)!.SetValue(main, Stopwatch.GetTimestamp() - 181L * Stopwatch.Frequency);
        Call("UpdateIdleDisplay");
        Check(((Border)main.FindName("IdleClockPane")).Visibility == Visibility.Collapsed, "real composer draft prevents idle screen");
        ((TextBox)main.FindName("MessageInput")).Clear();

        // Exercise actual WPF scrolling and monitor bounds in a dedicated fixture window.
        // The fixture has no app lifecycle handlers, credentials or server connection.
        main.Content = null;
        root.Width = double.NaN; root.Height = double.NaN;
        var host = new Window { Title = "Assister presentation check", Content = root, Width = 1280, Height = 820, Left = 40, Top = 40, ShowActivated = false };
        host.Show();
        host.UpdateLayout();
        scroll.ScrollToEnd();
        Pump();
        Check(scroll.VerticalOffset > 0, "hidden scrollbar still permits scrolling");
        scroll.ScrollToVerticalOffset(0);
        Pump();
        Check(scroll.VerticalOffset == 0, "can return to earlier messages");
        var fullscreen = new FullscreenController(host);
        var previous = new Rect(host.Left, host.Top, host.Width, host.Height);
        fullscreen.Toggle();
        Pump();
        var handle = new WindowInteropHelper(host).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        Check(GetMonitorInfo(MonitorFromWindow(handle, 2), ref info) && GetWindowRect(handle, out var actual) &&
            actual.Left == info.Monitor.Left && actual.Top == info.Monitor.Top && actual.Right == info.Monitor.Right && actual.Bottom == info.Monitor.Bottom,
            "fullscreen covers entire monitor including taskbar");
        Check(host.WindowStyle == WindowStyle.None && host.ResizeMode == ResizeMode.NoResize && host.Topmost, "fullscreen removes window chrome");
        fullscreen.Toggle();
        Pump();
        Check(!fullscreen.IsFullscreen && host.WindowStyle == WindowStyle.SingleBorderWindow && !host.Topmost && Math.Abs(host.Width - previous.Width) < 1 && Math.Abs(host.Left - previous.Left) < 1, "exit fullscreen restores window");
        host.Close();
        main.Close();
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
    private static void Render(Grid root, int width, int height, string name)
    {
        root.Width = width; root.Height = height;
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create($"artifacts/ui/{name}.png"); encoder.Save(file);
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
}
