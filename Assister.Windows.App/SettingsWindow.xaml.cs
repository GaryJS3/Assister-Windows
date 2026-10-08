using System.Windows;
using Assister.Windows.App.Services;

namespace Assister.Windows.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private CancellationTokenSource? _setupLifetime;
    private bool _closed;
    private readonly Func<CancellationToken, Task<string>> _checkForUpdates;
    private CancellationTokenSource? _updateCheckLifetime;
    internal SettingsWindow(AppSettings settings, Func<CancellationToken, Task<string>> checkForUpdates)
    {
        InitializeComponent();
        _settings = settings;
        _checkForUpdates = checkForUpdates;
        UpdateVersionLabel.Text = $"Installed version {AutoUpdater.CurrentVersion}";
        EndpointInput.Text = settings.Endpoint;
        WakeWordEnabledInput.IsChecked = settings.WakeWordEnabled;
        WakeWordModelInput.Text = string.IsNullOrWhiteSpace(settings.WakeWordModelDirectory) ? WakeWordSetup.ModelDirectory : settings.WakeWordModelDirectory;
        WakeWordKeywordsInput.Text = string.IsNullOrWhiteSpace(settings.WakeWordKeywordsFile) ? WakeWordSetup.KeywordsFile : settings.WakeWordKeywordsFile;
        WakeWordSetupStatus.Text = WakeWordSetup.IsInstalled() ? "Engine installed · ready to enable" : "One-time download from sherpa-onnx. No scripts or file selection needed.";
        Closed += (_, _) => { _closed = true; _setupLifetime?.Cancel(); _updateCheckLifetime?.Cancel(); };
        TokenInput.Password = CredentialStore.Read() ?? CredentialStore.ReadDevelopmentToken() ?? string.Empty;
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        using var lifetime = new CancellationTokenSource();
        _updateCheckLifetime = lifetime;
        CheckForUpdatesButton.IsEnabled = false;
        UpdateStatusLabel.Text = "Checking for updates and downloading any available release…";
        try
        {
            var status = await _checkForUpdates(lifetime.Token);
            if (!_closed) UpdateStatusLabel.Text = status;
        }
        catch (OperationCanceledException) { if (!_closed) UpdateStatusLabel.Text = "Update check cancelled."; }
        catch (Exception exception) { if (!_closed) UpdateStatusLabel.Text = $"Update check failed: {exception.Message}"; }
        finally
        {
            _updateCheckLifetime = null;
            if (!_closed) CheckForUpdatesButton.IsEnabled = true;
        }
    }

    private async void SetupWakeWord_Click(object sender, RoutedEventArgs e)
    {
        using var lifetime = new CancellationTokenSource();
        _setupLifetime = lifetime;
        SetupWakeWordButton.IsEnabled = false;
        SaveSettingsButton.IsEnabled = false;
        CancelSetupButton.Visibility = Visibility.Visible;
        WakeWordSetupStatus.Text = "Starting download…";
        ErrorLabel.Text = "";
        try
        {
            var progress = new Progress<string>(status => { if (!_closed) WakeWordSetupStatus.Text = status; });
            await WakeWordSetup.InstallAsync(progress, lifetime.Token);
            if (_closed) return;
            WakeWordModelInput.Text = WakeWordSetup.ModelDirectory;
            WakeWordKeywordsInput.Text = WakeWordSetup.KeywordsFile;
            WakeWordEnabledInput.IsChecked = true;
            WakeWordSetupStatus.Text = "Ready · select Save and connect, then say “light up”.";
        }
        catch (OperationCanceledException) { if (!_closed) WakeWordSetupStatus.Text = "Setup stopped. You can retry anytime."; }
        catch (Exception exception) { if (!_closed) WakeWordSetupStatus.Text = $"Setup failed: {exception.Message} Select setup to retry."; }
        finally
        {
            _setupLifetime = null;
            if (!_closed)
            {
                SetupWakeWordButton.IsEnabled = true;
                SaveSettingsButton.IsEnabled = true;
                CancelSetupButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void CancelSetup_Click(object sender, RoutedEventArgs e) => _setupLifetime?.Cancel();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var wakeWordEnabled = WakeWordEnabledInput.IsChecked == true;
            if (wakeWordEnabled)
            {
                if (WakeWordModelInput.Text == WakeWordSetup.ModelDirectory && !WakeWordSetup.IsInstalled())
                    throw new InvalidOperationException("Select Set up wake word engine before enabling listening.");
                WakeWordDetector.CreateConfig(WakeWordModelInput.Text.Trim(), WakeWordKeywordsInput.Text.Trim());
            }
            var token = AssisterClient.NormalizeToken(TokenInput.Password);
            if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("Enter the client bearer token.");
            using var client = new AssisterClient(EndpointInput.Text, token);
            CredentialStore.Save(token);
            var endpoint = EndpointInput.Text.Trim().TrimEnd('/') + "/";
            if (!string.Equals(_settings.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase)) _settings.ConversationId = null;
            _settings.Endpoint = endpoint;
            _settings.WakeWordEnabled = wakeWordEnabled;
            _settings.WakeWordModelDirectory = WakeWordModelInput.Text.Trim();
            _settings.WakeWordKeywordsFile = WakeWordKeywordsInput.Text.Trim();
            _settings.Save();
            DialogResult = true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            ErrorLabel.Text = exception.Message;
        }
    }
}
