using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace MonitorApp;

public partial class MainWindow : Window
{
    private const string AppName = "WindowHome";
    private const string PreviousAppName = "App Auto Monitor Switcher";
    private const string MinimizedToTrayArgument = "--minimized-to-tray";
    private const int MinimumWindowWidth = 760;
    private const int MinimumWindowHeight = 620;
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectShow = 0x8002;
    private const int ObjectIdWindow = 0;
    private const uint WinEventOutOfContext = 0x0000;
    private static readonly int WmTaskbarCreated = RegisterWindowMessage("TaskbarCreated");

    private readonly AppStateService _stateService = new();
    private readonly NativeWindowService _windowService = new();
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _iconAnimationTimer;
    private readonly DispatcherTimer _windowEventDebounceTimer;
    private readonly HashSet<nint> _appliedWindows = [];
    private readonly HashSet<Guid> _startupSuppressedRuleIds = [];
    private readonly SoundPlayer _clickSoundPlayer = CreateClickSoundPlayer();
    private readonly ProcessAudioController _processAudioController = new();
    private readonly GlobalHotkeyService _soundHotkeyService;

    private AppSettings _settings = new();
    private List<MonitorInfo> _monitors = [];
    private List<WindowInfo> _allWindows = [];
    private List<WindowInfo> _windows = [];
    private AppRule? _selectedRule;
    private Forms.NotifyIcon? _trayIcon;
    private readonly List<string> _iconFramePaths = [];
    private readonly List<System.Drawing.Icon> _trayAnimationIcons = [];
    private WinEventDelegate? _winEventDelegate;
    private nint _windowEventHook;
    private int _iconFrameIndex;
    private bool _syncingStartupChecks;
    private SoundControlWindow? _soundControlWindow;

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(AnyButton_Click), true);
        _soundHotkeyService = new GlobalHotkeyService(
            () => _settings.SoundControl,
            action => _processAudioController.ApplyToMatchingSessions(_settings.SoundControl, action));

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _pollTimer.Tick += (_, _) => OnPollTimerTick();

        _windowEventDebounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _windowEventDebounceTimer.Tick += (_, _) =>
        {
            _windowEventDebounceTimer.Stop();
            ApplySavedRules();
        };

        _iconAnimationTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(420)
        };
        _iconAnimationTimer.Tick += (_, _) => AdvanceAnimatedIcon();

        Loaded += MainWindow_Loaded;
        StateChanged += MainWindow_StateChanged;
        SourceInitialized += MainWindow_SourceInitialized;
        Closing += MainWindow_Closing;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        MinWidth = MinimumWindowWidth;
        MinHeight = MinimumWindowHeight;
        _settings = _stateService.Load();
        MinimizeToTrayCheck.IsChecked = _settings.MinimizeToTray;
        _syncingStartupChecks = true;
        AutoStartCheck.IsChecked = IsAutoStartEnabled();
        ElevatedStartupCheck.IsChecked = TryGetElevatedStartupEnabled();
        _syncingStartupChecks = false;
        ModeCombo.SelectedIndex = 0;

        EnsureAnimatedIcons();
        SetupTrayIcon();
        AdvanceAnimatedIcon();
        RefreshAll();
        BindRules();
        ClearEditor();
        MarkAlreadyRunningSavedAppsForStartupSkip();
        StartWindowEventHooks();
        StartAutoStartRules();
        _soundHotkeyService.Start();
        _pollTimer.Start();
        _iconAnimationTimer.Start();
        SetStatus($"Rules stored: {_stateService.SettingsPath}");
        if (SaveAutomation.ShouldHideOnLaunch(_settings.MinimizeToTray))
        {
            Dispatcher.BeginInvoke(HideToTray);
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WindowProc);
        }
    }

    private nint WindowProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo)
        {
            if (msg == WmTaskbarCreated)
            {
                RestoreTrayIconAfterTaskbarRestart();
            }

            return nint.Zero;
        }

        var minMaxInfo = Marshal.PtrToStructure<MinMaxInfo>(lParam);
        var dpi = VisualTreeHelper.GetDpi(this);
        minMaxInfo.MinTrackSize.X = (int)Math.Ceiling(MinimumWindowWidth * dpi.DpiScaleX);
        minMaxInfo.MinTrackSize.Y = (int)Math.Ceiling(MinimumWindowHeight * dpi.DpiScaleY);
        Marshal.StructureToPtr(minMaxInfo, lParam, true);
        handled = true;
        return nint.Zero;
    }

    private void RefreshAll()
    {
        RefreshMonitors();
        RefreshWindows();
    }

    private void RefreshMonitors()
    {
        _monitors = _windowService.GetMonitors().ToList();
        MonitorCountText.Text = $"{_monitors.Count} active monitor{(_monitors.Count == 1 ? "" : "s")} detected";
        MonitorList.ItemsSource = _monitors;
        MonitorCombo.ItemsSource = _monitors;

        if (MonitorCombo.SelectedItem is null && _monitors.Count > 0)
        {
            MonitorCombo.SelectedItem = _monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? _monitors[0];
        }
    }

    private void RefreshWindows()
    {
        if (ProcessCombo.IsDropDownOpen)
        {
            return;
        }

        var selectedHandle = (ProcessCombo.SelectedItem as WindowInfo)?.Handle;
        _allWindows = _windowService.GetOpenWindows().ToList();
        _windows = WindowDisplay.FilterForDropdown(_allWindows, WindowFilterBox.Text).ToList();
        ProcessCombo.ItemsSource = _windows;
        ProcessCombo.SelectedItem = _windows.FirstOrDefault(window => window.Handle == selectedHandle)
            ?? _windows.FirstOrDefault();
        UpdateWindowPreview();
    }

    private void ApplyWindowFilter()
    {
        var selectedHandle = (ProcessCombo.SelectedItem as WindowInfo)?.Handle;
        _windows = WindowDisplay.FilterForDropdown(_allWindows, WindowFilterBox.Text).ToList();
        ProcessCombo.ItemsSource = _windows;
        ProcessCombo.SelectedItem = _windows.FirstOrDefault(window => window.Handle == selectedHandle)
            ?? _windows.FirstOrDefault();
        UpdateWindowPreview();
    }

    private void BindRules()
    {
        UpdateRuleMonitorLabels();
        RulesList.ItemsSource = null;
        RulesList.ItemsSource = _settings.Rules;
    }

    private void UpdateRuleMonitorLabels()
    {
        foreach (var rule in _settings.Rules)
        {
            rule.Enabled = true;
            rule.TargetMonitorLabel = _monitors.FirstOrDefault(monitor =>
                    string.Equals(monitor.DeviceName, rule.TargetMonitorDeviceName, StringComparison.OrdinalIgnoreCase))
                ?.DisplayName
                ?? (string.IsNullOrWhiteSpace(rule.TargetMonitorLabel) ? "Unknown monitor" : rule.TargetMonitorLabel);
        }
    }

    private void OnPollTimerTick()
    {
        if (ShouldRefreshCurrentWindows())
        {
            RefreshMonitors();
            RefreshWindows();
        }
    }

    private bool ShouldRefreshCurrentWindows()
    {
        return IsVisible && ShowInTaskbar && WindowState != WindowState.Minimized;
    }

    private void ApplySavedRules()
    {
        ReleaseStartupSuppressionsForStoppedApps();
        var monitorSignature = string.Join("|", _monitors.Select(monitor => $"{monitor.DeviceName}:{monitor.Bounds.Left}:{monitor.Bounds.Top}:{monitor.Bounds.Width}:{monitor.Bounds.Height}"));
        var latestMonitors = _windowService.GetMonitors().ToList();
        var latestSignature = string.Join("|", latestMonitors.Select(monitor => $"{monitor.DeviceName}:{monitor.Bounds.Left}:{monitor.Bounds.Top}:{monitor.Bounds.Width}:{monitor.Bounds.Height}"));
        if (!string.Equals(monitorSignature, latestSignature, StringComparison.Ordinal))
        {
            _monitors = latestMonitors;
            MonitorCountText.Text = $"{_monitors.Count} active monitor{(_monitors.Count == 1 ? "" : "s")} detected";
            MonitorList.ItemsSource = _monitors;
            MonitorCombo.ItemsSource = _monitors;
            BindRules();
            _appliedWindows.Clear();
        }

        var assignments = RuleAutomation.MatchRulesToWindows(
            _settings.Rules.Where(rule => !_startupSuppressedRuleIds.Contains(rule.Id)),
            _windowService.GetOpenWindows(),
            (window, rule) => _windowService.Matches(window, rule),
            _appliedWindows);

        foreach (var assignment in assignments)
        {
            var monitor = FindTargetMonitor(assignment.Rule);
            if (monitor is null)
            {
                continue;
            }

            if (_windowService.ApplyRule(assignment.Window, assignment.Rule, monitor))
            {
                _appliedWindows.Add(assignment.Window.Handle);
                SetStatus($"Moved {assignment.Window.ProcessName} to {monitor.DisplayName}");
            }
        }
    }

    private void ReleaseStartupSuppressionsForStoppedApps()
    {
        if (_startupSuppressedRuleIds.Count == 0)
        {
            return;
        }

        foreach (var ruleId in RuleAutomation.GetStoppedSuppressedRuleIds(_settings, GetRunningProcesses(), _startupSuppressedRuleIds))
        {
            _startupSuppressedRuleIds.Remove(ruleId);
        }
    }

    private void MarkAlreadyRunningSavedAppsForStartupSkip()
    {
        foreach (var ruleId in RuleAutomation.GetAlreadyRunningRuleIds(_settings, GetRunningProcesses()))
        {
            _startupSuppressedRuleIds.Add(ruleId);
        }

        foreach (var window in _windowService.GetOpenWindows())
        {
            var matchingRule = _settings.Rules.FirstOrDefault(rule => _windowService.Matches(window, rule));
            if (matchingRule is not null)
            {
                _appliedWindows.Add(window.Handle);
                _startupSuppressedRuleIds.Add(matchingRule.Id);
            }
        }
    }

    private MonitorInfo? FindTargetMonitor(AppRule rule)
    {
        return _monitors.FirstOrDefault(monitor => string.Equals(monitor.DeviceName, rule.TargetMonitorDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? _monitors.FirstOrDefault(monitor => monitor.IsPrimary)
            ?? _monitors.FirstOrDefault();
    }

    private void SaveRuleButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var rule = SaveRuleFromEditor(appendOnly: true);
            if (rule is not null)
            {
                SetStatus($"Saved {rule.DisplayName}.");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Save failed: {ex.Message}");
        }
    }

    private AppRule? SaveRuleFromEditor(bool appendOnly)
    {
        if (MonitorCombo.SelectedItem is not MonitorInfo monitor)
        {
            SetStatus("Choose target monitor.");
            return null;
        }

        var exePath = ExePathBox.Text.Trim();
        var processName = ProcessNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(exePath) && string.IsNullOrWhiteSpace(processName))
        {
            SetStatus("Choose .exe or process.");
            return null;
        }

        var rule = appendOnly || _selectedRule is null
            ? SaveAutomation.CreateManualRule(ReadRuleEditorIdentity(), monitor)
            : _selectedRule;

        if (!appendOnly && _selectedRule is not null)
        {
            rule.DisplayName = string.IsNullOrWhiteSpace(NameBox.Text)
                ? Path.GetFileNameWithoutExtension(exePath.Length > 0 ? exePath : processName)
                : NameBox.Text.Trim();
            rule.ExecutablePath = exePath;
            rule.ProcessName = string.IsNullOrWhiteSpace(processName)
                ? Path.GetFileNameWithoutExtension(exePath)
                : Path.GetFileNameWithoutExtension(processName);
        }

        rule.TargetMonitorDeviceName = monitor.DeviceName;
        rule.TargetMonitorLabel = monitor.DisplayName;
        rule.WindowState = ReadEditorWindowState();
        rule.Mode = rule.WindowState == SavedWindowState.Maximized ? PlacementMode.Maximized : PlacementMode.Exact;
        rule.UseTrayMinimize = ReadEditorUsesTrayMinimize();
        rule.TrayAction = ReadEditorTrayAction();
        rule.AutoStart = RuleAutoStartCheck.IsChecked == true;
        rule.Enabled = true;

        if (rule.WindowState != SavedWindowState.Maximized)
        {
            rule.Position = new WindowPosition
            {
                Left = ReadInt(LeftBox, 100),
                Top = ReadInt(TopBox, 80),
                Width = ReadInt(WidthBox, 1200),
                Height = ReadInt(HeightBox, 800)
            };
        }

        if (appendOnly || _selectedRule is null)
        {
            _settings.Rules.Add(rule);
        }

        _selectedRule = rule;

        SaveSettings();
        BindRules();
        RulesList.SelectedItem = rule;
        _appliedWindows.Clear();
        return rule;
    }

    private void DeleteRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRule is null)
        {
            SetStatus("No rule selected.");
            return;
        }

        var name = _selectedRule.DisplayName;
        _settings.Rules.Remove(_selectedRule);
        _selectedRule = null;
        SaveSettings();
        BindRules();
        ClearEditor();
        _appliedWindows.Clear();
        SetStatus($"Deleted {name}.");
    }

    private void MoveNowButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRule is null)
        {
            SetStatus("No rule selected.");
            return;
        }

        var monitor = FindTargetMonitor(_selectedRule);
        if (monitor is null)
        {
            SetStatus("No monitor available.");
            return;
        }

        var assignments = RuleAutomation.MatchRulesToWindows(
            [_selectedRule],
            _windowService.GetOpenWindows(),
            (window, rule) => _windowService.Matches(window, rule));
        var moved = 0;
        foreach (var assignment in assignments)
        {
            if (_windowService.ApplyRule(assignment.Window, assignment.Rule, monitor))
            {
                _appliedWindows.Add(assignment.Window.Handle);
                moved++;
            }
        }

        SetStatus(moved == 0 ? "No matching open windows." : $"Moved {moved} window{(moved == 1 ? "" : "s")}.");
    }

    private void StartAppButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRule is null)
        {
            SetStatus("No rule selected.");
            return;
        }

        try
        {
            if (StartAppForRule(_selectedRule))
            {
                SetStatus($"Started {_selectedRule.DisplayName}.");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Start failed: {ex.Message}");
        }
    }

    private void BrowseExeButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Applications (*.exe)|*.exe",
            Title = "Choose application"
        };

        if (dialog.ShowDialog(this) == true)
        {
            ApplyRuleEditorIdentity(EditorFieldAutomation.CreateIdentityFromExecutable(dialog.FileName));
        }
    }

    private void UseProcessButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessCombo.SelectedItem is not WindowInfo window)
        {
            SetStatus("No process selected.");
            return;
        }

        ApplyRuleEditorIdentity(EditorFieldAutomation.CreateIdentityFromWindow(window));
        SetStatus($"Using {window.ProcessName}.");
    }

    private void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        var window = GetFreshSelectedWindow();
        if (window is null)
        {
            SetStatus("No window selected.");
            return;
        }

        var monitor = _windowService.FindContainingMonitor(window, _monitors);
        if (monitor is null)
        {
            SetStatus("Could not find window monitor.");
            return;
        }

        var position = _windowService.CapturePosition(window, monitor);
        MonitorCombo.SelectedItem = monitor;
        var state = _windowService.CaptureWindowState(window);
        SetModeFromWindowState(state);
        LeftBox.Text = position.Left.ToString();
        TopBox.Text = position.Top.ToString();
        WidthBox.Text = position.Width.ToString();
        HeightBox.Text = position.Height.ToString();

        SetStatus(state == SavedWindowState.Maximized
            ? $"Captured {window.ProcessName} maximized on {monitor.DisplayName}."
            : $"Captured {window.ProcessName} {DescribeWindowState(state)} at {position.Left},{position.Top} {position.Width}x{position.Height} on {monitor.DisplayName}.");
    }

    private void SaveCurrentPositionButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = GetFreshSelectedWindow();
            if (window is null)
            {
                SetStatus("No window selected.");
                return;
            }

            var monitor = _windowService.FindContainingMonitor(window, _monitors);
            if (monitor is null)
            {
                SetStatus("Could not find window monitor.");
                return;
            }

            var position = _windowService.CapturePosition(window, monitor);
            var editorIdentity = ReadRuleEditorIdentity();
            var existingRule = FindRuleForEditorIdentity(editorIdentity);
            SelectRuleForOverwrite(existingRule);

            ApplyRuleEditorIdentity(EditorFieldAutomation.ResolveIdentityForPositionSave(editorIdentity, window));
            MonitorCombo.SelectedItem = monitor;
            var state = _windowService.CaptureWindowState(window);
            SetModeFromWindowState(state);
            LeftBox.Text = position.Left.ToString();
            TopBox.Text = position.Top.ToString();
            WidthBox.Text = position.Width.ToString();
            HeightBox.Text = position.Height.ToString();

            var savedRule = SaveRuleFromEditor(appendOnly: false);
            if (savedRule is not null)
            {
                _appliedWindows.Add(window.Handle);
                SetStatus(state == SavedWindowState.Maximized
                    ? $"Overwrote {window.ProcessName}: maximized on {monitor.DisplayName}."
                    : $"Saved {window.ProcessName}: {DescribeWindowState(state)} at {position.Left},{position.Top} {position.Width}x{position.Height} on {monitor.DisplayName}.");
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Save current position failed: {ex.Message}");
        }
    }

    private WindowInfo? GetFreshSelectedWindow()
    {
        if (ProcessCombo.SelectedItem is not WindowInfo selectedWindow)
        {
            return null;
        }

        var freshWindow = _windowService.GetWindowInfo(selectedWindow.Handle);
        if (freshWindow is not null)
        {
            return freshWindow;
        }

        RefreshWindows();
        return ProcessCombo.SelectedItem as WindowInfo;
    }

    private void SelectRuleForOverwrite(AppRule? rule)
    {
        RulesList.SelectedItem = null;
        _selectedRule = rule;
        if (rule is not null)
        {
            RulesList.SelectedItem = rule;
        }
    }

    private AppRule? FindRuleForEditorIdentity(EditorIdentity identity)
    {
        if (!identity.HasIdentity)
        {
            return _selectedRule;
        }

        if (_selectedRule is not null && EditorFieldAutomation.MatchesRule(_selectedRule, identity))
        {
            return _selectedRule;
        }

        return _settings.Rules.FirstOrDefault(rule => EditorFieldAutomation.MatchesRule(rule, identity));
    }

    private static bool ShouldOverwriteSelectedRuleWithWindow(AppRule rule, WindowInfo window)
    {
        if (!string.IsNullOrWhiteSpace(rule.ExecutablePath) && !string.IsNullOrWhiteSpace(window.ExecutablePath))
        {
            return string.Equals(rule.ExecutablePath, window.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrWhiteSpace(rule.ExecutablePath))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(rule.ProcessName)
            && string.Equals(
                Path.GetFileNameWithoutExtension(rule.ProcessName),
                Path.GetFileNameWithoutExtension(window.ProcessName),
                StringComparison.OrdinalIgnoreCase);
    }

    private void NewRuleButton_Click(object sender, RoutedEventArgs e)
    {
        RulesList.SelectedItem = null;
        ClearEditor();
        SetStatus("New rule ready.");
    }

    private void RulesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedRule = RulesList.SelectedItem as AppRule;
        if (_selectedRule is null)
        {
            return;
        }

        NameBox.Text = _selectedRule.DisplayName;
        ExePathBox.Text = _selectedRule.ExecutablePath;
        ProcessNameBox.Text = _selectedRule.ProcessName;
        MonitorCombo.SelectedItem = FindTargetMonitor(_selectedRule);
        SetModeFromWindowState(_selectedRule.EffectiveWindowState);
        SetTrayActionFromRule(_selectedRule.EffectiveUseTrayMinimize, _selectedRule.TrayAction);
        RuleAutoStartCheck.IsChecked = _selectedRule.AutoStart;
        LeftBox.Text = _selectedRule.Position.Left.ToString();
        TopBox.Text = _selectedRule.Position.Top.ToString();
        WidthBox.Text = _selectedRule.Position.Width.ToString();
        HeightBox.Text = _selectedRule.Position.Height.ToString();
        UpdatePlacementVisibility();
    }

    private void ProcessCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateWindowPreview();
    }

    private void WindowFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded || _syncingStartupChecks)
        {
            return;
        }

        ApplyWindowFilter();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshAll();
        SetStatus("Refreshed monitors and windows.");
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdatePlacementVisibility();
    }

    private void MinimizeToTrayCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _settings.MinimizeToTray = MinimizeToTrayCheck.IsChecked == true;
        SaveSettings();
        if (AutoStartCheck.IsChecked == true)
        {
            SetAutoStart(true, _settings.MinimizeToTray);
        }

        if (ElevatedStartupCheck.IsChecked == true)
        {
            try
            {
                ElevatedStartupTask.SetEnabled(true, _settings.MinimizeToTray);
            }
            catch (Exception ex)
            {
                SetStatus($"Admin startup update failed: {CleanSchedulerMessage(ex.Message)}");
                SetElevatedStartupCheck(TryGetElevatedStartupEnabled());
            }
        }
    }

    private void AutoStartCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingStartupChecks)
        {
            return;
        }

        try
        {
            SetAutoStart(AutoStartCheck.IsChecked == true, MinimizeToTrayCheck.IsChecked == true);
            SetStatus(AutoStartCheck.IsChecked == true ? "Autostart enabled." : "Autostart disabled.");
            if (AutoStartCheck.IsChecked == true && ElevatedStartupCheck.IsChecked == true)
            {
                SetElevatedStartupCheck(false);
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Autostart update failed: {ex.Message}");
            SetAutoStartCheck(IsAutoStartEnabled());
        }
    }

    private void ElevatedStartupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _syncingStartupChecks)
        {
            return;
        }

        if (ElevatedStartupCheck.IsChecked == true && !IsRunningAsAdministrator())
        {
            SetElevatedStartupCheck(false);
            const string message = "Admin startup needs permission to create a Task Scheduler entry with administrator privileges. Restart WindowHome as administrator, then enable Admin startup again.";
            System.Windows.MessageBox.Show(this, message, "Restart as administrator", MessageBoxButton.OK, MessageBoxImage.Information);
            SetStatus("Admin startup requires restarting WindowHome as administrator.");
            return;
        }

        try
        {
            ElevatedStartupTask.SetEnabled(ElevatedStartupCheck.IsChecked == true, MinimizeToTrayCheck.IsChecked == true);
            if (ElevatedStartupCheck.IsChecked == true)
            {
                SetAutoStart(false, MinimizeToTrayCheck.IsChecked == true);
                SetAutoStartCheck(false);
            }

            SetStatus(ElevatedStartupCheck.IsChecked == true
                ? "Admin startup task enabled."
                : "Admin startup task removed.");
        }
        catch (Exception ex)
        {
            SetStatus($"Admin startup update failed: {CleanSchedulerMessage(ex.Message)}");
            SetElevatedStartupCheck(TryGetElevatedStartupEnabled());
        }
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    private void SoundControlButton_Click(object sender, RoutedEventArgs e)
    {
        if (_soundControlWindow is not null)
        {
            _soundControlWindow.Activate();
            return;
        }

        _soundControlWindow = new SoundControlWindow(
            _settings,
            _windowService,
            SaveSettings,
            () => _clickSoundPlayer.Play())
        {
            Owner = this
        };
        _soundControlWindow.Closed += (_, _) => _soundControlWindow = null;
        _soundControlWindow.Show();
    }

    private void StartAutoStartRules()
    {
        var runningProcesses = GetRunningProcesses();
        var allLaunchableRules = RuleAutomation.GetLaunchableAutoStartRules(_settings);
        var launchableRules = RuleAutomation.GetLaunchableAutoStartRules(_settings, runningProcesses);
        foreach (var ruleId in RuleAutomation.GetAlreadyRunningRuleIds(_settings, runningProcesses))
        {
            _startupSuppressedRuleIds.Add(ruleId);
        }
        var started = 0;
        foreach (var rule in launchableRules)
        {
            if (StartAppForRule(rule))
            {
                started++;
            }
        }

        var skipped = allLaunchableRules.Count - launchableRules.Count;
        if (started > 0)
        {
            SetStatus(skipped > 0
                ? $"Autostart launched {started} app{(started == 1 ? "" : "s")} and skipped {skipped} already running."
                : $"Autostart launched {started} app{(started == 1 ? "" : "s")}.");
        }
        else if (skipped > 0)
        {
            SetStatus($"Autostart skipped {skipped} already running app{(skipped == 1 ? "" : "s")}.");
        }
    }

    private static IReadOnlyList<RunningProcessInfo> GetRunningProcesses()
    {
        var processes = new List<RunningProcessInfo>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var executablePath = "";
                try
                {
                    executablePath = process.MainModule?.FileName ?? "";
                }
                catch
                {
                    executablePath = "";
                }

                try
                {
                    processes.Add(new RunningProcessInfo(process.ProcessName, executablePath));
                }
                catch
                {
                    // Process exited while scanning.
                }
            }
        }

        return processes;
    }

    private bool StartAppForRule(AppRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.ExecutablePath))
        {
            SetStatus("Selected rule has no executable path.");
            return false;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = rule.ExecutablePath,
            UseShellExecute = true,
            WindowStyle = rule.EffectiveWindowState == SavedWindowState.MinimizedToTaskbar
                ? ProcessWindowStyle.Minimized
                : ProcessWindowStyle.Normal
        };

        Process.Start(startInfo);
        ScheduleApplySavedRules();
        return true;
    }

    private void ScheduleApplySavedRules()
    {
        _ = Task.Delay(1800).ContinueWith(_ => Dispatcher.BeginInvoke(ApplySavedRules));
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            HideToTray();
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        StopWindowEventHooks();
        _pollTimer.Stop();
        _iconAnimationTimer.Stop();
        _soundHotkeyService.Dispose();
        _trayIcon?.Dispose();
        foreach (var icon in _trayAnimationIcons)
        {
            icon.Dispose();
        }
    }

    private void StartWindowEventHooks()
    {
        if (_windowEventHook != nint.Zero)
        {
            return;
        }

        _winEventDelegate = HandleWindowEvent;
        _windowEventHook = SetWinEventHook(
            EventObjectCreate,
            EventObjectShow,
            nint.Zero,
            _winEventDelegate,
            0,
            0,
            WinEventOutOfContext);
    }

    private void StopWindowEventHooks()
    {
        if (_windowEventHook != nint.Zero)
        {
            UnhookWinEvent(_windowEventHook);
            _windowEventHook = nint.Zero;
        }
    }

    private void HandleWindowEvent(
        nint hook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime)
    {
        if (hwnd == nint.Zero || idObject != ObjectIdWindow || idChild != 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            _windowEventDebounceTimer.Stop();
            _windowEventDebounceTimer.Start();
        });
    }

    private void SetupTrayIcon()
    {
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = true;
            return;
        }

        var icon = _trayAnimationIcons.FirstOrDefault()
            ?? (Environment.ProcessPath is { Length: > 0 } processPath
            ? System.Drawing.Icon.ExtractAssociatedIcon(processPath) ?? System.Drawing.SystemIcons.Application
            : System.Drawing.SystemIcons.Application);

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = icon,
            Text = AppName,
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip()
        };
        _trayIcon.ContextMenuStrip.Items.Add("Show", null, (_, _) => ShowFromTray());
        _trayIcon.ContextMenuStrip.Items.Add("Exit", null, (_, _) =>
        {
            _trayIcon.Visible = false;
            Close();
        });
        _trayIcon.DoubleClick += (_, _) => ShowFromTray();
    }

    private void EnsureAnimatedIcons()
    {
        if (_iconFramePaths.Count > 0)
        {
            return;
        }

        var iconDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowHome",
            "Icons");
        Directory.CreateDirectory(iconDir);

        for (var i = 0; i < 6; i++)
        {
            var path = Path.Combine(iconDir, $"windowhome-frame-{i}.ico");
            DrawWindowHomeIcon(path, i);
            _iconFramePaths.Add(path);
            _trayAnimationIcons.Add(new System.Drawing.Icon(path));
        }
    }

    private void AdvanceAnimatedIcon()
    {
        if (_iconFramePaths.Count == 0)
        {
            return;
        }

        _iconFrameIndex = (_iconFrameIndex + 1) % _iconFramePaths.Count;
        var framePath = _iconFramePaths[_iconFrameIndex];
        if (_trayIcon is not null && _iconFrameIndex < _trayAnimationIcons.Count)
        {
            _trayIcon.Icon = _trayAnimationIcons[_iconFrameIndex];
        }

        Icon = BitmapFrame.Create(new Uri(framePath, UriKind.Absolute));
    }

    private static void DrawWindowHomeIcon(string path, int frame)
    {
        using var bitmap = new System.Drawing.Bitmap(256, 256);
        using var graphics = System.Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.Clear(System.Drawing.Color.FromArgb(255, 232, 245));

        using var bg = new System.Drawing.Drawing2D.LinearGradientBrush(
            new System.Drawing.Rectangle(0, 0, 256, 256),
            System.Drawing.Color.FromArgb(255, 218, 238),
            System.Drawing.Color.FromArgb(216, 240, 255),
            35f);
        graphics.FillRectangle(bg, 0, 0, 256, 256);

        using var monitorFill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(244, 232, 255));
        using var screenFill = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 244, 253));
        using var outline = new System.Drawing.Pen(System.Drawing.Color.FromArgb(121, 100, 177), 8);
        using var stand = new System.Drawing.Pen(System.Drawing.Color.FromArgb(121, 100, 177), 8)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Square,
            EndCap = System.Drawing.Drawing2D.LineCap.Square
        };
        graphics.FillRectangle(monitorFill, 16, 28, 224, 184);
        graphics.DrawRectangle(outline, 16, 28, 224, 184);
        graphics.FillRectangle(screenFill, 28, 40, 200, 160);
        graphics.DrawLine(stand, 98, 224, 158, 224);
        graphics.DrawLine(stand, 128, 212, 128, 224);

        var slots = new[]
        {
            new System.Drawing.Rectangle(42, 56, 70, 54),
            new System.Drawing.Rectangle(144, 56, 70, 54),
            new System.Drawing.Rectangle(86, 132, 84, 54)
        };

        var activeColors = new[]
        {
            System.Drawing.Color.FromArgb(190, 232, 255),
            System.Drawing.Color.FromArgb(215, 195, 255),
            System.Drawing.Color.FromArgb(186, 244, 232)
        };
        for (var i = 0; i < slots.Length; i++)
        {
            using var slotBrush = new System.Drawing.SolidBrush(activeColors[(i + frame) % activeColors.Length]);
            graphics.FillRectangle(slotBrush, slots[i]);
        }

        using var arrowOne = new System.Drawing.Pen(System.Drawing.Color.FromArgb(118, 226, 209), 14)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor
        };
        using var arrowTwo = new System.Drawing.Pen(System.Drawing.Color.FromArgb(185, 154, 255), 14)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.ArrowAnchor
        };
        var offset = (frame % 3) * 4;
        graphics.DrawLine(arrowOne, 78 + offset, 132 - offset, 164 + offset, 86 - offset);
        graphics.DrawLine(arrowTwo, 122 - offset, 184 - offset, 176 + offset, 138 - offset);

        using var sparkle = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(255, 200, 107));
        graphics.FillEllipse(sparkle, 196 - frame * 3, 34 + frame * 3, 16, 16);
        graphics.FillEllipse(sparkle, 36 + frame * 4, 202 - frame * 2, 12, 12);

        SaveBitmapAsPngIcon(bitmap, path);
    }

    private static void SaveBitmapAsPngIcon(System.Drawing.Bitmap bitmap, string path)
    {
        using var pngStream = new MemoryStream();
        bitmap.Save(pngStream, System.Drawing.Imaging.ImageFormat.Png);
        var pngBytes = pngStream.ToArray();

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write((uint)pngBytes.Length);
        writer.Write((uint)22);
        writer.Write(pngBytes);
    }

    private void HideToTray()
    {
        SetupTrayIcon();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = true;
        }

        ShowInTaskbar = false;
        Hide();
        _trayIcon?.ShowBalloonTip(1000, AppName, "Still running in the notification area.", Forms.ToolTipIcon.Info);
    }

    private void RestoreTrayIconAfterTaskbarRestart()
    {
        if (_trayIcon is null)
        {
            SetupTrayIcon();
            return;
        }

        _trayIcon.Visible = false;
        _trayIcon.Visible = true;
        if (!IsVisible)
        {
            _trayIcon.ShowBalloonTip(1000, AppName, "WindowHome is still running.", Forms.ToolTipIcon.Info);
        }
    }

    private void ShowFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
        RefreshAll();
    }

    private void UpdateWindowPreview()
    {
        if (ProcessCombo.SelectedItem is not WindowInfo window)
        {
            WindowPreviewText.Text = "No visible top-level windows found.";
            return;
        }

        var state = _windowService.CaptureWindowState(window);
        WindowPreviewText.Text = $"{window.ProcessName} | PID {window.ProcessId} | {DescribeWindowState(state)} | {window.Rect.Left},{window.Rect.Top} {window.Rect.Width}x{window.Rect.Height}";
    }

    private void UpdatePlacementVisibility()
    {
        if (ExactPositionGrid is null)
        {
            return;
        }

        ExactPositionGrid.Visibility = ModeCombo.SelectedIndex == 1 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClearEditor()
    {
        _selectedRule = null;
        ApplyRuleEditorIdentity(new EditorIdentity("", "", ""));
        ModeCombo.SelectedIndex = 0;
        TrayActionCombo.SelectedIndex = 0;
        RuleAutoStartCheck.IsChecked = false;
        LeftBox.Text = "100";
        TopBox.Text = "80";
        WidthBox.Text = "1200";
        HeightBox.Text = "800";
        MonitorCombo.SelectedItem = _monitors.FirstOrDefault(monitor => monitor.IsPrimary) ?? _monitors.FirstOrDefault();
        UpdatePlacementVisibility();
    }

    private void SaveSettings()
    {
        UpdateRuleMonitorLabels();
        _stateService.Save(_settings);
    }

    private EditorIdentity ReadRuleEditorIdentity()
    {
        return new EditorIdentity(NameBox.Text.Trim(), ProcessNameBox.Text.Trim(), ExePathBox.Text.Trim());
    }

    private void ApplyRuleEditorIdentity(EditorIdentity identity)
    {
        NameBox.Text = identity.DisplayName;
        ProcessNameBox.Text = identity.ProcessName;
        ExePathBox.Text = identity.ExecutablePath;
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private void AnyButton_Click(object sender, RoutedEventArgs e)
    {
        _clickSoundPlayer.Play();
    }

    private static SoundPlayer CreateClickSoundPlayer()
    {
        var stream = new MemoryStream(CreateClickWaveBytes());
        var player = new SoundPlayer(stream);
        player.Load();
        return player;
    }

    private static byte[] CreateClickWaveBytes()
    {
        const int sampleRate = 44100;
        const short channels = 1;
        const short bitsPerSample = 16;
        const int durationSamples = sampleRate / 55;
        const int dataLength = durationSamples * channels * bitsPerSample / 8;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + dataLength);
        writer.Write("WAVE"u8.ToArray());
        writer.Write("fmt "u8.ToArray());
        writer.Write(16);
        writer.Write((short)1);
        writer.Write(channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write(bitsPerSample);
        writer.Write("data"u8.ToArray());
        writer.Write(dataLength);

        for (var i = 0; i < durationSamples; i++)
        {
            var envelope = Math.Pow(1.0 - (double)i / durationSamples, 4);
            var tone = Math.Sin(2 * Math.PI * 2200 * i / sampleRate);
            var sample = (short)(short.MaxValue * 0.34 * envelope * tone);
            writer.Write(sample);
        }

        return stream.ToArray();
    }

    private SavedWindowState ReadEditorWindowState()
    {
        return ModeCombo.SelectedIndex switch
        {
            1 => SavedWindowState.Maximized,
            2 => SavedWindowState.MinimizedToTaskbar,
            _ => SavedWindowState.Normal
        };
    }

    private bool ReadEditorUsesTrayMinimize()
    {
        return TrayActionCombo.SelectedIndex > 0;
    }

    private MinimizeToTrayAction ReadEditorTrayAction()
    {
        return TrayActionCombo.SelectedIndex == 2
            ? MinimizeToTrayAction.CloseButton
            : MinimizeToTrayAction.MinimizeButton;
    }

    private void SetModeFromWindowState(SavedWindowState state)
    {
        ModeCombo.SelectedIndex = state switch
        {
            SavedWindowState.Maximized => 1,
            SavedWindowState.MinimizedToTaskbar => 2,
            _ => 0
        };
    }

    private void SetTrayActionFromRule(bool useTrayMinimize, MinimizeToTrayAction action)
    {
        if (!useTrayMinimize)
        {
            TrayActionCombo.SelectedIndex = 0;
            return;
        }

        TrayActionCombo.SelectedIndex = action == MinimizeToTrayAction.CloseButton ? 2 : 1;
    }

    private static string DescribeWindowState(SavedWindowState state)
    {
        return state switch
        {
            SavedWindowState.Maximized => "maximized",
            SavedWindowState.MinimizedToTaskbar => "minimized to taskbar",
            SavedWindowState.MinimizedToTray => "minimized to tray",
            _ => "windowed"
        };
    }

    private static string CleanSchedulerMessage(string message)
    {
        return string.IsNullOrWhiteSpace(message)
            ? "Task Scheduler did not return details. Try starting WindowHome as administrator."
            : message.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    }

    private static bool IsRunningAsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private bool TryGetElevatedStartupEnabled()
    {
        try
        {
            return ElevatedStartupTask.IsEnabled();
        }
        catch (Exception ex)
        {
            SetStatus($"Could not read admin startup task: {CleanSchedulerMessage(ex.Message)}");
            return false;
        }
    }

    private void SetAutoStartCheck(bool value)
    {
        _syncingStartupChecks = true;
        AutoStartCheck.IsChecked = value;
        _syncingStartupChecks = false;
    }

    private void SetElevatedStartupCheck(bool value)
    {
        _syncingStartupChecks = true;
        ElevatedStartupCheck.IsChecked = value;
        _syncingStartupChecks = false;
    }

    private static int ReadInt(System.Windows.Controls.TextBox textBox, int fallback)
    {
        return int.TryParse(textBox.Text.Trim(), out var value) ? value : fallback;
    }

    private static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
        var value = key?.GetValue(AppName) as string;
        var currentPath = Environment.ProcessPath;
        return !string.IsNullOrWhiteSpace(value)
            && !string.IsNullOrWhiteSpace(currentPath)
            && value.Contains(currentPath, StringComparison.OrdinalIgnoreCase);
    }

    private static void SetAutoStart(bool enabled, bool startMinimizedToTray)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true)
            ?? Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);

        if (!enabled)
        {
            key.DeleteValue(AppName, false);
            key.DeleteValue(PreviousAppName, false);
            return;
        }

        var currentPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentPath))
        {
            throw new InvalidOperationException("Cannot resolve app path.");
        }

        key.DeleteValue(PreviousAppName, false);
        var command = startMinimizedToTray
            ? $"\"{currentPath}\" {MinimizedToTrayArgument}"
            : $"\"{currentPath}\"";
        key.SetValue(AppName, command);
    }

    private delegate void WinEventDelegate(
        nint hook,
        uint eventType,
        nint hwnd,
        int idObject,
        int idChild,
        uint eventThread,
        uint eventTime);

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(
        uint eventMin,
        uint eventMax,
        nint eventHookAssembly,
        WinEventDelegate winEventProc,
        uint processId,
        uint threadId,
        uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public Point Reserved;
        public Point MaxSize;
        public Point MaxPosition;
        public Point MinTrackSize;
        public Point MaxTrackSize;
    }
}
