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
            var button = (Button)window.FindName("CheckForUpdatesButton");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var status = ((TextBlock)window.FindName("UpdateStatusLabel")).Text;
            if (!called || !button.IsEnabled || !status.Contains(shouldFail ? "Fixture unavailable" : "You're up to date"))
                throw new Exception("Manual update button failed to present its result or restore retry.");
            window.Close();
        }
        Console.WriteLine("PASS: Settings loads; manual update button invokes shared check, shows success/failure, and restores retry.");
    }
}
