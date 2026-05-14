using System.Diagnostics;
using System.IO;
using System.Text.Json.Serialization;

namespace MonitorApp;

public sealed class AppSettings
{
    public bool MinimizeToTray { get; set; } = true;
    public List<AppRule> Rules { get; set; } = [];
}

public sealed class AppRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public bool Enabled { get; set; } = true;
    public bool AutoStart { get; set; }
    public string DisplayName { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string TargetMonitorDeviceName { get; set; } = "";
    public string TargetMonitorLabel { get; set; } = "";
    public PlacementMode Mode { get; set; } = PlacementMode.Exact;
    public SavedWindowState WindowState { get; set; } = SavedWindowState.Normal;
    public bool UseTrayMinimize { get; set; }
    public MinimizeToTrayAction TrayAction { get; set; } = MinimizeToTrayAction.MinimizeButton;
    public WindowPosition Position { get; set; } = new();

    [JsonIgnore]
    public string MatchLabel => string.IsNullOrWhiteSpace(ExecutablePath) ? ProcessName : ExecutablePath;

    [JsonIgnore]
    public SavedWindowState EffectiveWindowState => WindowState == SavedWindowState.Normal && Mode == PlacementMode.Maximized
        ? SavedWindowState.Maximized
        : WindowState == SavedWindowState.MinimizedToTray
            ? SavedWindowState.Normal
        : WindowState;

    [JsonIgnore]
    public bool EffectiveUseTrayMinimize => UseTrayMinimize || WindowState == SavedWindowState.MinimizedToTray;

    [JsonIgnore]
    public string PlacementLabel
    {
        get
        {
            var placement = EffectiveWindowState switch
            {
                SavedWindowState.Maximized => "Maximized",
                SavedWindowState.MinimizedToTaskbar => $"Minimized to taskbar | {Position.Left},{Position.Top} {Position.Width}x{Position.Height}",
                _ => $"{Position.Left},{Position.Top} {Position.Width}x{Position.Height}"
            };

            return EffectiveUseTrayMinimize
                ? $"{placement} | tray via {TrayActionLabel}"
                : placement;
        }
    }

    [JsonIgnore]
    public string AutomationLabel => AutoStart ? "Autostart enabled" : "";

    [JsonIgnore]
    public string TrayActionLabel => TrayAction == MinimizeToTrayAction.CloseButton ? "close button" : "minimize button";
}

public sealed class WindowPosition
{
    public int Left { get; set; } = 100;
    public int Top { get; set; } = 80;
    public int Width { get; set; } = 1200;
    public int Height { get; set; } = 800;
}

public enum PlacementMode
{
    Exact,
    Maximized
}

public enum SavedWindowState
{
    Normal,
    Maximized,
    MinimizedToTaskbar,
    MinimizedToTray
}

public enum MinimizeToTrayAction
{
    MinimizeButton,
    CloseButton
}

public sealed class MonitorInfo
{
    public required string DeviceName { get; init; }
    public required string DisplayName { get; init; }
    public required ScreenRect Bounds { get; init; }
    public required ScreenRect WorkArea { get; init; }
    public bool IsPrimary { get; init; }

    public override string ToString() => DisplayName;
}

public sealed class ScreenRect
{
    public int Left { get; init; }
    public int Top { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Right => Left + Width;
    public int Bottom => Top + Height;

    public bool ContainsCenterOf(WindowRect rect)
    {
        var centerX = rect.Left + rect.Width / 2;
        var centerY = rect.Top + rect.Height / 2;
        return centerX >= Left && centerX < Right && centerY >= Top && centerY < Bottom;
    }
}

public sealed class WindowInfo
{
    public nint Handle { get; init; }
    public int ProcessId { get; init; }
    public string ProcessName { get; init; } = "";
    public string ExecutablePath { get; init; } = "";
    public string Title { get; init; } = "";
    public WindowRect Rect { get; init; } = new();
    public bool IsMaximized { get; init; }
    public bool IsMinimized { get; init; }
    public bool IsVisible { get; init; } = true;
    public bool IsHidden { get; init; }

