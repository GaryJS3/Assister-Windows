using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Assister.Windows.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var assembly = typeof(SettingsWindow).Assembly;
        var settings = Activator.CreateInstance(assembly.GetType("Assister.Windows.App.Services.AppSettings")!, nonPublic: true)!;
        var constructor = typeof(SettingsWindow).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        foreach (var shouldFail in new[] { false, true })
        {
            var called = false;
            Func<CancellationToken, Task<string>> check = token =>
            {
                called = true;
                return shouldFail ? Task.FromException<string>(new InvalidOperationException("Fixture unavailable")) : Task.FromResult("You're up to date · fixture");
            };
            var window = (SettingsWindow)constructor.Invoke([settings, check]);
            var timeout = (TextBox)window.FindName("WakeWordConversationTimeoutInput");
            if (timeout.Text != "60") throw new Exception("Wake conversation timeout must default to 60 seconds.");
            var clockTimeout = (TextBox)window.FindName("IdleDisplayTimeoutInput");
            if (clockTimeout.Text != "180" || ((CheckBox)window.FindName("AutoplayResponsesInput")).IsChecked != true)
                throw new Exception("Clock and autoplay defaults are incorrect.");
            foreach (var invalid in new[] { "-1", "86401", "abc", "1.5" })
            {
                timeout.Text = invalid;
                ((Button)window.FindName("SaveSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!((TextBlock)window.FindName("ErrorLabel")).Text.Contains("idle timeout"))
                    throw new Exception("Invalid conversation timeout must be rejected before saving credentials.");
            }
            timeout.Text = "60";
            foreach (var invalid in new[] { "-1", "86401", "abc", "1.5" })
            {
                clockTimeout.Text = invalid;
                ((Button)window.FindName("SaveSettingsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (!((TextBlock)window.FindName("ErrorLabel")).Text.Contains("clock timeout"))
                    throw new Exception("Invalid clock timeout must be rejected before saving credentials.");
            }
            var button = (Button)window.FindName("CheckForUpdatesButton");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var status = ((TextBlock)window.FindName("UpdateStatusLabel")).Text;
            if (!called || !button.IsEnabled || !status.Contains(shouldFail ? "Fixture unavailable" : "You're up to date"))
                throw new Exception("Manual update button failed to present its result or restore retry.");
            window.Close();
        }
        Console.WriteLine("PASS: Settings defaults (wake 60s, clock 180s, autoplay on), invalid wake/clock timeouts rejected; manual update feedback and retry.");
    }
}
