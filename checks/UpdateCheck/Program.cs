using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Assister.Windows.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // Use a plain Application so the fixture never starts app networking or installs a release.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var previous = new Window();
        app.MainWindow = previous;
        previous.Show();
        var window = new UpdateWindow();
        app.MainWindow = window;
        window.Show();
        previous.Close();
        Check(window.IsVisible && !previous.IsVisible, "progress window survives closing the main window");
        window.Close();
        Check(window.IsVisible, "installation cannot be interrupted by closing its window");

        void Call(string method, params object[] args) => typeof(UpdateWindow).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
        var frame = new DispatcherFrame();
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        var progress = new Progress<string>(status => { Call("Report", status); frame.Continue = false; });
        Task.Run(() => ((IProgress<string>)progress).Report("Installing the verified update…")).GetAwaiter().GetResult();
        Dispatcher.PushFrame(frame);
        Check(((TextBlock)window.FindName("StatusLabel")).Text.StartsWith("Installing"), "worker progress reaches the UI dispatcher");
        Render(window, "update-progress");
        Call("ShowFailure", "The update failed. The previous app was restarted. Try the update again later.");
        Check(((Button)window.FindName("CloseButton")).Visibility == Visibility.Visible && !((ProgressBar)window.FindName("UpdateProgress")).IsIndeterminate, "failure stops progress and offers Close");
        Render(window, "update-error");
        window.Close();
        Check(!window.IsVisible, "failure window can close");
        var completed = new UpdateWindow();
        completed.Show();
        typeof(UpdateWindow).GetMethod("Complete", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(completed, null);
        Check(!completed.IsVisible, "successful update closes progress");
        app.Shutdown();
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        Console.WriteLine("PASS " + label);
    }

    private static void Render(Window window, string name)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        var content = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
            drawing.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory("artifacts/ui");
        using var output = File.Create($"artifacts/ui/{name}.png");
        encoder.Save(output);
    }
}