    public string DisplayLabel => $"{ProcessName} - {Title}";

    public override string ToString() => DisplayLabel;
}

public readonly record struct RuleWindowAssignment(AppRule Rule, WindowInfo Window);

public readonly record struct EditorIdentity(string DisplayName, string ProcessName, string ExecutablePath)
{
    public bool HasIdentity =>
        !string.IsNullOrWhiteSpace(DisplayName)
        || !string.IsNullOrWhiteSpace(ProcessName)
        || !string.IsNullOrWhiteSpace(ExecutablePath);
}

public static class EditorFieldAutomation
{
    public static EditorIdentity CreateIdentityFromExecutable(string executablePath)
    {
        var normalizedName = Path.GetFileNameWithoutExtension(executablePath?.Trim() ?? "");
        return new EditorIdentity(normalizedName, normalizedName, executablePath?.Trim() ?? "");
    }

    public static EditorIdentity CreateIdentityFromWindow(WindowInfo window)
    {
        var processName = NormalizeProcessName(window.ProcessName);
        return new EditorIdentity(processName, processName, window.ExecutablePath);
    }

    public static EditorIdentity ResolveIdentityForPositionSave(EditorIdentity editorIdentity, WindowInfo window)
    {
        return editorIdentity.HasIdentity ? Normalize(editorIdentity) : CreateIdentityFromWindow(window);
    }

    public static bool MatchesRule(AppRule rule, EditorIdentity identity)
    {
        var normalized = Normalize(identity);
        if (!string.IsNullOrWhiteSpace(rule.ExecutablePath) && !string.IsNullOrWhiteSpace(normalized.ExecutablePath))
        {
            return string.Equals(rule.ExecutablePath, normalized.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrWhiteSpace(normalized.ProcessName)
            && string.Equals(NormalizeProcessName(rule.ProcessName), normalized.ProcessName, StringComparison.OrdinalIgnoreCase);
    }

    private static EditorIdentity Normalize(EditorIdentity identity)
    {
        var executablePath = identity.ExecutablePath?.Trim() ?? "";
        var processName = NormalizeProcessName(string.IsNullOrWhiteSpace(identity.ProcessName) ? executablePath : identity.ProcessName);
        var displayName = string.IsNullOrWhiteSpace(identity.DisplayName)
            ? processName
            : identity.DisplayName.Trim();
        return new EditorIdentity(displayName, processName, executablePath);
    }

    private static string NormalizeProcessName(string value)
    {
        return Path.GetFileNameWithoutExtension(value?.Trim() ?? "");
    }
}

public static class SaveAutomation
{
    public static bool ShouldHideOnLaunch(bool startMinimizedToTray)
    {
        return startMinimizedToTray;
    }

    public static AppRule CreateManualRule(EditorIdentity identity, MonitorInfo monitor)
    {
        return new AppRule
        {
            DisplayName = string.IsNullOrWhiteSpace(identity.DisplayName)
                ? Path.GetFileNameWithoutExtension(identity.ExecutablePath.Length > 0 ? identity.ExecutablePath : identity.ProcessName)
                : identity.DisplayName.Trim(),
            ExecutablePath = identity.ExecutablePath.Trim(),
            ProcessName = string.IsNullOrWhiteSpace(identity.ProcessName)
                ? Path.GetFileNameWithoutExtension(identity.ExecutablePath)
                : Path.GetFileNameWithoutExtension(identity.ProcessName),
            TargetMonitorDeviceName = monitor.DeviceName,
            TargetMonitorLabel = monitor.DisplayName,
            Enabled = true
        };
    }
}

public static class LaunchAutomation
{
    public static ProcessStartInfo CreateStartInfo(AppRule rule)
    {
        return new ProcessStartInfo
        {
            FileName = rule.ExecutablePath,
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal,
            WorkingDirectory = Path.GetDirectoryName(rule.ExecutablePath) ?? ""
        };
    }
}

public sealed class WindowRect
{
    public int Left { get; init; }
    public int Top { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}
