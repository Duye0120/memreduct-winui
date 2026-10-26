using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MemReduct.Fluent;

public sealed partial class MainWindow
{
    private bool _updateBusy;
    private string? _downloadedInstaller;
    private CancellationTokenSource? _updateCancellation;
    private bool _startupUpdateChecked;
    private string? _startupUpdateStatus;
    private CancellationTokenSource? _startupUpdateCancellation;

    private async Task CheckStartupUpdateAsync(Func<CancellationToken, Task<AppRelease?>> check)
    {
        if (_exitRequested || _startupUpdateChecked || !_settings.CheckUpdatesOnStartup) return;
        _startupUpdateChecked = true;
        if (_updateBusy) return;
        using var cancellation = new CancellationTokenSource();
        _startupUpdateCancellation = cancellation;
        try
        {
            var release = await check(cancellation.Token);
            if (_exitRequested || cancellation.IsCancellationRequested || !_settings.CheckUpdatesOnStartup || _updateBusy) return;
            _startupUpdateStatus = release is null ? UiText.Get("UpdateNone") : UiText.Get("UpdateAvailable", release.Version);
            if (SettingsPage is not null) SettingsPage.UpdateStatus.Text = _startupUpdateStatus;
            if (release is not null)
            {
                UpdateNotice.Title = _startupUpdateStatus;
                UpdateNotice.Visibility = Visibility.Visible;
                UpdateNotice.IsOpen = true;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            // Startup checks stay quiet offline; the manual update action retains detailed errors.
            if (_exitRequested || cancellation.IsCancellationRequested || _updateBusy) return;
            _startupUpdateStatus = UiText.Get("UpdateFailed");
            if (SettingsPage is not null) SettingsPage.UpdateStatus.Text = _startupUpdateStatus;
        }
        finally { _startupUpdateCancellation = null; }
    }

    private void UpdateDetails_Click(object sender, RoutedEventArgs args)
    {
        if (_exitRequested) return;
        UpdateNotice.IsOpen = false;
        UpdateNotice.Visibility = Visibility.Collapsed;
        SelectPage(true);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_exitRequested && SettingsHost.Visibility == Visibility.Visible)
                SettingsPage.UpdateButton.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        });
    }

    private async void Update_Click(object sender, RoutedEventArgs args)
    {
        if (_updateCancellation is not null)
        {
            _updateCancellation.Cancel();
            SettingsPage.UpdateButton.IsEnabled = false;
            return;
        }
        await CheckUpdateAsync();
    }

    private async Task CheckUpdateAsync()
    {
        if (_exitRequested || _checking || _cleaning || _restartPromptOpen || _elevating || _updateBusy) return;
        _startupUpdateChecked = true;
        _startupUpdateCancellation?.Cancel();
        _updateBusy = true;
        _updateCancellation = new CancellationTokenSource();
        SettingsPage.UpdateButton.IsEnabled = false;
        SettingsPage.UpdateProgress.Visibility = Visibility.Collapsed;
        try
        {
            if (_downloadedInstaller is not null)
            {
                _restartPromptOpen = true;
                RefreshOptions();
                if (!SaveSettings()) return;
                SettingsPage.UpdateStatus.Text = UiText.Get("UpdateInstalling");
                await UpdateService.RequestInstallAsync(_downloadedInstaller);
                if (_exitRequested) return;
                ExitWindow();
                return;
            }
            SettingsPage.UpdateStatus.Text = UiText.Get("UpdateChecking");
            var release = await UpdateService.CheckAsync(_updateCancellation.Token);
            if (_exitRequested) return;
            if (release is null) { SettingsPage.UpdateStatus.Text = UiText.Get("UpdateNone"); return; }
            SettingsPage.UpdateStatus.Text = UiText.Get("UpdateAvailable", release.Version);
            if (!Installation.IsInstalled)
            {
                SettingsPage.UpdateStatus.Text += " " + UiText.Get("UpdatePortable");
                return;
            }
            _restartPromptOpen = true;
            RefreshOptions();
            try
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = Shell.XamlRoot, RequestedTheme = Shell.ActualTheme,
                    Title = UiText.Get("UpdateAvailable", release.Version), Content = UiText.Get("UpdateConfirm"),
                    PrimaryButtonText = UiText.Get("UpdateDownload"), CloseButtonText = UiText.Get("Later"),
                    DefaultButton = ContentDialogButton.Close, CornerRadius = new(10 / Shell.XamlRoot.RasterizationScale)
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            }
            finally { _restartPromptOpen = false; RefreshOptions(); }
            if (_exitRequested) return;
            SettingsPage.UpdateStatus.Text = UiText.Get("UpdateDownloading");
            SettingsPage.UpdateProgress.Value = 0;
            SettingsPage.UpdateProgress.Visibility = Visibility.Visible;
            SettingsPage.UpdateButton.Content = UiText.Get("UpdateCancelDownload");
            SettingsPage.UpdateButton.IsEnabled = true;
            var progress = new Progress<long>(received =>
            {
                if (_exitRequested || _updateCancellation is null || _updateCancellation.IsCancellationRequested) return;
                SettingsPage.UpdateProgress.Value = received * 100.0 / release.Size;
                SettingsPage.UpdateStatus.Text = UiText.Get("UpdateDownloadProgress", SettingsPage.UpdateProgress.Value,
                    MemorySnapshot.Bytes((ulong)received), MemorySnapshot.Bytes((ulong)release.Size));
            });
            _downloadedInstaller = await UpdateService.DownloadAsync(release, progress, _updateCancellation.Token);
            if (_exitRequested) return;
            SettingsPage.UpdateProgress.Value = 100;
            SettingsPage.UpdateButton.Content = UiText.Get("UpdateInstall");
            SettingsPage.UpdateStatus.Text = UiText.Get("UpdateReady");
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { if (!_exitRequested) SettingsPage.UpdateStatus.Text = UiText.Get("UpdateCanceled"); }
        catch (OperationCanceledException) when (_updateCancellation?.IsCancellationRequested == true)
        {
            if (_exitRequested) return;
            SettingsPage.UpdateStatus.Text = UiText.Get("UpdateDownloadCanceled");
            SettingsPage.UpdateButton.Content = UiText.Get("UpdateButton.Content");
        }
        catch (Exception error)
        {
            if (_exitRequested) return;
            _downloadedInstaller = null;
            SettingsPage.UpdateButton.Content = UiText.Get("UpdateButton.Content");
            SettingsPage.UpdateStatus.Text = UiText.Get("UpdateFailed");
            ReportIssue(SettingsPage.UpdateStatus.Text, error.Message);
        }
        finally
        {
            _updateCancellation?.Dispose();
            _updateCancellation = null;
            _updateBusy = false;
            _restartPromptOpen = false;
            if (!_exitRequested) { SettingsPage.UpdateProgress.Visibility = Visibility.Collapsed; SettingsPage.UpdateButton.IsEnabled = true; RefreshOptions(); }
        }
    }
}
