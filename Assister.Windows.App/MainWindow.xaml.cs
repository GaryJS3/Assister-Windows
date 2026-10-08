using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Assister.Windows.App.Services;

namespace Assister.Windows.App;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly ObservableCollection<ChatItem> _messages = [];
    private AssisterClient? _client;
    private CancellationTokenSource? _connectionLifetime;
    private Guid _conversationId;
    private Guid? _activeInteraction;
    private readonly CancellationTokenSource _updateLifetime = new();
    private CancellationTokenSource? _wakeWordLifetime;
    private Task _wakeWordTask = Task.CompletedTask;
    private CancellationTokenSource? _playbackLifetime;
    private readonly SemaphoreSlim _playbackGate = new(1, 1);
    private Task _playbackTask = Task.CompletedTask;
    private Guid? _playingInteraction;
    private bool _closed;
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private bool _updatePending;
    private bool _supportsVoice;
    private bool _voiceActive;
    private bool _voiceSending;
    private bool _voiceSendAttempted;
    private CancellationTokenSource? _voiceLifetime;
    private VoiceRecorder? _voiceRecorder;
    private Task<VoiceRecording>? _captureTask;
    private VoiceRecording? _recording;
    private Guid? _voiceAttachmentId;
    private string? _voiceRequestKey;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            _ = UpdateLoopAsync();
            await RestartWakeWordAsync();
            EndpointLabel.Text = _settings.Endpoint;
            if (!string.IsNullOrWhiteSpace(CredentialStore.Read() ?? CredentialStore.ReadDevelopmentToken())) await ConnectAsync();
            else AddWelcome();
        };
        Closed += (_, _) => { _closed = true; _playbackLifetime?.Cancel(); _voiceLifetime?.Cancel(); _wakeWordLifetime?.Cancel(); _updateLifetime.Cancel(); _connectionLifetime?.Cancel(); _client?.Dispose(); };
    }

    private async Task RestartWakeWordAsync()
    {
        _wakeWordLifetime?.Cancel();
        await _wakeWordTask;
        _wakeWordLifetime?.Dispose();
        if (_closed) return;
        WakeWordStatus.Text = "Wake word off";
        if (!_settings.WakeWordEnabled || _voiceActive || _playingInteraction is not null) return;
        var lifetime = _wakeWordLifetime = new CancellationTokenSource();
        var detector = new WakeWordDetector();
        void OnUi(Action action) => Dispatcher.BeginInvoke(() =>
        {
            if (!_closed && !lifetime.IsCancellationRequested) action();
        });
        detector.StatusChanged += status => OnUi(() => WakeWordStatus.Text = status);
        detector.Detected += keyword => OnUi(() =>
        {
            _ = BeginVoiceAsync();
        });
        WakeWordStatus.Text = "Starting wake word…";
        _wakeWordTask = detector.RunAsync(_settings.WakeWordModelDirectory, _settings.WakeWordKeywordsFile, lifetime.Token);
    }

    private async Task UpdateLoopAsync()
    {
        var token = _updateLifetime.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await CheckForUpdatesAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception) { AutoUpdater.Log($"Update check failed: {exception.GetType().Name}."); }
            try { await Task.Delay(TimeSpan.FromMinutes(15), token); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task BeginVoiceAsync()
    {
        if (_voiceActive || _activeInteraction is not null || OwnedWindows.Count > 0) return;
        if (_client is null || !_supportsVoice) { ShowError("Connect to a server supporting audio input before recording a voice command."); return; }
        _voiceActive = true;
        _voiceSendAttempted = false;
        _voiceLifetime = new CancellationTokenSource();
        var token = _voiceLifetime.Token;
        _voiceRequestKey = Guid.NewGuid().ToString();
        _voiceAttachmentId = null;
        _recording = null;
        TypedComposer.Visibility = Visibility.Collapsed;
        VoiceComposer.Visibility = Visibility.Visible;
        VoiceNotice.Text = "Listening....";
        VoiceSendButton.IsEnabled = false;
        VoiceCancelButton.IsEnabled = true;
        Keyboard.ClearFocus();
        VoiceCancelButton.Focus();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        WakeWordStatus.Text = "Recording command · wake word paused";
        try
        {
            _wakeWordLifetime?.Cancel();
            await _wakeWordTask;
            await StopPlaybackAsync();
            token.ThrowIfCancellationRequested();
            if (ConversationIdlePolicy.ShouldStartNew(_messages.Count > 0, _settings.LastConversationActivityUtc,
                DateTimeOffset.UtcNow, _settings.WakeWordConversationTimeoutSeconds))
            {
                VoiceNotice.Text = "Starting a new conversation…";
                await CreateNewConversationAsync(token);
                token.ThrowIfCancellationRequested();
                VoiceNotice.Text = "Listening....";
            }
            _voiceRecorder = new VoiceRecorder();
            _captureTask = _voiceRecorder.RecordAsync(token);
            VoiceSendButton.IsEnabled = true;
            _recording = await _captureTask;
            token.ThrowIfCancellationRequested();
            if (_voiceSending || _voiceSendAttempted) return;
            if (!_recording.HeardSpeech)
            {
                ShowError("No speech detected. Say the wake word again to try recording.");
                await EndVoiceAsync();
                return;
            }
            await SendVoiceAsync();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (!_closed && !_voiceSending) { ShowError($"Could not record: {exception.Message}"); await EndVoiceAsync(); }
        }
    }

    private async Task EndVoiceAsync()
    {
        _voiceLifetime?.Cancel();
        if (_captureTask is not null)
        {
            try { await _captureTask; }
            catch (Exception) { }
        }
        _voiceLifetime?.Dispose();
        _voiceLifetime = null;
        _captureTask = null;
        _voiceRecorder = null;
        _recording = null;
        _voiceActive = false;
        if (_closed) return;
        VoiceComposer.Visibility = Visibility.Collapsed;
        TypedComposer.Visibility = Visibility.Visible;
        await RestartWakeWordAsync();
    }

    private async void VoiceCancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_voiceActive || _voiceSending) return;
        VoiceSendButton.IsEnabled = false;
        VoiceCancelButton.IsEnabled = false;
        await EndVoiceAsync();
    }

    private async void VoiceSend_Click(object sender, RoutedEventArgs e) => await SendVoiceAsync();

    private async Task SendVoiceAsync()
    {
        if (!_voiceActive || _voiceSending || _captureTask is null || _client is null || _voiceLifetime?.IsCancellationRequested != false) return;
        _voiceSending = true;
        _voiceSendAttempted = true;
        VoiceSendButton.IsEnabled = false;
        VoiceCancelButton.IsEnabled = false;
        try
        {
            _voiceRecorder?.Stop();
            var recording = await _captureTask;
            if (!recording.HeardSpeech) { ShowError("No speech detected."); await EndVoiceAsync(); return; }
            VoiceNotice.Text = "Sending voice…";
            var token = _connectionLifetime?.Token ?? CancellationToken.None;
            _voiceAttachmentId ??= await _client.UploadVoiceAsync(recording.Pcm, token);
            var item = await _client.SubmitVoiceAsync(_conversationId, _voiceAttachmentId.Value, _voiceRequestKey!, token);
            MarkConversationActivity();
            var chatItem = new ChatItem(item.Id, "Transcribing voice…", "", item.Status, item.LastSequence);
            if (_messages.All(message => message.Id != item.Id)) _messages.Add(chatItem);
            _activeInteraction = item.Id;
            CancelButton.Visibility = Visibility.Visible;
            RenderMessages();
            _ = ObserveInteractionAsync(_client, chatItem, 0, token);
            await EndVoiceAsync();
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                VoiceNotice.Text = "Send failed · retry or cancel";
                ShowError($"Could not send voice: {exception.Message}. Retry uses the same request key; cancellation cannot undo a request already accepted by the server.");
            }
        }
        finally
        {
            _voiceSending = false;
            if (!_closed) { VoiceSendButton.IsEnabled = true; VoiceCancelButton.IsEnabled = true; }
        }
    }

    private async Task<string> CheckForUpdatesAsync(CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _updateLifetime.Token);
        await _updateGate.WaitAsync(lifetime.Token);
        try
        {
            if (_updatePending) return "Update ready. Close Settings; Assister will restart when the chat is idle.";
            var staged = await AutoUpdater.DownloadAsync(_settings.Endpoint, lifetime.Token);
            if (staged is null) return $"You're up to date · {AutoUpdater.CurrentVersion}";
            _updatePending = true;
            _ = InstallWhenIdleAsync(staged);
            return "Update ready. Close Settings; Assister will restart when the chat is idle.";
        }
        finally { _updateGate.Release(); }
    }

    private async Task InstallWhenIdleAsync(string staged)
    {
        try
        {
            var token = _updateLifetime.Token;
            while (_playingInteraction is not null || _voiceActive || _activeInteraction is not null || !string.IsNullOrWhiteSpace(MessageInput.Text) || !SendButton.IsEnabled || OwnedWindows.Count > 0)
                await Task.Delay(TimeSpan.FromSeconds(1), token);
            token.ThrowIfCancellationRequested();
            AutoUpdater.BeginInstall(staged);
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException) when (_updateLifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _updatePending = false;
            AutoUpdater.Log($"Update installation failed: {exception.GetType().Name}.");
            if (!_closed) ShowError($"Could not install the update: {exception.Message}");
        }
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceActive) { ShowError("Send or cancel the voice recording before opening Settings."); return; }
        var dialog = new SettingsWindow(_settings, CheckForUpdatesAsync) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            await RestartWakeWordAsync();
            await ConnectAsync();
        }
    }

    private async Task ConnectAsync()
    {
        await StopPlaybackAsync();
        _connectionLifetime?.Cancel();
        _client?.Dispose();
        _connectionLifetime = new CancellationTokenSource();
        var token = _connectionLifetime.Token;
        SetConnection("Connecting…", "#E4A95C");
        AssisterClient? candidate = null;
        try
        {
            candidate = new AssisterClient(_settings.Endpoint, CredentialStore.Read() ?? CredentialStore.ReadDevelopmentToken() ?? "");
            using var protocol = await candidate.ConnectAsync(token);
            _supportsVoice = protocol.RootElement.TryGetProperty("features", out var voiceFeatures) && voiceFeatures.EnumerateArray().Any(feature => feature.GetString() == "audio.input");
            var features = protocol.RootElement.TryGetProperty("features", out var featureArray)
                ? string.Join(" · ", featureArray.EnumerateArray().Select(item => item.GetString())) : "Protocol v1";
            var client = candidate;
            _client = client;
            candidate = null;
            if (_settings.ConversationId is null)
            {
                _conversationId = await client.CreateConversationAsync(token);
                _settings.ConversationId = _conversationId;
                _settings.LastConversationActivityUtc = null;
                _settings.Save();
            }
            else _conversationId = _settings.ConversationId.Value;

            var history = await client.GetHistoryAsync(_conversationId, token);
            if (history.Count > 0)
            {
                var latestMessage = history.Max(item => item.CreatedAt);
                if (_settings.LastConversationActivityUtc is null || latestMessage > _settings.LastConversationActivityUtc)
                {
                    _settings.LastConversationActivityUtc = latestMessage;
                    _settings.Save();
                }
            }
            _messages.Clear();
            _activeInteraction = null;
            CancelButton.Visibility = Visibility.Collapsed;
            foreach (var interaction in history)
            {
                var active = interaction.Status is not ("completed" or "failed" or "cancelled");
                var item = new ChatItem(interaction.Id, interaction.Input, active ? "" : interaction.Response, interaction.Status, interaction.LastSequence);
                item.AutoPlay = false;
                _messages.Add(item);
                if (active)
                {
                    _activeInteraction = interaction.Id;
                    CancelButton.Visibility = Visibility.Visible;
                    _ = ObserveInteractionAsync(client, item, 0, token);
                }
            }
            _ = RestoreAudioAsync(client, token);
            RenderMessages();
            ConversationTitle.Text = history.LastOrDefault()?.Input is { Length: > 0 } title ? title[..Math.Min(48, title.Length)] : "New conversation";
            ProtocolLabel.Text = features;
            EndpointLabel.Text = _settings.Endpoint;
            SetConnection("Connected", "#74D6B2");
            if (history.Count == 0) AddWelcome();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            _client?.Dispose();
            _client = null;
            SetConnection("Connection failed", "#E27C7C");
            ProtocolLabel.Text = exception.Message;
            AddWelcome("Could not connect. Open Settings to check the server and token.");
        }
        finally { candidate?.Dispose(); }
    }

    private async void NewConversation_Click(object sender, RoutedEventArgs e)
    {
        if (_voiceActive) { ShowError("Send or cancel the voice recording before starting a new conversation."); return; }
        if (_client is null) { Settings_Click(sender, e); return; }
        try
        {
            await StopPlaybackAsync();
            await CreateNewConversationAsync(_connectionLifetime?.Token ?? CancellationToken.None);
        }
        catch (Exception exception) { ShowError(exception.Message); }
    }

    private async Task CreateNewConversationAsync(CancellationToken token)
    {
        var conversationId = await _client!.CreateConversationAsync(token);
        token.ThrowIfCancellationRequested();
        _conversationId = conversationId;
        _settings.ConversationId = conversationId;
        _settings.LastConversationActivityUtc = null;
        _settings.Save();
        _messages.Clear();
        _activeInteraction = null;
        CancelButton.Visibility = Visibility.Collapsed;

        ConversationTitle.Text = "New conversation";
        AddWelcome();
    }

    private void MarkConversationActivity()
    {
        _settings.LastConversationActivityUtc = DateTimeOffset.UtcNow;
        try { _settings.Save(); }
        catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException)
        {
            ShowError("Could not save conversation activity: " + exception.Message);
        }
    }

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendMessageAsync();

    private async void MessageInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            await SendMessageAsync();
        }
    }

    private async Task SendMessageAsync()
    {
        if (_voiceActive) return;
        var message = MessageInput.Text.Trim();
        if (message.Length == 0) return;
        if (message.Length > 1000) { ShowError("Messages are limited to 1,000 characters."); return; }
        if (_client is null) { ShowError("Connect to an Assister server in Settings before sending."); return; }

        await StopPlaybackAsync();
        SendButton.IsEnabled = false;
        try
        {
            var item = await _client.SubmitAsync(_conversationId, message, Guid.NewGuid().ToString(), _connectionLifetime?.Token ?? CancellationToken.None);
            MarkConversationActivity();
            var chatItem = new ChatItem(item.Id, message, "", item.Status, item.LastSequence);
            _activeInteraction = item.Id;
            CancelButton.Visibility = Visibility.Visible;
            _messages.Add(chatItem);
            MessageInput.Clear();
            ConversationTitle.Text = message[..Math.Min(48, message.Length)];
            RenderMessages();
            _ = ObserveInteractionAsync(_client, chatItem, 0, _connectionLifetime?.Token ?? CancellationToken.None);
        }
        catch (Exception exception) { ShowError(exception.Message); }
        finally { SendButton.IsEnabled = true; }
    }

    private async Task ObserveInteractionAsync(AssisterClient client, ChatItem item, long sequence, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var document in client.ObserveAsync(item.Id, sequence, cancellationToken))
            {
                using (document)
                {
                    var root = document.RootElement;
                    var type = root.GetProperty("type").GetString() ?? "";
                    var data = root.TryGetProperty("data", out var payload) ? payload : default;
                    if (!_messages.Contains(item)) continue;
                    item.Timeline.Apply(root);
                    switch (type)
                    {
                        case "stt.started": item.Input = "Transcribing voice…"; item.Status = "transcribing"; break;
                        case "stt.partial": case "stt.final":
                            item.Input = ReadText(data, "text");
                            if (item.Input.Length > 0) ConversationTitle.Text = item.Input[..Math.Min(48, item.Input.Length)];
                            break;
                        case "response.delta": item.Response += ReadText(data, "text"); item.Status = "responding"; break;
                        case "response.completed": item.Response = ReadText(data, "text"); break;
                        case "tts.started": item.AudioStatus = "Preparing speech…"; break;
                        case "tts.audio":
                            item.HasAudio = true;
                            item.AudioStatus = "Audio ready";
                            if (!item.AutoPlayed && item.AutoPlay && AutoplayResponses.IsChecked == true)
                            {
                                item.AutoPlayed = true;
                                _ = StartPlaybackAsync(item);
                            }
                            break;
                        case "tts.failed": item.AudioStatus = "Speech unavailable"; break;
                        case "interaction.cancelled":
                            if (_playingInteraction == item.Id) _playbackLifetime?.Cancel();
                            item.Status = "cancelled"; break;
                        case "interaction.started": item.Status = "running"; break;
                        case "interaction.completed": item.Status = "completed"; break;
                        case "interaction.failed": item.Status = "failed"; item.Error = ReadText(data, "message"); break;


                    }
                }
                if (item.Status is "completed" or "failed" or "cancelled")
                {
                    MarkConversationActivity();
                    Dispatcher.Invoke(() =>
                    {
                        if (_activeInteraction == item.Id) { _activeInteraction = null; CancelButton.Visibility = Visibility.Collapsed; }
                    });
                }
                RenderMessages();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { Dispatcher.Invoke(() => SetConnection("Reconnecting", "#E4A95C")); _ = exception; }
    }

    private async Task RestoreAudioAsync(AssisterClient client, CancellationToken token)
    {
        foreach (var item in _messages.ToArray())
        {
            try
            {
                if (item.Status is "completed" or "failed" or "cancelled")
                    await client.RestoreTimelineAsync(item.Id, item.Timeline.Apply, token);
                if (await client.HasAudioAsync(item.Id, token) && _messages.Contains(item)) item.HasAudio = true;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception) { /* History text remains usable when audio metadata is unavailable. */ }
        }
        if (!token.IsCancellationRequested && !_closed) RenderMessages();
    }

    private async Task StopPlaybackAsync()
    {
        _playbackLifetime?.Cancel();
        await _playbackTask;
    }

    private async void AutoplayResponses_Unchecked(object sender, RoutedEventArgs e) => await StopPlaybackAsync();

    private async Task StartPlaybackAsync(ChatItem item)
    {
        await _playbackGate.WaitAsync();
        try
        {
            await StopPlaybackAsync();
            if (_closed || _voiceActive || _client is null || !_messages.Contains(item)) return;
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(_connectionLifetime!.Token);
            _playbackLifetime = lifetime;
            _playingInteraction = item.Id;
            _playbackTask = PlayResponseAsync(_client, item, lifetime);
        }
        finally { _playbackGate.Release(); }
        await _playbackTask;
    }

    private async Task PlayResponseAsync(AssisterClient client, ChatItem item, CancellationTokenSource lifetime)
    {
        var playbackId = Guid.NewGuid();
        var finalState = "failed";
        var started = false;
        try
        {
            item.AudioStatus = "Loading audio…";
            RenderMessages();
            _wakeWordLifetime?.Cancel();
            await _wakeWordTask;
            var bytes = await client.DownloadAudioAsync(item.Id, lifetime.Token);
            using (ResponseAudio.Open(bytes)) { }
            await ResponseAudio.PlayAsync(bytes, async () =>
            {
                started = true;
                item.AudioStatus = "Playing…";
                RenderMessages();
                await client.ReportPlaybackAsync(item.Id, playbackId, "started", lifetime.Token);
            }, lifetime.Token);
            finalState = "completed";
            item.AudioStatus = "Audio finished";
            if (_messages.Contains(item)) MarkConversationActivity();
        }
        catch (OperationCanceledException) { finalState = started ? "stopped" : "failed"; item.AudioStatus = "Audio stopped"; }
        catch (Exception exception) { item.AudioStatus = "Could not play audio: " + exception.Message; }
        finally
        {
            try
            {
                // Reports outlive a user Stop, but remain bounded when the server is unavailable.
                using var reportTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await client.ReportPlaybackAsync(item.Id, playbackId, finalState, reportTimeout.Token);
            }
            catch (Exception) { item.AudioStatus += " · Playback report unavailable"; }
            _playingInteraction = null;
            _playbackLifetime = null;
            lifetime.Dispose();
            if (!_closed)
            {
                RenderMessages();
                try { await RestartWakeWordAsync(); }
                catch (Exception) { WakeWordStatus.Text = "Could not resume wake word"; }
            }
        }
    }
    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_activeInteraction is not { } id || _client is null) return;
        CancelButton.IsEnabled = false;
        try { await _client.CancelAsync(id, _connectionLifetime?.Token ?? CancellationToken.None); }
        catch (Exception exception) { ShowError($"Could not cancel the request: {exception.Message}"); }
        finally { CancelButton.IsEnabled = true; }
    }

    private static string ReadText(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private void RenderMessages()
    {
        Dispatcher.Invoke(() =>
        {
            var followLatest = ConversationScroll.ScrollableHeight - ConversationScroll.VerticalOffset < 60;
            MessagesPanel.Children.Clear();
            foreach (var item in _messages)
            {
                AddBubble("YOU", item.Input, user: true);
                if (item.Response.Length > 0) AddBubble("ASSISTER", item.Response, user: false);
                if (item.Timeline.Steps.Count > 0)
                    MessagesPanel.Children.Add(new Controls.ExecutionTimelineView(item.Timeline, item.Status is "completed" or "failed" or "cancelled", item.TraceExpanded, expanded => item.TraceExpanded = expanded));
                if (item.HasAudio)
                {
                    var button = new Button { Content = _playingInteraction == item.Id ? "Stop audio" : "Play response", MinHeight = 44, MinWidth = 140, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(15, 0, 0, 12) };
                    button.Click += async (_, _) => { if (_playingInteraction == item.Id) await StopPlaybackAsync(); else await StartPlaybackAsync(item); };
                    MessagesPanel.Children.Add(button);
                }
                if (item.AudioStatus.Length > 0) MessagesPanel.Children.Add(new TextBlock { Text = item.AudioStatus, Foreground = (Brush)FindResource("Muted"), Margin = new Thickness(15, 0, 0, 12) });
                else if (item.Status is not ("completed" or "failed" or "cancelled")) AddBubble("ASSISTER", "Working on it…", user: false, muted: true);
                if (!string.IsNullOrWhiteSpace(item.Error)) AddBubble("SERVER", item.Error, user: false, muted: true);
            }
            if (followLatest) ConversationScroll.ScrollToEnd();
        });
    }

    private void AddBubble(string label, string text, bool user, bool muted = false)
    {
        var stack = new StackPanel { MaxWidth = 1000, HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 20) };
        stack.Children.Add(new TextBlock { Text = label, Foreground = (Brush)FindResource("Muted"), FontSize = 14, FontWeight = FontWeights.Bold, Margin = new Thickness(5, 0, 5, 6) });
        stack.Children.Add(new Border
        {
            Background = user ? (Brush)FindResource("PanelRaised") : Brushes.Transparent,
            BorderBrush = user ? (Brush)FindResource("PanelRaised") : Brushes.Transparent,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(15, 11, 15, 11),
            Child = new TextBlock { Text = text, Foreground = muted ? (Brush)FindResource("Muted") : Brushes.White, FontSize = 22, TextWrapping = TextWrapping.Wrap, LineHeight = 33 }
        });
        MessagesPanel.Children.Add(stack);
    }

    private void AddWelcome(string? detail = null)
    {
        if (_messages.Count > 0) return;
        MessagesPanel.Children.Clear();
        var welcome = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 100, 0, 70), HorizontalAlignment = HorizontalAlignment.Center, MaxWidth = 510 };
        welcome.Children.Add(new TextBlock { Text = "A clearer view of what\nAssister is doing.", FontSize = 34, FontWeight = FontWeights.SemiBold, TextAlignment = TextAlignment.Center, LineHeight = 42 });
        welcome.Children.Add(new TextBlock { Text = detail ?? "Connect to your server, ask a question, and follow the answer and execution as they happen.", Foreground = (Brush)FindResource("Muted"), FontSize = 20, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 17, 20, 0), LineHeight = 33 });
        MessagesPanel.Children.Add(welcome);
    }

    private void ShowError(string message)
    {
        ProtocolLabel.Text = message;
        ProtocolLabel.Foreground = new SolidColorBrush(Color.FromRgb(240, 165, 165));
    }

    private void SetConnection(string text, string color)
    {
        ConnectionLabel.Text = text;
        ConnectionDot.Fill = (Brush)new BrushConverter().ConvertFromString(color)!;
    }

    private sealed class ChatItem(Guid id, string input, string response, string status, long sequence)
    {
        public ExecutionTimeline Timeline { get; } = new();
        public bool TraceExpanded { get; set; }
        public Guid Id { get; } = id;
        public string Input { get; set; } = input;
        public string Response { get; set; } = response;
        public string Status { get; set; } = status;
        public long Sequence { get; set; } = sequence;
        public bool HasAudio { get; set; }
        public bool AutoPlay { get; set; } = true;
        public bool AutoPlayed { get; set; }
        public string AudioStatus { get; set; } = "";
        public string? Error { get; set; }
    }
}
