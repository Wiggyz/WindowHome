using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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
    private const int MinimumWindowWidth = 1360;
    private const int MinimumWindowHeight = 860;
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint EventObjectCreate = 0x8000;
    private const uint EventObjectShow = 0x8002;
    private const int ObjectIdWindow = 0;
    private const uint WinEventOutOfContext = 0x0000;

    private readonly AppStateService _stateService = new();
    private readonly NativeWindowService _windowService = new();
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _iconAnimationTimer;
    private readonly DispatcherTimer _windowEventDebounceTimer;
    private readonly HashSet<nint> _appliedWindows = [];

    private AppSettings _settings = new();
    private List<MonitorInfo> _monitors = [];
    private List<WindowInfo> _windows = [];
    private AppRule? _selectedRule;
    private Forms.NotifyIcon? _trayIcon;
    private readonly List<string> _iconFramePaths = [];
    private readonly List<System.Drawing.Icon> _trayAnimationIcons = [];
    private WinEventDelegate? _winEventDelegate;
    private nint _windowEventHook;
    private int _iconFrameIndex;
    private bool _exitRequested;

    public MainWindow()
    {
        InitializeComponent();

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
        AutoStartCheck.IsChecked = IsAutoStartEnabled();
        ModeCombo.SelectedIndex = 0;

        EnsureAnimatedIcons();
        SetupTrayIcon();
        AdvanceAnimatedIcon();
        RefreshAll();
        BindRules();
        ClearEditor();
        StartWindowEventHooks();
        ApplySavedRules();
        _pollTimer.Start();
        _iconAnimationTimer.Start();
        SetStatus($"Rules stored: {_stateService.SettingsPath}");
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
        var selectedHandle = (ProcessCombo.SelectedItem as WindowInfo)?.Handle;
        _windows = _windowService.GetOpenWindows().ToList();
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

        foreach (var window in _windowService.GetOpenWindows())
        {
            if (_appliedWindows.Contains(window.Handle))
            {
                continue;
            }

            var rule = _settings.Rules.FirstOrDefault(savedRule => _windowService.Matches(window, savedRule));
            if (rule is null)
            {
                continue;
            }

            var monitor = FindTargetMonitor(rule);
            if (monitor is null)
            {
                continue;
            }

            if (_windowService.ApplyRule(window, rule, monitor))
            {
                _appliedWindows.Add(window.Handle);
                SetStatus($"Moved {window.ProcessName} to {monitor.DisplayName}");
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
            var rule = SaveRuleFromEditor();
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

    private AppRule? SaveRuleFromEditor()
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

        var rule = _selectedRule ?? new AppRule();
        rule.DisplayName = string.IsNullOrWhiteSpace(NameBox.Text)
            ? Path.GetFileNameWithoutExtension(exePath.Length > 0 ? exePath : processName)
            : NameBox.Text.Trim();
        rule.ExecutablePath = exePath;
        rule.ProcessName = string.IsNullOrWhiteSpace(processName)
            ? Path.GetFileNameWithoutExtension(exePath)
            : Path.GetFileNameWithoutExtension(processName);
        rule.TargetMonitorDeviceName = monitor.DeviceName;
        rule.TargetMonitorLabel = monitor.DisplayName;
        rule.Mode = ModeCombo.SelectedIndex == 1 ? PlacementMode.Maximized : PlacementMode.Exact;
        rule.Enabled = true;

        if (rule.Mode == PlacementMode.Exact)
        {
            rule.Position = new WindowPosition
            {
                Left = ReadInt(LeftBox, 100),
                Top = ReadInt(TopBox, 80),
                Width = ReadInt(WidthBox, 1200),
                Height = ReadInt(HeightBox, 800)
            };
        }

        if (_selectedRule is null)
        {
            _settings.Rules.Add(rule);
            _selectedRule = rule;
        }

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

        var moved = 0;
        foreach (var window in _windowService.GetOpenWindows().Where(window => _windowService.Matches(window, _selectedRule)))
        {
            if (_windowService.ApplyRule(window, _selectedRule, monitor))
            {
                _appliedWindows.Add(window.Handle);
                moved++;
            }
        }

        SetStatus(moved == 0 ? "No matching open windows." : $"Moved {moved} window{(moved == 1 ? "" : "s")}.");
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
            ExePathBox.Text = dialog.FileName;
            ProcessNameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                NameBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
            }
        }
    }

    private void UseProcessButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessCombo.SelectedItem is not WindowInfo window)
        {
            SetStatus("No process selected.");
            return;
        }

        NameBox.Text = window.ProcessName;
        ProcessNameBox.Text = window.ProcessName;
        ExePathBox.Text = window.ExecutablePath;
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
        ModeCombo.SelectedIndex = window.IsMaximized ? 1 : 0;
        LeftBox.Text = position.Left.ToString();
        TopBox.Text = position.Top.ToString();
        WidthBox.Text = position.Width.ToString();
        HeightBox.Text = position.Height.ToString();

        SetStatus(window.IsMaximized
            ? $"Captured {window.ProcessName} maximized on {monitor.DisplayName}."
            : $"Captured {window.ProcessName} size {position.Width}x{position.Height} on {monitor.DisplayName}.");
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
            var existingRule = _settings.Rules.FirstOrDefault(rule => _windowService.Matches(window, rule));
            SelectRuleForOverwrite(existingRule);

            NameBox.Text = window.ProcessName;
            ProcessNameBox.Text = window.ProcessName;
            ExePathBox.Text = window.ExecutablePath;
            MonitorCombo.SelectedItem = monitor;
            ModeCombo.SelectedIndex = window.IsMaximized ? 1 : 0;
            LeftBox.Text = position.Left.ToString();
            TopBox.Text = position.Top.ToString();
            WidthBox.Text = position.Width.ToString();
            HeightBox.Text = position.Height.ToString();

            var savedRule = SaveRuleFromEditor();
            if (savedRule is not null)
            {
                SetStatus(window.IsMaximized
                    ? $"Overwrote {window.ProcessName}: maximized on {monitor.DisplayName}."
                    : $"Overwrote {window.ProcessName}: {position.Width}x{position.Height} at {position.Left},{position.Top} on {monitor.DisplayName}.");
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
        ModeCombo.SelectedIndex = _selectedRule.Mode == PlacementMode.Maximized ? 1 : 0;
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
    }

    private void AutoStartCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        try
        {
            SetAutoStart(AutoStartCheck.IsChecked == true);
            SetStatus(AutoStartCheck.IsChecked == true ? "Autostart enabled." : "Autostart disabled.");
        }
        catch (Exception ex)
        {
            SetStatus($"Autostart update failed: {ex.Message}");
            AutoStartCheck.IsChecked = IsAutoStartEnabled();
        }
    }

    private void HideButton_Click(object sender, RoutedEventArgs e)
    {
        HideToTray();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && MinimizeToTrayCheck.IsChecked == true)
        {
            HideToTray();
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_exitRequested)
        {
            StopWindowEventHooks();
            _pollTimer.Stop();
            _iconAnimationTimer.Stop();
            _trayIcon?.Dispose();
            foreach (var icon in _trayAnimationIcons)
            {
                icon.Dispose();
            }
            return;
        }

        if (MinimizeToTrayCheck.IsChecked == true)
        {
            e.Cancel = true;
            HideToTray();
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
            _exitRequested = true;
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
        ShowInTaskbar = false;
        Hide();
        _trayIcon?.ShowBalloonTip(1000, AppName, "Still watching app windows.", Forms.ToolTipIcon.Info);
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

        WindowPreviewText.Text = $"{window.ProcessName} | PID {window.ProcessId} | {window.Rect.Left},{window.Rect.Top} {window.Rect.Width}x{window.Rect.Height}";
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
        NameBox.Text = "";
        ExePathBox.Text = "";
        ProcessNameBox.Text = "";
        ModeCombo.SelectedIndex = 0;
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

    private void SetStatus(string message)
    {
        StatusText.Text = message;
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

    private static void SetAutoStart(bool enabled)
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
        key.SetValue(AppName, $"\"{currentPath}\"");
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
