using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace MemReduct.Fluent;

public sealed partial class MainWindow : Window
{
    private const int DefaultWindowWidth = 560, DefaultWindowHeight = 800;
    private readonly AppSettings _settings;
    private readonly bool _checking;
    private readonly DispatcherQueueTimer? _timer;
    private readonly DispatcherQueueTimer? _animationTimer;
    private int _animationFrame;
    private readonly TrayIcon? _tray;
    private bool _ready, _cleaning, _exitRequested, _sampleFailed, _restartPromptOpen, _elevating;
    private bool _startupCleanupPending;
    private bool _startupEnabled;
    private bool _helperReady, _helperProbeBusy;
    private DateTimeOffset _nextHelperProbe;
    private bool CanCleanMemory => Installation.IsInstalled ? _helperReady : MemoryEngine.IsAdministrator;
    private SettingsView SettingsPage { get; set; } = null!;
    private bool _startupReadable = true;
    private bool _statsStacked;
    private double _cornerScale;
    private DateTimeOffset _lastAttempt;
    private DateTimeOffset? _lastReadUtc;
    private DateTimeOffset _thresholdRetryAfter;
    private bool _gameActive;
    private double _lastPercent;
    private CleanupFeedback _feedback;
    private long _feedbackUntil;
    private string _feedbackText = "";
    private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();
    private readonly Windows.UI.ViewManagement.AccessibilitySettings _accessibility = new();
    internal bool CanStayInTray => _tray?.IsVisible == true;
    private bool CanRenderWindow => !_exitRequested && AppWindow.IsVisible
        && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };
    private bool CanStartAutomaticCleanup => !_exitRequested && !_cleaning && !_restartPromptOpen && !_elevating && !_helperProbeBusy && !_startupCleanupPending && !_sampleFailed;

    public MainWindow(AppSettings settings, bool checking)
    {
        _settings = settings;
        _checking = checking;
        _startupCleanupPending = !checking && App.Arguments.Contains("--clean");
        _lastAttempt = settings.LastCleanUtc is { } last && last <= DateTimeOffset.UtcNow ? last : DateTimeOffset.UtcNow;
        InitializeComponent();
        if (checking) Title = "Mem Reduct Fluent - Self-check";
        Shell.Language = UiText.LanguageTag;
        IssueBanner.Closed += (_, _) => IssueBanner.Visibility = Visibility.Collapsed;
        UpdateNotice.Closed += (_, _) => UpdateNotice.Visibility = Visibility.Collapsed;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        var handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(handle) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(DefaultWindowWidth * scale), (int)(DefaultWindowHeight * scale)));
        UpdatePresenterConstraints(scale);
        UpdateCornerRadii();
        ResultText.Text = _settings.LastCleanUtc is { } lastClean
            ? UiText.Get("LastClean", lastClean.ToLocalTime()) : UiText.Get("NeverCleaned");
        LastCleanupTime.Text = ResultText.Text;
        ApplyTheme();
        SelectPage(false);
        if (!checking)
        {
            Shell.SizeChanged += (_, _) => ReflowMetrics(Shell.ActualWidth, _uiSettings.TextScaleFactor);
            _uiSettings.TextScaleFactorChanged += (_, _) => DispatcherQueue.TryEnqueue(() => ReflowMetrics(Shell.ActualWidth, _uiSettings.TextScaleFactor));
            try
            {
                _tray = new TrayIcon(handle, ShowWindow, ExitWindow, CleanFromTray,
                    () => SetGameMode(!_settings.GameMode), () => _settings.GameMode,
                    () => SaveCleanupConfirmation(!_settings.ConfirmCleanup), () => _settings.ConfirmCleanup);
                if (!_tray.IsVisible) ReportIssue(UiText.Get("TrayUnavailable"), UiText.Get("ExitOnClose"));
            }
            catch (Exception error) { ReportIssue(UiText.Get("TrayUnavailable"), error.Message + " " + UiText.Get("ExitOnClose")); }
            try { _startupEnabled = StartupRegistration.IsEnabled(); }
            catch (Exception error) { ReportIssue(UiText.Get("StartupReadFailed"), error.Message); _startupReadable = false; }
            _timer = DispatcherQueue.CreateTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += (_, _) => Sample();
            _timer.Start();
            _animationTimer = DispatcherQueue.CreateTimer();
            _animationTimer.Interval = TimeSpan.FromMilliseconds(100);
            _animationTimer.Tick += (_, _) =>
            {
                _animationFrame = (_animationFrame + 1) % 12;
                UpdateTraySafely();
            };
        }
        _ready = true;
        RefreshOptions();
        if (!checking) Sample();
        AppWindow.Closing += (_, args) =>
        {
            if (!_exitRequested && !_checking && _settings.CloseToTray && _tray?.IsVisible == true)
            { args.Cancel = true; AppWindow.Hide(); }
            else PrepareToExit();
        };
        Closed += (_, _) => { PrepareToExit(); StopMetricsAnimation(); _timer?.Stop(); _animationTimer?.Stop(); _tray?.Dispose(); ((App)Application.Current).Finish(); };
        Shell.Loaded += async (_, _) =>
        {
            Title = _checking ? "Mem Reduct 界面预览" : "Mem Reduct";
            if (!_checking && Installation.IsInstalled) await RefreshHelperAsync(startTask: true);
            if (_exitRequested) return;
            UpdateCornerRadii();
            Shell.XamlRoot.Changed -= XamlRoot_Changed;
            Shell.XamlRoot.Changed += XamlRoot_Changed;
            await RunStartupCleanupAsync();
        };
        if (!checking) DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
            () => _ = CheckStartupUpdateAsync(UpdateService.CheckAsync));
    }

    private void EnsureSettingsLoaded()
    {
        if (SettingsPage is not null) return;
        var ready = _ready;
        _ready = false;
        try
        {
            SettingsPage = new SettingsView();
            SettingsHost.Content = SettingsPage;
            SettingsPage.LanguageChoice.SelectionChanged += LanguageChoice_SelectionChanged;
            SettingsPage.ThemeChoice.SelectionChanged += ThemeChoice_SelectionChanged;
            SettingsPage.PinSwitch.Toggled += Preference_Toggled;
            SettingsPage.TraySwitch.Toggled += Preference_Toggled;
            SettingsPage.StartupSwitch.Toggled += StartupSwitch_Toggled;
            SettingsPage.WorkingSetCheck.Click += CleanupOption_Click;
            SettingsPage.SystemCacheCheck.Click += CleanupOption_Click;
            SettingsPage.StandbyCheck.Click += CleanupOption_Click;
            SettingsPage.ModifiedFileCheck.Click += CleanupOption_Click;
            SettingsPage.ModifiedPagesCheck.Click += CleanupOption_Click;
            SettingsPage.FullStandbyCheck.Click += CleanupOption_Click;
            SettingsPage.RegistryCheck.Click += CleanupOption_Click;
            SettingsPage.CombineCheck.Click += CleanupOption_Click;
            SettingsPage.ConfirmSwitch.Toggled += ConfirmSwitch_Toggled;
            SettingsPage.NotifyCleanupSwitch.Toggled += Preference_Toggled;
            SettingsPage.GameModeSwitch.Toggled += GameModeSwitch_Toggled;
            SettingsPage.ThresholdNumber.ValueChanged += AutoNumber_ValueChanged;
            SettingsPage.ThresholdNumber.LostFocus += AutoNumber_LostFocus;
            SettingsPage.ThresholdSwitch.Toggled += Preference_Toggled;
            SettingsPage.IntervalNumber.ValueChanged += AutoNumber_ValueChanged;
            SettingsPage.IntervalNumber.LostFocus += AutoNumber_LostFocus;
            SettingsPage.IntervalSwitch.Toggled += Preference_Toggled;
            SettingsPage.UpdateButton.Click += Update_Click;
            SettingsPage.AutoUpdateSwitch.Toggled += Preference_Toggled;
            SettingsPage.ExitButton.Click += Exit_Click;
            LoadSettings();
            _cornerScale = 0;
            UpdateCornerRadii();
            ReflowMetrics(Shell.ActualWidth, _uiSettings.TextScaleFactor);
        }
        finally { _ready = ready; }
        RefreshOptions();
    }

    private void LoadSettings()
    {
        SettingsPage.VersionText.Text = "Mem Reduct  " + UpdateService.DisplayVersion;
        SettingsPage.StartupSwitch.IsOn = _startupEnabled;
        SettingsPage.StartupSwitch.IsEnabled = _startupReadable;
        SettingsPage.ThemeChoice.SelectedIndex = _settings.Theme == "Light" ? 1 : _settings.Theme == "Dark" ? 2 : 0;
        SettingsPage.LanguageChoice.SelectedIndex = _settings.Language == "zh-CN" ? 1 : _settings.Language == "en-US" ? 2 : 0;
        SettingsPage.PinSwitch.IsOn = _settings.AlwaysOnTop;
        SettingsPage.TraySwitch.IsOn = _settings.CloseToTray;
        SettingsPage.ConfirmSwitch.IsOn = _settings.ConfirmCleanup;
        SettingsPage.NotifyCleanupSwitch.IsOn = _settings.NotifyAfterCleanup;
        SettingsPage.AutoUpdateSwitch.IsOn = _settings.CheckUpdatesOnStartup;
        if (_startupUpdateStatus is not null) SettingsPage.UpdateStatus.Text = _startupUpdateStatus;
        if (_downloadedRelease is not null) SetReadyUpdate(_downloadedRelease);
        SettingsPage.WorkingSetCheck.IsChecked = _settings.CleanWorkingSets;
        SettingsPage.SystemCacheCheck.IsChecked = _settings.CleanSystemCache;
        SettingsPage.StandbyCheck.IsChecked = _settings.CleanLowPriorityStandby;
        SettingsPage.ModifiedFileCheck.IsChecked = _settings.CleanModifiedFileCache;
        SettingsPage.ModifiedPagesCheck.IsChecked = _settings.CleanModifiedPages;
        SettingsPage.FullStandbyCheck.IsChecked = _settings.CleanStandby;
        SettingsPage.RegistryCheck.IsChecked = _settings.CleanRegistryCache;
        SettingsPage.CombineCheck.IsChecked = _settings.CombineMemoryPages;
        SettingsPage.AdvancedCleanup.IsExpanded = AdvancedCleanupCount() > 0;
        SettingsPage.ThresholdSwitch.IsOn = _settings.AutoThreshold;
        SettingsPage.IntervalSwitch.IsOn = _settings.AutoInterval;
        SettingsPage.GameModeSwitch.IsOn = _settings.GameMode;
        SettingsPage.ThresholdNumber.Value = _settings.Threshold;
        SettingsPage.IntervalNumber.Value = _settings.IntervalMinutes;
    }

    private bool SaveSettings()
    {
        if (_checking) return true;
        try { SettingsStore.Save(_settings); return true; }
        catch (Exception error) { ReportIssue(UiText.Get("SaveFailed"), error.GetBaseException().Message); return false; }
    }

    private void ApplyTheme()
    {
        Shell.RequestedTheme = _settings.Theme switch
        { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        AppWindow.TitleBar.PreferredTheme = _settings.Theme switch
        { "Light" => TitleBarTheme.Light, "Dark" => TitleBarTheme.Dark, _ => TitleBarTheme.UseDefaultAppMode };
    }

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (_exitRequested) return;
        UpdateCornerRadii();
        UpdatePresenterConstraints(GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0);
    }

    // The presenter minimums are physical pixels, so they must follow the monitor DPI; the OS itself keeps the DIP window size.
    private void UpdatePresenterConstraints(double scale)
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(560 * scale);
            presenter.PreferredMinimumHeight = (int)(440 * scale);
        }
    }

    private void UpdateCornerRadii()
    {
        if (Shell.XamlRoot is null) return;
        var scale = Shell.XamlRoot.RasterizationScale;
        if (_cornerScale == scale) return;
        var cardRadius = new CornerRadius(10);
        var controlRadius = new CornerRadius(6);
        foreach (var card in new[] { SettingsPage?.AppearanceCard, SettingsPage?.CleanupSettingsCard, SettingsPage?.AutoSettingsCard, SettingsPage?.AboutCard })
            if (card is not null) card.CornerRadius = cardRadius;
        IssueBanner.CornerRadius = AdminBanner.CornerRadius = UpdateNotice.CornerRadius = cardRadius;
        CleanupHistoryExpander.CornerRadius = ProcessesExpander.CornerRadius = new CornerRadius(0);
        MetricsCard.CornerRadius = cardRadius;
        CleanCard.CornerRadius = _statsStacked ? new CornerRadius(0, 0, 10, 10) : new CornerRadius(0, 10, 10, 0);
        foreach (var control in new Control?[] {
            ElevateButton, SettingsPage?.ThemeChoice, SettingsPage?.LanguageChoice,
            SettingsPage?.ThresholdNumber, SettingsPage?.IntervalNumber, SettingsPage?.SourceLink, SettingsPage?.ExitButton, SettingsPage?.UpdateButton, SettingsPage?.ReleasesLink, SettingsPage?.AdvancedCleanup,
            ProcessRefreshButton })
            if (control is not null) control.CornerRadius = controlRadius;
        CleanButton.CornerRadius = controlRadius;
        OverviewTab.CornerRadius = SettingsTab.CornerRadius = new CornerRadius(0);
        _cornerScale = scale;
    }

    private void RefreshOptions()
    {
        if (_exitRequested) return;
        SettingsHost.IsEnabled = !_elevating;
        OverviewTab.IsEnabled = SettingsTab.IsEnabled = !_elevating;
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = _settings.AlwaysOnTop;
        if (SettingsPage is not null)
        {
            SettingsPage.StartupSwitch.IsEnabled = _startupReadable && !_cleaning && !_elevating;
            SetText(SettingsPage.AdvancedCleanupSummary, UiText.Get("AdvancedCleanupSummary", AdvancedCleanupCount()));
            SettingsPage.ThresholdNumber.IsEnabled = _settings.AutoThreshold;
            SettingsPage.IntervalNumber.IsEnabled = _settings.AutoInterval;
            SettingsPage.LanguageChoice.IsEnabled = !_cleaning && !_restartPromptOpen && !_elevating;
            if (Installation.IsInstalled) SettingsPage.StartupNote.Text = UiText.Get(SettingsPage.StartupSwitch.IsOn ? "InstalledStartupOn" : "InstalledStartupNote");
            SettingsPage.GameModeStatus.Text = UiText.Get(_gameActive ? "GameModeActive" : _settings.GameMode ? "GameModeWatching" : "GameModeOff");
        }
        ManualGameSwitch.IsOn = _settings.ManualGameMode;
        ManualGameSwitch.IsEnabled = !_cleaning && !_restartPromptOpen && !_elevating;
        CleanButton.IsEnabled = !_cleaning && !_restartPromptOpen && !_elevating && _settings.ManualAreas != CleanAreas.None;
        ElevateButton.IsEnabled = !_checking && !_cleaning && !_restartPromptOpen && !_elevating;
        if (Installation.IsInstalled) ElevateButton.Content = UiText.Get("AuthorizeHelper");
        if (_elevating) CleanButton.Content = UiText.Get("ElevationStarting");
        else if (!_cleaning) CleanButton.Content = UiText.Get(CanCleanMemory ? "CleanMemory" : "AuthorizeClean");
        CleanDescription.Text = UiText.Get(!CanCleanMemory ? Installation.IsInstalled ? "HelperAuthorizeDescription" : "AuthorizeDescription"
            : _settings.ManualGameMode ? "GameCleanupDescription" : "CleanDescription");
        AdminBanner.IsOpen = !_gameActive && (_settings.AutoThreshold || _settings.AutoInterval)
            && _settings.AutomaticAreas != CleanAreas.None && !CanCleanMemory;
        AdminBanner.Visibility = AdminBanner.IsOpen ? Visibility.Visible : Visibility.Collapsed;
        var modes = new List<string>();
        if (_settings.AutoThreshold) modes.Add(UiText.Get("ThresholdMode", _settings.Threshold));
        if (_settings.AutoInterval) modes.Add(UiText.Get("IntervalMode", _settings.IntervalMinutes));
        AutoStatusText.Text = modes.Count == 0 ? UiText.Get("AutoOff") :
            UiText.Get("AutoModes", string.Join(UiText.Get("AutoModeJoin"), modes)) + (AdminBanner.IsOpen ? UiText.Get("WaitingPermission") : "");
        if (_settings.Areas == CleanAreas.None) AutoStatusText.Text = UiText.Get("SelectArea");
        else if (modes.Count != 0 && _settings.AutomaticAreas == CleanAreas.None) AutoStatusText.Text = UiText.Get("ManualOnlyAuto");
        else if (_settings.AutoThreshold && _thresholdRetryAfter > DateTimeOffset.UtcNow)
            AutoStatusText.Text += UiText.Get("ThresholdRetryDeferred");
        if (_gameActive || _settings.ManualGameMode) AutoStatusText.Text = UiText.Get("GameModeActive");
        ProtectionStatusText.Text = !_settings.ManualGameMode && _settings.GameMode ? UiText.Get("FullscreenPauseLabel") : "";
    }

    private int AdvancedCleanupCount() => System.Numerics.BitOperations.PopCount((uint)(_settings.Areas
        & (CleanAreas.ModifiedFileCache | CleanAreas.ModifiedPages | CleanAreas.Standby | CleanAreas.RegistryCache | CleanAreas.CombinePages)));

    internal void ReflowMetrics(double width, double textScale)
    {
        if (_exitRequested) return;
        var availableWidth = width;
        var gutter = availableWidth >= 700 ? 20 : 16;
        BodyScroll.Padding = new Thickness(gutter, 0, gutter, 16);
        NavigationGutter.Margin = new Thickness(gutter, 0, gutter, 12);
        NotificationScroll.Margin = new Thickness(gutter, 0, gutter, 0);
        var pageInset = BodyScroll.Padding;
        BrandHeader.Margin = new Thickness(gutter - 2, 0, 0, 0);
        BrandName.Visibility = width < 760 * textScale ? Visibility.Collapsed : Visibility.Visible;
        AppTitleBar.Height = Math.Max(32, 28 * textScale);
        if (SettingsPage is not null)
        {
            SettingsPage.ThresholdNumber.Width = SettingsPage.IntervalNumber.Width = 96 * textScale;
            SettingsPage.ThresholdUnit.Width = SettingsPage.IntervalUnit.Width = 32 * textScale;
            SettingsPage.LanguageChoice.MinWidth = SettingsPage.ThemeChoice.MinWidth = 144 * textScale;
            var compactAbout = width - pageInset.Left - pageInset.Right < 500 * textScale;
            foreach (var control in new FrameworkElement[] { SettingsPage.UpdateButton, SettingsPage.SourceLink, SettingsPage.ExitButton })
            {
                Grid.SetRow(control, compactAbout ? 1 : 0);
                Grid.SetColumn(control, compactAbout ? 0 : 1);
                Grid.SetColumnSpan(control, compactAbout ? 2 : 1);
            }
            foreach (var row in new[] { SettingsPage.AboutUpdateRow, SettingsPage.AboutLinksRow, SettingsPage.AboutLicenseRow })
            {
                Grid.SetColumnSpan((FrameworkElement)row.Children[0], compactAbout ? 2 : 1);
                row.RowSpacing = compactAbout ? (double)Application.Current.Resources["Space12"] : 0;
            }
        }
        var contentWidth = Math.Min(920, availableWidth - pageInset.Left - pageInset.Right);
        var stacked = contentWidth < 700 * textScale;
        var readingWidth = contentWidth - (stacked ? 0 : 224 * textScale) - 32;
        var compactDetails = readingWidth < 500 * textScale;
        MetricsDetails.RowSpacing = compactDetails ? 8 : 0;
        Grid.SetRow(CacheCell, compactDetails ? 1 : 0);
        Grid.SetColumn(CacheCell, compactDetails ? 0 : 1);
        Grid.SetColumnSpan(CommitCell, compactDetails ? 2 : 1);
        Grid.SetColumnSpan(CacheCell, compactDetails ? 2 : 1);
        _statsStacked = stacked;
        MetricsGrid.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(224 * textScale);
        Grid.SetRow(CleanCard, stacked ? 1 : 0);
        Grid.SetColumn(CleanCard, stacked ? 0 : 1);
        CleanCard.BorderThickness = stacked ? new Thickness(0, 0.5, 0, 0) : new Thickness(0.5, 0, 0, 0);
        CleanCard.Padding = new Thickness(16);
        var radius = MetricsCard.CornerRadius.TopLeft;
        CleanCard.CornerRadius = stacked ? new CornerRadius(0, 0, radius, radius) : new CornerRadius(0, radius, radius, 0);
        PhysicalCell.Padding = new Thickness(16);
        foreach (var fact in new[] { UsedFact, FreeFact, TotalFact })
        {
            fact.Orientation = stacked ? Orientation.Vertical : Orientation.Horizontal;
            fact.Spacing = stacked ? 4 : 8;
        }
        var compactPlan = contentWidth < 620 * textScale;
        Grid.SetRow(ProtectionStatusText, compactPlan ? 1 : 0);
        Grid.SetColumn(ProtectionStatusText, compactPlan ? 0 : 1);
        Grid.SetColumnSpan(AutoStatusText, compactPlan ? 2 : 1);
    }

    private void Sample()
    {
        if (_exitRequested) return;
        try
        {
            UpdateGameProtection(_settings.ManualGameMode || (_settings.GameMode && GameModeMonitor.IsFullscreenApp()), DateTimeOffset.UtcNow);
            var snapshot = MemoryEngine.Read();
            _lastReadUtc = DateTimeOffset.UtcNow;
            _lastPercent = snapshot.PhysicalPercent;
            ObserveThresholdRetry(snapshot.PhysicalPercent, DateTimeOffset.UtcNow);
            if (CanRenderWindow) UpdateStats(snapshot);
            SetSamplingState(false);
            UpdateTraySafely();
            if (!_checking && Installation.IsInstalled && !_helperProbeBusy && !_cleaning && !_elevating && DateTimeOffset.UtcNow >= _nextHelperProbe)
                _ = RefreshHelperAsync(startTask: !_helperReady);
            if (CanStartAutomaticCleanup && CanCleanMemory && _settings.IsAutoDue(snapshot.PhysicalPercent, DateTimeOffset.UtcNow, _lastAttempt, _gameActive, _thresholdRetryAfter))
                _ = CleanAsync(false);
        }
        catch (Exception error)
        {
            ReportSamplingFailure(error);
        }
    }

    private void ReportSamplingFailure(Exception error)
    {
        var firstFailure = !_sampleFailed;
        SetSamplingState(true);
        if (firstFailure) ReportIssue(UiText.Get("SampleFailed"), error.Message, InfoBarSeverity.Error);
        UpdateTraySafely();
    }

    private void SetSamplingState(bool failed)
    {
        var wasFailed = _sampleFailed;
        _sampleFailed = failed;
        MonitoringStatus.Visibility = failed && OverviewPage.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        PressureText.Visibility = !failed && OverviewPage.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        if (failed) StopMetricsAnimation();
        if (wasFailed != failed)
        {
            SetText(LiveLabel, UiText.Get(failed ? "MonitoringUnavailable" : "LiveLabel.Text"));
        }
        var color = GetStatusColor(0, failed ? CleanupFeedback.Failed : CleanupFeedback.None);
        if (LiveIndicator.Fill is not Microsoft.UI.Xaml.Media.SolidColorBrush brush || brush.Color != color)
            LiveIndicator.Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(color);
        if (failed)
        {
            if (_lastReadUtc is not null) UpdatePhysicalColor(_lastPercent);
            var label = UiText.Get(_lastReadUtc is null ? "NoReading" : "LastReading");
            SetText(PressureText, label);
            PressureText.Foreground = LiveIndicator.Fill;
            var hint = _lastReadUtc is { } time ? UiText.Get("LastReadingTime", time.ToLocalTime()) : UiText.Get("NoReading");
            ToolTipService.SetToolTip(LiveLabel, hint);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(LiveLabel, hint);
        }
        else if (wasFailed)
        {
            ToolTipService.SetToolTip(LiveLabel, null);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(LiveLabel, "");
            if (IssueBanner.Title == UiText.Get("SampleFailed")) IssueBanner.IsOpen = false;
        }
    }

    private void UpdateGameProtection(bool active, DateTimeOffset now)
    {
        if (_gameActive == active) return;
        // A full-screen exit or Alt+Tab must not immediately trigger a pending cleanup.
        if (_gameActive) _lastAttempt = now;
        _gameActive = active;
        RefreshOptions();
    }

    private void ObserveThresholdRetry(double percent, DateTimeOffset now)
    {
        if (_thresholdRetryAfter == default || (percent >= _settings.Threshold && now < _thresholdRetryAfter)) return;
        _thresholdRetryAfter = default;
        RefreshOptions();
    }

    private void CompleteCleanupAttempt(bool manual, double percent, DateTimeOffset now)
    {
        // Count the cooldown from completion, including slow native operations.
        _lastAttempt = now;
        if (!manual)
            _thresholdRetryAfter = percent >= _settings.Threshold ? now.AddMinutes(5) : default;
        RefreshOptions();
    }

    private void UpdateTray()
    {
        var state = GetTrayState();
        _tray?.Update(state.Percent, state.Feedback, state.Status, _gameActive, state.Frame);
    }

    private (double? Percent, CleanupFeedback Feedback, string Status, int Frame) GetTrayState()
    {
        if (_feedback != CleanupFeedback.Cleaning && Environment.TickCount64 >= _feedbackUntil) _feedback = CleanupFeedback.None;
        var status = _feedback != CleanupFeedback.None ? _feedbackText
            : UiText.Get(_sampleFailed ? "MonitoringUnavailable" : _gameActive ? "GameModeActive" : _lastPercent >= 90 ? "MemoryCritical" : _lastPercent >= 70 ? "MemoryWarning" : "MemoryNormal");
        if (_sampleFailed && _feedback != CleanupFeedback.None) status = UiText.Get("MonitoringUnavailable") + "  " + status;
        if (_gameActive && (_feedback != CleanupFeedback.None || _sampleFailed)) status += "  " + UiText.Get("GameModeActive");
        return (_sampleFailed ? null : _lastPercent,
            _sampleFailed && _feedback != CleanupFeedback.Cleaning ? CleanupFeedback.Failed : _feedback,
            status, _feedback == CleanupFeedback.Cleaning ? _animationFrame : -1);
    }

    private void UpdateTraySafely()
    {
        if (_exitRequested) return;
        try { UpdateTray(); }
        catch (Exception error) { _animationTimer?.Stop(); ReportIssue(UiText.Get("TrayUnavailable"), error.Message); }
    }

    private void SetFeedback(CleanupFeedback feedback, string text)
    {
        _feedback = feedback; _feedbackText = text;
        _feedbackUntil = Environment.TickCount64 + 5000;
        if (feedback == CleanupFeedback.Cleaning) { _animationFrame = 0; _animationTimer?.Start(); }
        else _animationTimer?.Stop();
        UpdateTraySafely();
    }

    internal void UpdateStats(MemorySnapshot snapshot)
    {
        SetText(PressureText, UiText.Get(_metricsCleaning ? "Cleaning" : snapshot.PhysicalPercent >= 90 ? "PressureHigh" : snapshot.PhysicalPercent >= 70 ? "PressureElevated" : "PressureNormal"));
        SetText(PhysicalUsed, MemorySnapshot.Bytes(snapshot.PhysicalTotal - snapshot.PhysicalAvailable));
        SetText(PhysicalFree, MemorySnapshot.Bytes(snapshot.PhysicalAvailable));
        SetText(PhysicalTotal, MemorySnapshot.Bytes(snapshot.PhysicalTotal));
        SetText(CommitUsed, UiText.Get("CommittedMemory", MemorySnapshot.Bytes(snapshot.CommitUsed)));
        SetText(CommitTotal, UiText.Get("CommitLimit", MemorySnapshot.Bytes(snapshot.CommitLimit)));
        SetText(CachePeak, snapshot.CachePeak is { } peak ? UiText.Get("PeakMemory", MemorySnapshot.Bytes(peak)) : UiText.Get("PeakUnavailable"));
        UpdatePhysicalColor(snapshot.PhysicalPercent);
        UpdateAnimatedMetrics(snapshot);
    }

    private void UpdatePhysicalColor(double percent)
    {
        var theme = _accessibility.HighContrast ? "HighContrast" : Shell.ActualTheme == ElementTheme.Dark ? "Default" : "Light";
        var palette = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[theme];
        var pressure = percent >= 90 ? "High" : percent >= 70 ? "Elevated" : "Normal";
        var barBrush = (Microsoft.UI.Xaml.Media.Brush)palette[$"Usage{pressure}Brush"];
        var textBrush = (Microsoft.UI.Xaml.Media.Brush)palette[$"Usage{pressure}TextBrush"];
        if (!ReferenceEquals(PhysicalBar.Foreground, barBrush)) PhysicalBar.Foreground = barBrush;
        if (!ReferenceEquals(PressureText.Foreground, textBrush)) PressureText.Foreground = textBrush;
    }

    private Windows.UI.Color GetStatusColor(double percent, CleanupFeedback feedback)
    {
        var rgb = TrayIcon.StatusColor(percent, feedback, false, Shell.ActualTheme == ElementTheme.Dark);
        if (_accessibility.HighContrast)
        {
            var foreground = _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground);
            rgb = (uint)(foreground.R << 16 | foreground.G << 8 | foreground.B);
        }
        return Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    internal static void SetText(TextBlock block, string text) { if (block.Text != text) block.Text = text; }
    private static void SetProgress(ProgressBar bar, double value)
    { value = Math.Round(value, 1); if (bar.Value != value) bar.Value = value; }

    private void ReportIssue(string title, string message, InfoBarSeverity severity = InfoBarSeverity.Warning)
    {
        if (_exitRequested) return;
        IssueBanner.Title = title; IssueBanner.Message = message; IssueBanner.Severity = severity;
        IssueBanner.Visibility = Visibility.Visible; IssueBanner.IsOpen = true;
    }

    private void OverviewTab_Click(object sender, RoutedEventArgs args) => SelectPage(false);
    private void SettingsTab_Click(object sender, RoutedEventArgs args) => SelectPage(true);
    internal void SelectPage(bool settings)
    {
        if (settings) EnsureSettingsLoaded();
        foreach (var button in new[] { OverviewTab, SettingsTab })
        {
            var selected = settings == (button == SettingsTab);
            button.IsChecked = selected;
            button.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            button.Translation = System.Numerics.Vector3.Zero;
        }
        OverviewPage.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        SettingsHost.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        PressureText.Visibility = !settings && !_sampleFailed ? Visibility.Visible : Visibility.Collapsed;
        MonitoringStatus.Visibility = !settings && _sampleFailed ? Visibility.Visible : Visibility.Collapsed;
        if (SettingsPage is not null) SettingsPage.Visibility = SettingsHost.Visibility;
        var selectedTab = settings ? SettingsTab : OverviewTab;
        selectedTab.Focus(FocusState.Programmatic);
        Shell.UpdateLayout();
        BodyScroll.ChangeView(0, 0, null, true);
        // Let pending focus/extent changes settle before resetting the shared scroll viewer.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (!_exitRequested)
            {
                FrameworkElement page = SettingsPage?.Visibility == Visibility.Visible ? SettingsPage : OverviewPage;
                page.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0 });
            }
        });
    }

    private void ThemeChoice_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_ready) return;
        _settings.Theme = (SettingsPage.ThemeChoice.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "System";
        ApplyTheme(); SaveSettings();
    }

    private async void LanguageChoice_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_ready) return;
        var previous = _settings.Language;
        _settings.Language = (SettingsPage.LanguageChoice.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "System";
        if (!SaveSettings())
        {
            _settings.Language = previous;
            _ready = false;
            SettingsPage.LanguageChoice.SelectedIndex = previous == "zh-CN" ? 1 : previous == "en-US" ? 2 : 0;
            _ready = true;
            return;
        }
        if (_checking || UiText.ResolveSelectedLanguage(_settings.Language) == UiText.LanguageTag) return;
        _restartPromptOpen = true;
        RefreshOptions();
        try
        {
            if (await CreateLanguageRestartDialog().ShowAsync() != ContentDialogResult.Primary) return;
            if (_exitRequested) return;
            using var process = Process.Start(CreateRestartStartInfo());
            if (process is null) throw new InvalidOperationException(UiText.Get("RestartFailed"));
            ExitWindow();
        }
        catch (Exception error) { ReportIssue(UiText.Get("RestartFailed"), error.Message); }
        finally { _restartPromptOpen = false; if (!_exitRequested) RefreshOptions(); }
    }

    private ContentDialog CreateLanguageRestartDialog() => new()
    {
        XamlRoot = Shell.XamlRoot, RequestedTheme = Shell.ActualTheme,
        CornerRadius = new CornerRadius(12 / Shell.XamlRoot.RasterizationScale),
        Title = UiText.Get("LanguageRestartTitle"), Content = UiText.Get("LanguageRestartMessage"),
        PrimaryButtonText = UiText.Get("RestartNow"), CloseButtonText = UiText.Get("RestartLater"),
        DefaultButton = ContentDialogButton.Close
    };

    internal static ProcessStartInfo CreateRestartStartInfo()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!)
        { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory };
        // Restart only this app; never replay a previous --clean request.
        start.ArgumentList.Add("--restart");
        return start;
    }

    private void Preference_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_ready) return;
        _settings.AlwaysOnTop = SettingsPage.PinSwitch.IsOn;
        _settings.CloseToTray = SettingsPage.TraySwitch.IsOn;
        _settings.NotifyAfterCleanup = SettingsPage.NotifyCleanupSwitch.IsOn;
        _settings.CheckUpdatesOnStartup = SettingsPage.AutoUpdateSwitch.IsOn;
        if (!_settings.CheckUpdatesOnStartup) _startupUpdateCancellation?.Cancel();
        if (_settings.AutoThreshold != SettingsPage.ThresholdSwitch.IsOn) _thresholdRetryAfter = default;
        _settings.AutoThreshold = SettingsPage.ThresholdSwitch.IsOn;
        _settings.AutoInterval = SettingsPage.IntervalSwitch.IsOn;
        RefreshOptions(); SaveSettings();
    }

    private void GameModeSwitch_Toggled(object sender, RoutedEventArgs args)
    {
        if (_ready) SetGameMode(SettingsPage.GameModeSwitch.IsOn);
    }

    private void SetGameMode(bool enabled)
    {
        if (!_ready || _elevating) return;
        _settings.GameMode = enabled;
        if (SettingsPage is not null)
        {
            _ready = false; SettingsPage.GameModeSwitch.IsOn = enabled; _ready = true;
        }
        if (!_settings.GameMode) UpdateGameProtection(_settings.ManualGameMode, DateTimeOffset.UtcNow);
        RefreshOptions(); SaveSettings();
        if (!_checking) Sample();
    }

    private async void ManualGameSwitch_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_ready || ManualGameSwitch.IsOn == _settings.ManualGameMode) return;
        if (_cleaning || _elevating || _restartPromptOpen) { RefreshOptions(); return; }
        var enabled = ManualGameSwitch.IsOn;
        _settings.ManualGameMode = enabled;
        if (!SaveSettings())
        {
            _settings.ManualGameMode = !enabled;
            RefreshOptions();
            return;
        }
        UpdateGameProtection(enabled || (_settings.GameMode && !_checking && GameModeMonitor.IsFullscreenApp()), DateTimeOffset.UtcNow);
        RefreshOptions();
        if (enabled) await CleanAsync(true, confirm: false);
    }

    private void CleanupOption_Click(object sender, RoutedEventArgs args)
    {
        _settings.CleanWorkingSets = SettingsPage.WorkingSetCheck.IsChecked == true;
        _settings.CleanSystemCache = SettingsPage.SystemCacheCheck.IsChecked == true;
        _settings.CleanLowPriorityStandby = SettingsPage.StandbyCheck.IsChecked == true;
        _settings.CleanModifiedFileCache = SettingsPage.ModifiedFileCheck.IsChecked == true;
        _settings.CleanModifiedPages = SettingsPage.ModifiedPagesCheck.IsChecked == true;
        _settings.CleanStandby = SettingsPage.FullStandbyCheck.IsChecked == true;
        _settings.CleanRegistryCache = SettingsPage.RegistryCheck.IsChecked == true;
        _settings.CombineMemoryPages = SettingsPage.CombineCheck.IsChecked == true;
        RefreshOptions(); SaveSettings();
    }

    private void AutoNumber_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_ready || !double.IsFinite(args.NewValue)) return;
        if (sender == SettingsPage.ThresholdNumber) { _settings.Threshold = (int)Math.Round(args.NewValue); _thresholdRetryAfter = default; }
        else _settings.IntervalMinutes = (int)Math.Round(args.NewValue);
        _settings.Normalize();
        var committed = sender == SettingsPage.ThresholdNumber ? _settings.Threshold : _settings.IntervalMinutes;
        if (sender.Value != committed) { _ready = false; sender.Value = committed; _ready = true; }
        RefreshOptions(); SaveSettings();
    }

    private void AutoNumber_LostFocus(object sender, RoutedEventArgs args)
    {
        if (!_ready) return;
        if (sender is NumberBox box && !double.IsFinite(box.Value))
            box.Value = box == SettingsPage.ThresholdNumber ? _settings.Threshold : _settings.IntervalMinutes;
    }

    private async void StartupSwitch_Toggled(object sender, RoutedEventArgs args)
    {
        if (!_ready || _checking) return;
        var desired = SettingsPage.StartupSwitch.IsOn;
        _elevating = true; RefreshOptions();
        SettingsPage.StartupSwitch.IsEnabled = false;
        try
        {
            await StartupRegistration.SetEnabledAsync(desired);
            _startupEnabled = desired;
            if (_exitRequested) return;
            if (desired && Installation.IsInstalled) await RefreshHelperAsync(startTask: true);
        }
        catch (Exception error)
        {
            if (_exitRequested) return;
            _ready = false;
            try { SettingsPage.StartupSwitch.IsOn = StartupRegistration.IsEnabled(); }
            catch { SettingsPage.StartupSwitch.IsOn = !desired; }
            _ready = true;
            ReportIssue(UiText.Get("StartupFailed"), error.Message);
        }
        finally { _elevating = false; if (!_exitRequested) RefreshOptions(); }
    }

    private async void CleanButton_Click(object sender, RoutedEventArgs args)
    {
        if (_checking && App.Arguments.Contains("--visual-preview")) await PreviewMetricsCleanupAsync();
        else await CleanAsync(true);
    }
    private async Task RunStartupCleanupAsync()
    {
        if (_exitRequested || !_startupCleanupPending) return;
        _startupCleanupPending = false;
        await CleanAsync(true, confirm: false);
    }
    internal void ReportStartupResumeFailure(string detail) => ReportIssue(UiText.Get("StartupResumeFailed"), detail);
    private void ConfirmSwitch_Toggled(object sender, RoutedEventArgs args)
    {
        if (_ready) SaveCleanupConfirmation(SettingsPage.ConfirmSwitch.IsOn);
    }

    private bool SaveCleanupConfirmation(bool confirm, bool hintShown = false)
    {
        if (_elevating) return false;
        var previous = (_settings.ConfirmCleanup, _settings.TrayCleanupHintShown);
        _settings.ConfirmCleanup = confirm;
        _settings.TrayCleanupHintShown |= hintShown;
        var saved = SaveSettings();
        if (!saved) (_settings.ConfirmCleanup, _settings.TrayCleanupHintShown) = previous;
        var ready = _ready;
        _ready = false;
        if (SettingsPage is not null) SettingsPage.ConfirmSwitch.IsOn = _settings.ConfirmCleanup;
        _ready = ready;
        return saved;
    }

    private ContentDialog CreateCleanupDialog(bool firstTrayClick, out CheckBox? confirmationChoice)
    {
        confirmationChoice = null;
        object content = UiText.Get("CleanConfirm");
        if (firstTrayClick)
        {
            confirmationChoice = new CheckBox
            {
                IsChecked = _settings.ConfirmCleanup,
                Content = new TextBlock { Text = UiText.Get("TrayConfirmChoice"), TextWrapping = TextWrapping.Wrap }
            };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(confirmationChoice, UiText.Get("TrayConfirmChoice"));
            var panel = new StackPanel { Spacing = 12, MaxWidth = 400 };
            panel.Children.Add(new TextBlock { Text = UiText.Get("TrayFirstCleanMessage"), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(confirmationChoice);
            panel.Children.Add(new TextBlock
            {
                Text = UiText.Get(Installation.IsInstalled ? "TrayInstalledAuthorization" : "TrayPortableAuthorization"),
                TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Muted"]
            });
            content = panel;
        }
        return new ContentDialog
        {
            XamlRoot = Shell.XamlRoot, RequestedTheme = Shell.ActualTheme,
            Title = UiText.Get(firstTrayClick ? "TrayFirstCleanTitle" : "CleanMemory"),
            CornerRadius = new CornerRadius(12 / Shell.XamlRoot.RasterizationScale), Content = content,
            PrimaryButtonText = UiText.Get("CleanAction"), CloseButtonText = UiText.Get("CancelAction"),
            DefaultButton = ContentDialogButton.Close
        };
    }

    private async void CleanFromTray()
    {
        if (_exitRequested || _checking || _cleaning || _restartPromptOpen || _elevating) return;
        if (_settings.ManualAreas == CleanAreas.None)
        {
            SetFeedback(CleanupFeedback.Warning, UiText.Get("SelectArea"));
            ShowWindow(); SelectPage(true); ReportIssue(UiText.Get("CleanMemory"), UiText.Get("SelectArea"));
            return;
        }
        await CleanAsync(true, fromTray: true);
    }

    private async Task CleanAsync(bool manual, bool confirm = true, bool fromTray = false)
    {
        if (_exitRequested || _checking) return;
        var areas = manual ? _settings.ManualAreas : _settings.AutomaticAreas;
        if (_cleaning || _restartPromptOpen || _elevating || (!manual && (_gameActive || _settings.ManualGameMode)) || areas == CleanAreas.None) return;
        _cleaning = true; RefreshOptions();
        Stopwatch? cleanupDuration = null;
        try
        {
            var firstTrayClick = fromTray && !_settings.TrayCleanupHintShown;
            if (manual && confirm && (firstTrayClick || _settings.ConfirmCleanup))
            {
                await ShowForConfirmationAsync();
                if (_exitRequested) return;
                var dialog = CreateCleanupDialog(firstTrayClick, out var confirmationChoice);
                var choice = await dialog.ShowAsync();
                if (_exitRequested) return;
                if (choice != ContentDialogResult.Primary)
                { SetFeedback(CleanupFeedback.Warning, UiText.Get("CleanupCanceled")); return; }
                if (firstTrayClick && !SaveCleanupConfirmation(confirmationChoice!.IsChecked == true, hintShown: true)) return;
            }
            if (Installation.IsInstalled)
            {
                if (!_helperReady && !await AuthorizeHelperAsync()) return;
            }
            else if (!MemoryEngine.IsAdministrator) { await RelaunchElevatedAsync("--clean"); return; }
            if (_exitRequested) return;
            _lastAttempt = DateTimeOffset.UtcNow;
            CleanButton.Content = UiText.Get("Cleaning");
            SetFeedback(CleanupFeedback.Cleaning, UiText.Get("Cleaning"));
            BeginMetricsCleanup();
            cleanupDuration = Stopwatch.StartNew();
            var result = Installation.IsInstalled ? await HelperClient.CleanAsync(areas, automatic: !manual)
                : await Task.Run(() => MemoryEngine.Clean(areas, automatic: !manual));
            if (_exitRequested) return;
            cleanupDuration.Stop();
            ApplyCleanupResult(manual, result, cleanupDuration.Elapsed);
            cleanupDuration = null;
            Sample();
            EndMetricsCleanup();
            CompleteCleanupAttempt(manual, _lastPercent, DateTimeOffset.UtcNow);
            if (result.CompletedAreas != CleanAreas.None && ShouldNotifyCleanup(_settings.NotifyAfterCleanup, result.Errors.Count, manual, _gameActive))
            {
                try { _tray?.NotifySuccess(MemorySnapshot.Bytes(result.FreedBytes)); }
                catch (Exception error) { ReportIssue(UiText.Get("NotificationFailed"), error.Message); }
            }
        }
        catch (Exception error)
        {
            if (_exitRequested) return;
            if (cleanupDuration is not null) RecordCleanup(manual, null, cleanupDuration.Elapsed, error.Message);
            CompleteCleanupAttempt(manual, _lastPercent, DateTimeOffset.UtcNow);
            SetFeedback(CleanupFeedback.Failed, UiText.Get("CleanupFailed")); ReportIssue(UiText.Get("CleanupFailed"), error.Message, InfoBarSeverity.Error);
        }
        finally { EndMetricsCleanup(); _animationTimer?.Stop(); _cleaning = false; RefreshOptions(); }
    }

    internal static bool ShouldNotifyCleanup(bool enabled, int errors, bool manual, bool gameActive)
        => enabled && errors == 0 && (manual || !gameActive);

    private void ApplyCleanupResult(bool manual, CleanupResult result, TimeSpan duration)
    {
        RecordCleanup(manual, result, duration);
        var completed = result.CompletedAreas != CleanAreas.None;
        if (completed) { _settings.LastCleanUtc = DateTimeOffset.UtcNow; SaveSettings(); }
        ResultText.Text = completed ? UiText.Get("FreedStatus", DateTime.Now, MemorySnapshot.Bytes(result.FreedBytes))
            : UiText.Get("CleanupFailed");
        SetFeedback(!completed ? CleanupFeedback.Failed : result.Errors.Count == 0 ? CleanupFeedback.Success : CleanupFeedback.Warning,
            result.Errors.Count == 0 ? ResultText.Text : UiText.Get(CleanupOutcomeKey(result)));
        if (result.Errors.Count != 0) ReportIssue(UiText.Get(CleanupOutcomeKey(result)), string.Join("\n", result.Errors),
            completed ? InfoBarSeverity.Warning : InfoBarSeverity.Error);
        else if (completed && (IssueBanner.Title == UiText.Get("CleanupFailed") || IssueBanner.Title == UiText.Get("PartialCleanup")))
            IssueBanner.IsOpen = false;
    }

    private static string CleanupOutcomeKey(CleanupResult? result) => result is null || result.CompletedAreas == CleanAreas.None
        ? "CleanupFailed" : result.Errors.Count == 0 ? "CleanupSucceeded" : "PartialCleanup";

    private async Task RefreshHelperAsync(bool startTask)
    {
        if (_checking || _exitRequested || _helperProbeBusy) return;
        _helperProbeBusy = true;
        try { _helperReady = await HelperClient.TryConnectAsync(startTask); }
        finally { _helperProbeBusy = false; _nextHelperProbe = DateTimeOffset.UtcNow.AddSeconds(10); if (!_exitRequested) RefreshOptions(); }
    }

    private async Task<bool> AuthorizeHelperAsync()
    {
        if (_checking || _exitRequested || _elevating) return false;
        _elevating = true; RefreshOptions();
        try
        {
            // Reopening reuses the fixed protected task before requesting any new UAC consent.
            _helperReady = await HelperClient.TryConnectAsync(startAuthorizedTask: true);
            if (_exitRequested) return false;
            if (!_helperReady)
            {
                await HelperClient.ConfigureAsync("authorize");
                _helperReady = await HelperClient.TryConnectAsync(startAuthorizedTask: true);
            }
            if (!_helperReady) throw new InvalidOperationException(UiText.Get("HelperUnavailable"));
            return !_exitRequested;
        }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { SetFeedback(CleanupFeedback.Warning, UiText.Get("ElevationCanceled")); return false; }
        catch (Exception error) { ReportIssue(UiText.Get("PermissionRequired"), error.Message); return false; }
        finally { _elevating = false; if (!_exitRequested) RefreshOptions(); }
    }

    private async void Elevate_Click(object sender, RoutedEventArgs args)
    { if (Installation.IsInstalled) await AuthorizeHelperAsync(); else await RelaunchElevatedAsync(""); }
    private async Task RelaunchElevatedAsync(string intent)
    {
        if (_checking || _exitRequested || _elevating || _restartPromptOpen) return;
        _elevating = true;
        RefreshOptions();
        try
        {
            if (!SaveSettings()) return;
            var request = Guid.NewGuid();
            using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, ElevationHandoff.EventName(request), out var created);
            if (!created) throw new InvalidOperationException(UiText.Get("ElevationFailed"));
            using var transfer = new EventWaitHandle(false, EventResetMode.ManualReset, ElevationHandoff.TransferEventName(request), out var transferCreated);
            if (!transferCreated) throw new InvalidOperationException(UiText.Get("ElevationFailed"));
            using var process = Process.Start(ElevationHandoff.CreateStartInfo(request, intent))
                ?? throw new InvalidOperationException(UiText.Get("ElevationFailed"));
            if (!await App.WaitUntilAsync(() =>
            {
                if (_exitRequested) throw new OperationCanceledException();
                if (process.HasExited) throw new InvalidOperationException(UiText.Get(
                    process.ExitCode == ElevationHandoff.WrongAccountExitCode ? "ElevationSameUser" : "ElevationHandoffFailed"));
                return ready.WaitOne(0);
            }, TimeSpan.FromSeconds(10))) throw new TimeoutException(UiText.Get("ElevationHandoffFailed"));
            if (_exitRequested) return;
            transfer.Set();
            ExitWindow();
        }
        catch (OperationCanceledException) when (_exitRequested) { }
        catch (Win32Exception error) when (error.NativeErrorCode == 1223)
        { ResultText.Text = UiText.Get("ElevationCanceled"); SetFeedback(CleanupFeedback.Warning, ResultText.Text); }
        catch (Exception error)
        { SetFeedback(CleanupFeedback.Failed, UiText.Get("ElevationFailed")); ReportIssue(UiText.Get("ElevationFailed"), error.Message); }
        finally { _elevating = false; RefreshOptions(); }
    }

    private async Task ShowForConfirmationAsync()
    {
        if (Shell.IsLoaded) { ShowWindow(); return; }
        var loaded = new TaskCompletionSource();
        RoutedEventHandler onLoaded = (_, _) => loaded.TrySetResult();
        Shell.Loaded += onLoaded;
        try { ShowWindow(); await loaded.Task; }
        finally { Shell.Loaded -= onLoaded; }
    }

    private void ShowWindow()
    {
        AppWindow.Show();
        if (AppWindow.Presenter is OverlappedPresenter presenter && presenter.State == OverlappedPresenterState.Minimized) presenter.Restore();
        Activate(); Sample();
    }
    private void Exit_Click(object sender, RoutedEventArgs args) => ExitWindow();
    private void PrepareToExit()
    {
        if (_exitRequested) return;
        _exitRequested = true; _ready = false; _updateCancellation?.Cancel(); _startupUpdateCancellation?.Cancel();
    }
    internal void ExitWindow() { PrepareToExit(); Close(); }
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
}
