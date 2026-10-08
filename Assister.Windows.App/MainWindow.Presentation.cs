using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Assister.Windows.App.Services;

namespace Assister.Windows.App;

public partial class MainWindow
{
    private readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private long _lastDisplayActivity = Stopwatch.GetTimestamp();
    private Point? _lastPointerPosition;
    private FullscreenController? _fullscreen;

    private void InitializePresentation()
    {
        _fullscreen = new FullscreenController(this);
        Loaded += (_, _) => { WakeConversation(); _idleTimer.Start(); };
        _idleTimer.Tick += (_, _) => UpdateIdleDisplay();
        PreviewKeyDown += (_, args) =>
        {
            WakeConversation();
            if (args.Key == Key.F11 || (args.Key == Key.Escape && _fullscreen.IsFullscreen))
            {
                ToggleFullscreen();
                args.Handled = true;
            }
        };
        AddHandler(PreviewMouseDownEvent, new MouseButtonEventHandler((_, args) =>
        {
            var wasIdle = IdleClockPane.Visibility == Visibility.Visible;
            WakeConversation();
            if (wasIdle) args.Handled = true;
        }), true);
        AddHandler(PreviewTouchDownEvent, new EventHandler<TouchEventArgs>((_, args) =>
        {
            var wasIdle = IdleClockPane.Visibility == Visibility.Visible;
            WakeConversation();
            if (wasIdle) args.Handled = true;
        }), true);
        PreviewMouseWheel += (_, _) => WakeConversation();
        PreviewMouseMove += (_, args) =>
        {
            var point = args.GetPosition(this);
            if (_lastPointerPosition != point) WakeConversation();
            _lastPointerPosition = point;
        };
        MessageInput.TextChanged += (_, _) => WakeConversation();
        IdleWakePhrase.Text = "“light up”";
    }

    private void WakeConversation()
    {
        _lastDisplayActivity = Stopwatch.GetTimestamp();
        IdleClockPane.Visibility = Visibility.Collapsed;
        ConversationPane.Visibility = Visibility.Visible;
    }

    private void UpdateIdleDisplay()
    {
        var busy = _voiceActive || _playingInteraction is not null || _activeInteraction is not null ||
            _messages.Any(item => item.Status is not ("completed" or "failed" or "cancelled")) ||
            !SendButton.IsEnabled || OwnedWindows.Count > 0;
        if (busy) { WakeConversation(); return; }
        var show = IdleDisplayPolicy.ShouldShow(Stopwatch.GetElapsedTime(_lastDisplayActivity),
            _settings.IdleDisplayTimeoutSeconds, busy, !string.IsNullOrWhiteSpace(MessageInput.Text));
        if (!show)
        {
            ConversationPane.Visibility = Visibility.Visible;
            IdleClockPane.Visibility = Visibility.Collapsed;
            return;
        }
        UpdateIdleClock(DateTimeOffset.Now);
        ConversationPane.Visibility = Visibility.Collapsed;
        IdleClockPane.Visibility = Visibility.Visible;
    }

    private void UpdateIdleClock(DateTimeOffset now)
    {
        IdleTime.Text = now.ToString("t", CultureInfo.CurrentCulture);
        IdleDate.Text = now.ToString("dddd, MMMM d", CultureInfo.CurrentCulture);
    }

    private async Task RefreshIdleWakePhraseAsync()
    {
        if (!_settings.WakeWordEnabled) { IdleWakePhrase.Text = "Wake word off"; return; }
        var path = _settings.WakeWordKeywordsFile;
        var phrase = await Task.Run(() =>
        {
            try
            {
                var labels = File.ReadLines(path).SelectMany(line => line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    .Where(part => part.StartsWith('@') && part.Length > 1)
                    .Select(part => "“" + part[1..].Replace('_', ' ').ToLowerInvariant() + "”").Distinct().Take(3).ToArray();
                return labels.Length > 0 ? string.Join(" · ", labels) : "Wake word";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return path == WakeWordSetup.KeywordsFile || string.IsNullOrWhiteSpace(path) ? "“light up”" : "Wake word unavailable";
            }
        });
        if (!_closed) IdleWakePhrase.Text = phrase;
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        WakeConversation();
        try { _fullscreen!.Toggle(); }
        catch (InvalidOperationException exception) { ShowError(exception.Message); }
        FullscreenButton.Content = _fullscreen!.IsFullscreen ? "⛶   Exit fullscreen" : "⛶   Fullscreen";
    }
}
