using System.Text.Json;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace MemReduct.Fluent;

public sealed partial class MainWindow
{
    private async Task CheckUpdateNoticeAsync(string directory)
    {
        var previousCheck = _startupUpdateChecked;
        var previousStatus = _startupUpdateStatus;
        var previousText = SettingsPage.UpdateStatus.Text;
        try
        {
            _startupUpdateChecked = false;
            await CheckStartupUpdateAsync(_ => Task.FromResult<AppRelease?>(new AppRelease(
                new Version(99, 0, 0), new Uri("https://example.invalid/fixture.exe"), new string('0', 64), 100)));
            await WritePreview(directory, "startup-update-notice-fixture.png");
            UpdateDetails_Click(this, new RoutedEventArgs());
            Require(await App.WaitUntilAsync(() =>
            {
                var top = SettingsPage.UpdateButton.TransformToVisual(Shell).TransformPoint(new Point()).Y;
                return SettingsHost.Visibility == Visibility.Visible && !UpdateNotice.IsOpen
                    && top >= 0 && top + SettingsPage.UpdateButton.ActualHeight <= Shell.ActualHeight;
            }, TimeSpan.FromSeconds(3)), "The startup notice opens settings and scrolls the update action into view");
            await WritePreview(directory, "startup-update-details-fixture.png");
        }
        finally
        {
            _startupUpdateChecked = previousCheck;
            _startupUpdateStatus = previousStatus;
            SettingsPage.UpdateStatus.Text = previousText;
            UpdateNotice.IsOpen = false;
            UpdateNotice.Visibility = Visibility.Collapsed;
            SelectPage(false);
        }
    }

    private async Task CheckStartupUpdatesAsync()
    {
        Require(JsonSerializer.Deserialize<AppSettings>("{}")!.CheckUpdatesOnStartup,
            "Existing profiles without the new preference default to checking on startup");
        var profile = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(
            new AppSettings { CheckUpdatesOnStartup = false, Threshold = 76, CleanSystemCache = false }))!;
        Require(!profile.CheckUpdatesOnStartup && profile.Threshold == 76 && !profile.CleanSystemCache,
            "Disabling startup checks round-trips alongside existing cleanup preferences");
        var enabled = _settings.CheckUpdatesOnStartup;
        var ready = _ready;
        var issue = IssueBanner.Title;
        var release = new AppRelease(new Version(99, 0, 0), new Uri("https://example.invalid/fixture.exe"), new string('0', 64), 100);
        var requests = 0;
        Task<AppRelease?> NoUpdate(CancellationToken _) { requests++; return Task.FromResult<AppRelease?>(null); }
        try
        {
            _settings.CheckUpdatesOnStartup = false;
            await CheckStartupUpdateAsync(NoUpdate);
            Require(requests == 0 && !_startupUpdateChecked, "Disabled startup checks do not request release metadata");
            _settings.CheckUpdatesOnStartup = true;
            var response = new TaskCompletionSource<AppRelease?>();
            var pending = CheckStartupUpdateAsync(_ => { requests++; return response.Task; });
            await CheckStartupUpdateAsync(NoUpdate);
            Require(requests == 1 && !pending.IsCompleted && SettingsPage is null && !_restartPromptOpen && !_updateBusy,
                "One pending startup request leaves the settings tree unloaded and does not block cleanup or launch");
            response.SetResult(release);
            await pending;
            Require(UpdateNotice.IsOpen && UpdateNotice.Title == UiText.Get("UpdateAvailable", release.Version)
                && SettingsPage is null && _downloadedInstaller is null && !_updateBusy,
                "A newer release produces a nonmodal notice without downloading or installing");
            UpdateNotice.IsOpen = false;
            UpdateNotice.Visibility = Visibility.Collapsed;
            _startupUpdateChecked = false;
            await CheckStartupUpdateAsync(NoUpdate);
            Require(_startupUpdateStatus == UiText.Get("UpdateNone") && !UpdateNotice.IsOpen && SettingsPage is null,
                "An up-to-date launch stays quiet and retains its result for deferred settings");
            _startupUpdateChecked = false;
            await CheckStartupUpdateAsync(_ => Task.FromException<AppRelease?>(new InvalidOperationException("Offline fixture")));
            Require(_startupUpdateStatus == UiText.Get("UpdateFailed") && !UpdateNotice.IsOpen && IssueBanner.Title == issue
                && _startupUpdateCancellation is null && !_updateBusy,
                "Network failure neither replaces an existing issue nor leaves manual updates locked");
            _startupUpdateChecked = false;
            response = new TaskCompletionSource<AppRelease?>();
            pending = CheckStartupUpdateAsync(_ => response.Task);
            _settings.CheckUpdatesOnStartup = false;
            response.SetResult(release);
            await pending;
            Require(!UpdateNotice.IsOpen, "Disabling checks suppresses an already-pending release response");
            _settings.CheckUpdatesOnStartup = true;
            _startupUpdateChecked = false;
            response = new TaskCompletionSource<AppRelease?>();
            pending = CheckStartupUpdateAsync(token => response.Task.WaitAsync(token));
            var cancellation = _startupUpdateCancellation!;
            PrepareToExit();
            await pending;
            Require(cancellation.IsCancellationRequested && !UpdateNotice.IsOpen && _startupUpdateCancellation is null,
                "Exiting cancels the startup request and discards late UI updates");
            _exitRequested = false;
            _ready = ready;
            _startupUpdateChecked = false;
            _updateBusy = true;
            var previousRequests = requests;
            await CheckStartupUpdateAsync(NoUpdate);
            Require(requests == previousRequests, "An active manual update takes precedence over the queued startup check");
        }
        finally
        {
            _settings.CheckUpdatesOnStartup = enabled;
            _exitRequested = false;
            _ready = ready;
            _updateBusy = false;
            _startupUpdateChecked = false;
            _startupUpdateStatus = null;
            UpdateNotice.IsOpen = false;
            UpdateNotice.Visibility = Visibility.Collapsed;
        }
    }
}
