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
    public string DisplayName { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string ProcessName { get; set; } = "";
    public string TargetMonitorDeviceName { get; set; } = "";
    public string TargetMonitorLabel { get; set; } = "";
    public PlacementMode Mode { get; set; } = PlacementMode.Exact;
    public WindowPosition Position { get; set; } = new();

    [JsonIgnore]
    public string MatchLabel => string.IsNullOrWhiteSpace(ExecutablePath) ? ProcessName : ExecutablePath;

    [JsonIgnore]
    public string PlacementLabel => Mode == PlacementMode.Maximized
        ? "Maximized"
        : $"{Position.Left},{Position.Top} {Position.Width}x{Position.Height}";
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

    public string DisplayLabel => $"{ProcessName} - {Title}";
}

public sealed class WindowRect
{
    public int Left { get; init; }
    public int Top { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
}
