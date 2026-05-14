using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
namespace MonitorApp;

public partial class SoundControlWindow : Window
{
    private readonly AppSettings _settings;
    private readonly NativeWindowService _windowService;
    private readonly Action _saveSettings;
    private readonly Action _playClick;
    private List<WindowInfo> _allWindows = [];
    private List<WindowInfo> _windows = [];
    private SoundAppRule? _selectedRule;
    private SoundHotkeyAction? _pendingHotkeyAction;

    public SoundControlWindow(AppSettings settings, NativeWindowService windowService, Action saveSettings, Action playClick)
    {
        InitializeComponent();
        _settings = settings;
        _windowService = windowService;
        _saveSettings = saveSettings;
        _playClick = playClick;

        AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(AnyButton_Click), true);
        Loaded += (_, _) =>
        {
            RefreshWindows();
            BindRules();
            ClearEditor();
            UpdateHotkeyButtons();
        };
        PreviewKeyDown += SoundControlWindow_PreviewKeyDown;
        PreviewMouseDown += SoundControlWindow_PreviewMouseDown;
    }

    private void BindRules()
    {
        SoundRulesList.ItemsSource = null;
        SoundRulesList.ItemsSource = _settings.SoundControl.Rules;
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
        ProcessCombo.SelectedItem = _windows.FirstOrDefault(window => window.Handle == selectedHandle) ?? _windows.FirstOrDefault();
        UpdateWindowPreview();
    }

    private void ApplyWindowFilter()
    {
        var selectedHandle = (ProcessCombo.SelectedItem as WindowInfo)?.Handle;
        _windows = WindowDisplay.FilterForDropdown(_allWindows, WindowFilterBox.Text).ToList();
        ProcessCombo.ItemsSource = _windows;
        ProcessCombo.SelectedItem = _windows.FirstOrDefault(window => window.Handle == selectedHandle) ?? _windows.FirstOrDefault();
        UpdateWindowPreview();
    }

    private void SaveRuleButton_Click(object sender, RoutedEventArgs e)
    {
        var rule = SaveRuleFromEditor();
        if (rule is not null)
        {
            SetStatus($"Saved {rule.DisplayName}.");
        }
    }

    private SoundAppRule? SaveRuleFromEditor()
    {
        var exePath = ExePathBox.Text.Trim();
        var processName = ProcessNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(exePath) && string.IsNullOrWhiteSpace(processName))
        {
            SetStatus("Choose .exe or process.");
            return null;
        }

        var rule = SaveAutomation.CreateManualSoundRule(new EditorIdentity(NameBox.Text.Trim(), processName, exePath));
        _settings.SoundControl.Rules.Add(rule);
        _selectedRule = rule;

        Persist();
        BindRules();
        SoundRulesList.SelectedItem = rule;
        return rule;
    }

    private void DeleteRuleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedRule is null)
        {
            SetStatus("No sound app selected.");
            return;
        }

        var name = _selectedRule.DisplayName;
        _settings.SoundControl.Rules.Remove(_selectedRule);
        _selectedRule = null;
        Persist();
        BindRules();
        ClearEditor();
        SetStatus($"Deleted {name}.");
    }

    private void NewRuleButton_Click(object sender, RoutedEventArgs e)
    {
        SoundRulesList.SelectedItem = null;
        ClearEditor();
        SetStatus("New sound app ready.");
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshWindows();
        SetStatus("Refreshed current windows.");
    }

    private void ProcessCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateWindowPreview();
    }

    private void WindowFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        ApplyWindowFilter();
    }

    private void UseProcessButton_Click(object sender, RoutedEventArgs e)
    {
        if (ProcessCombo.SelectedItem is not WindowInfo window)
        {
            SetStatus("No process selected.");
            return;
        }

        ApplyEditorIdentity(EditorFieldAutomation.CreateIdentityFromWindow(window));
        SetStatus($"Using {window.ProcessName}.");
    }

    private void BrowseExeButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Applications (*.exe)|*.exe", Title = "Choose application" };
        if (dialog.ShowDialog(this) == true)
        {
            ApplyEditorIdentity(EditorFieldAutomation.CreateIdentityFromExecutable(dialog.FileName));
        }
    }

    private void SoundRulesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedRule = SoundRulesList.SelectedItem as SoundAppRule;
        if (_selectedRule is null)
        {
            return;
        }

        NameBox.Text = _selectedRule.DisplayName;
        ExePathBox.Text = _selectedRule.ExecutablePath;
        ProcessNameBox.Text = _selectedRule.ProcessName;
    }

    private void VolumeUpHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        ArmHotkeyCapture(SoundHotkeyAction.VolumeUp);
    }

    private void VolumeDownHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        ArmHotkeyCapture(SoundHotkeyAction.VolumeDown);
    }

    private void MuteHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        ArmHotkeyCapture(SoundHotkeyAction.ToggleMute);
    }

    private void ArmHotkeyCapture(SoundHotkeyAction action)
    {
        _pendingHotkeyAction = action;
        UpdateHotkeyButtons();
        SetStatus("Press any keyboard key or mouse button to set the hotkey.");
    }

    private void SoundControlWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_pendingHotkeyAction is null)
        {
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        CaptureHotkey(new HotkeyBinding { Kind = HotkeyInputKind.Keyboard, Code = KeyInterop.VirtualKeyFromKey(key), DisplayText = key.ToString() });
        e.Handled = true;
    }

    private void SoundControlWindow_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_pendingHotkeyAction is null)
        {
            return;
        }

        CaptureHotkey(new HotkeyBinding { Kind = HotkeyInputKind.Mouse, Code = GetMouseCode(e.ChangedButton), DisplayText = $"Mouse {e.ChangedButton}" });
        e.Handled = true;
    }

    private void CaptureHotkey(HotkeyBinding binding)
    {
        if (_pendingHotkeyAction is null || binding.Code == 0)
        {
            return;
        }

        if (SoundControlAutomation.HasHotkeyConflict(_settings.SoundControl, _pendingHotkeyAction.Value, binding))
        {
            SetStatus("That hotkey is already used by another sound action.");
            _pendingHotkeyAction = null;
            UpdateHotkeyButtons();
            return;
        }

        switch (_pendingHotkeyAction.Value)
        {
            case SoundHotkeyAction.VolumeUp:
                _settings.SoundControl.VolumeUpHotkey = binding;
                break;
            case SoundHotkeyAction.VolumeDown:
                _settings.SoundControl.VolumeDownHotkey = binding;
                break;
            case SoundHotkeyAction.ToggleMute:
                _settings.SoundControl.MuteHotkey = binding;
                break;
        }

        _pendingHotkeyAction = null;
        Persist();
        UpdateHotkeyButtons();
        SetStatus($"Set {binding.DisplayText}.");
    }

    private void UpdateHotkeyButtons()
    {
        VolumeUpHotkeyButton.Content = GetHotkeyButtonText(SoundHotkeyAction.VolumeUp, _settings.SoundControl.VolumeUpHotkey);
        VolumeDownHotkeyButton.Content = GetHotkeyButtonText(SoundHotkeyAction.VolumeDown, _settings.SoundControl.VolumeDownHotkey);
        MuteHotkeyButton.Content = GetHotkeyButtonText(SoundHotkeyAction.ToggleMute, _settings.SoundControl.MuteHotkey);
    }

    private string GetHotkeyButtonText(SoundHotkeyAction action, HotkeyBinding binding)
    {
        if (_pendingHotkeyAction == action)
        {
            return "Press a key or button...";
        }

        return binding.IsSet ? binding.DisplayText : "Click to set hotkey";
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

    private void ClearEditor()
    {
        _selectedRule = null;
        ApplyEditorIdentity(new EditorIdentity("", "", ""));
    }

    private void Persist()
    {
        _saveSettings();
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private void AnyButton_Click(object sender, RoutedEventArgs e)
    {
        _playClick();
    }

    private void ApplyEditorIdentity(EditorIdentity identity)
    {
        NameBox.Text = identity.DisplayName;
        ProcessNameBox.Text = identity.ProcessName;
        ExePathBox.Text = identity.ExecutablePath;
    }

    private static int GetMouseCode(MouseButton button)
    {
        return button switch
        {
            MouseButton.Left => 1,
            MouseButton.Right => 2,
            MouseButton.Middle => 3,
            MouseButton.XButton1 => 4,
            MouseButton.XButton2 => 5,
            _ => 0
        };
    }
}
