using System.Diagnostics;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace MemReduct.Fluent;

public sealed partial class MainWindow
{
    private bool _metricsPreviewBusy;
    private string? _metricsPreviewDirectory;

    private async Task CheckMetricsAnimationAsync()
    {
        var original = _latestMetrics;
        var realPercent = _lastPercent;
        var latest = new MemorySnapshot(100, 38, 90, 100, 128UL * 1024 * 1024, 1024UL * 1024 * 1024);
        UpdateStats(new MemorySnapshot(100, 25, 80, 100, 256UL * 1024 * 1024, latest.CachePeak));
        BeginMetricsCleanup();
        UpdateStats(latest);
        if (CanAnimateMetrics)
        {
            await App.WaitUntilAsync(() => !_metricsToZero, TimeSpan.FromSeconds(3));
            Require(PhysicalValue.Text == $"{0:0.0}%" && CommitValue.Text == $"{0:0.0}%"
                && PhysicalBar.Value == 0,
                "Cleanup numbers and the usage bar reach zero together");
        }
        EndMetricsCleanup();
        await App.WaitUntilAsync(() => !_metricsToZero && !_metricsReturning, TimeSpan.FromSeconds(3));
        Require(PhysicalValue.Text == $"{62:0.0}%" && CommitValue.Text == $"{90:0.0}%"
            && CacheValue.Text == "128 MB" && PhysicalBar.Value == 62
            && !_metricsReturning && _lastPercent == realPercent,
            "Animation restores the newest real sample without changing the scheduling/tray reading");
        PhysicalBar.UpdateLayout();
        var indicator = Descendants(PhysicalBar).OfType<FrameworkElement>()
            .FirstOrDefault(element => element.Name == "DeterminateProgressBarIndicator");
        Require(indicator is not null && Math.Abs(indicator.ActualWidth - PhysicalBar.ActualWidth * 0.62) <= 2,
            "The rendered usage bar, not only its Value, follows the restored percentage");

        BeginMetricsCleanup(); EndMetricsCleanup();
        await App.WaitUntilAsync(() => !_metricsToZero && !_metricsReturning, TimeSpan.FromSeconds(3));
        Require(!_metricsToZero && !_metricsReturning && PhysicalBar.Value == 62,
            "A fast cleanup completes both animation phases without a stuck timer");
        BeginMetricsCleanup();
        UpdateStats(latest with { CacheCurrent = null });
        SetSamplingState(true);
        Require(PhysicalBar.Value == 62 && CacheValue.Text == "—" && _metricsTimer?.IsRunning != true,
            "Sampling failure cancels the visual zero and preserves missing-cache semantics");
        EndMetricsCleanup(); SetSamplingState(false);
        if (original is not null) UpdateStats(original);
    }

    // Isolated visible preview: no preferences, tray, authorization or native cleanup.
    internal async void RunAnimationPreview(string directory)
    {
        _metricsPreviewDirectory = directory;
        Directory.CreateDirectory(directory);
        Title = "Mem Reduct 界面预览";
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(DefaultWindowWidth * scale), (int)(DefaultWindowHeight * scale)));
        AppWindow.Move(new PointInt32(100, 20));
        Shell.SizeChanged += (_, _) => ReflowMetrics(Shell.ActualWidth, _uiSettings.TextScaleFactor);
        ReflowMetrics(DefaultWindowWidth, _uiSettings.TextScaleFactor);
        UpdateStats(MemoryEngine.Read());
        CleanButton.Content = UiText.Get("CleanMemory");
        Activate();
        await Task.Delay(500);
        await WritePreview(directory, "memory-layout.png");
        SelectPage(true);
        await WritePreview(directory, "settings-layout.png");
        WritePreviewBounds(directory, "settings-bounds.json");
        SettingsPage.AutoSettingsCard.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
        await WritePreview(directory, "settings-inputs.png");
        SettingsPage.AboutCard.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
        await WritePreview(directory, "settings-about.png");
        await WritePreview(directory, "settings-about-card.png", SettingsPage.AboutSection);
        BodyScroll.ChangeView(0, 0, null, true);
        AppWindow.Resize(new SizeInt32((int)(1280 * scale), (int)(520 * scale)));
        await WritePreview(directory, "settings-wide.png");
        WritePreviewBounds(directory, "settings-wide-bounds.json");
        AppWindow.Resize(new SizeInt32((int)(DefaultWindowWidth * scale), (int)(DefaultWindowHeight * scale)));
        SelectPage(false);
        await PreviewMetricsCleanupAsync();
        CleanupHistoryExpander.IsExpanded = ProcessesExpander.IsExpanded = true;
        await WritePreview(directory, "recent-cleanups-expanded.png", CleanupHistoryExpander);
        await WritePreview(directory, "processes-expanded.png", ProcessesExpander);
        await WritePreview(directory, "memory-expanded.png");
        CleanupHistoryExpander.IsExpanded = ProcessesExpander.IsExpanded = false;
    }

    private void WritePreviewBounds(string directory, string filename)
    {
        var elements = new FrameworkElement[] { Shell, AppTitleBar, BrandHeader, OverviewTab, SettingsTab, AppSurface, ContentLayout, BodyScroll, PageContent,
            SettingsHost, SettingsPage, SettingsPage.AppearanceCard, SettingsPage.LanguageChoice };
        File.WriteAllText(Path.Combine(directory, filename), System.Text.Json.JsonSerializer.Serialize(new
        {
            BodyScroll.ViewportWidth, BodyScroll.ExtentWidth, BodyScroll.HorizontalOffset,
            nativeCaptionHeight = AppWindow.TitleBar.Height / Shell.XamlRoot.RasterizationScale,
            elements = elements.Select(element => new
            {
                element.Name, element.ActualWidth, element.ActualHeight, element.Width, element.MinWidth, element.MaxWidth,
                desiredWidth = element.DesiredSize.Width,
                x = element.TransformToVisual(Shell).TransformPoint(new Windows.Foundation.Point()).X,
                y = element.TransformToVisual(Shell).TransformPoint(new Windows.Foundation.Point()).Y,
                alignment = element.HorizontalAlignment.ToString()
            })
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));
    }

    private async Task PreviewMetricsCleanupAsync()
    {
        if (_metricsPreviewBusy || _exitRequested) return;
        _metricsPreviewBusy = true;
        CleanButton.IsEnabled = false;
        CleanButton.Content = UiText.Get("Cleaning");
        try
        {
            var capture = CaptureMetricsFramesAsync();
            BeginMetricsCleanup();
            await Task.Delay(700);
            UpdateStats(MemoryEngine.Read());
            EndMetricsCleanup();
            await capture;
        }
        finally
        {
            EndMetricsCleanup();
            CleanButton.Content = UiText.Get("CleanMemory");
            CleanButton.IsEnabled = true;
            _metricsPreviewBusy = false;
        }
    }

    private async Task CaptureMetricsFramesAsync()
    {
        if (_metricsPreviewDirectory is null) return;
        var elapsed = Stopwatch.StartNew();
        for (var index = 0; index < 26 && !_exitRequested; index++)
        {
            await WritePreview(_metricsPreviewDirectory, $"frame-{index:00}.png", MetricsCard, waitMilliseconds: 0);
            await Task.Delay((int)Math.Max(0, (index + 1) * 100 - elapsed.ElapsedMilliseconds));
        }
    }
}
