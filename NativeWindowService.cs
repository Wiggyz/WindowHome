using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Forms = System.Windows.Forms;

namespace MonitorApp;

public sealed class NativeWindowService
{
    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        return Forms.Screen.AllScreens
            .Select((screen, index) => new MonitorInfo
            {
                DeviceName = screen.DeviceName,
                DisplayName = $"Monitor {index + 1}{(screen.Primary ? " (Primary)" : "")} - {screen.Bounds.Width}x{screen.Bounds.Height}",
                Bounds = new ScreenRect
                {
                    Left = screen.Bounds.Left,
                    Top = screen.Bounds.Top,
                    Width = screen.Bounds.Width,
                    Height = screen.Bounds.Height
                },
                WorkArea = new ScreenRect
                {
                    Left = screen.WorkingArea.Left,
                    Top = screen.WorkingArea.Top,
                    Width = screen.WorkingArea.Width,
                    Height = screen.WorkingArea.Height
                },
                IsPrimary = screen.Primary
            })
            .ToList();
    }

    public IReadOnlyList<WindowInfo> GetOpenWindows()
    {
        var windows = new List<WindowInfo>();

        EnumWindows((nint hwnd, nint lParam) =>
        {
            var window = GetWindowInfo(hwnd);
            if (window is not null)
            {
                windows.Add(window);
            }

            return true;
        }, nint.Zero);

        return WindowDisplay.OrderForDropdown(windows);
    }

    public WindowInfo? GetWindowInfo(nint hwnd)
    {
        var isVisible = IsWindowVisible(hwnd);
        var isMinimized = IsIconic(hwnd);

        var titleLength = GetWindowTextLength(hwnd);
        if (titleLength == 0)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            return null;
        }

        var title = GetWindowTitle(hwnd, titleLength);
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        if (!TryGetUsefulWindowRect(hwnd, isMinimized, out var rect) || rect.Width < 40 || rect.Height < 40)
        {
            return null;
        }

        var processName = "";
        var executablePath = "";
        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
            try
            {
                executablePath = process.MainModule?.FileName ?? "";
            }
            catch
            {
                executablePath = "";
            }
        }
        catch
        {
            return null;
        }

        return new WindowInfo
        {
            Handle = hwnd,
            ProcessId = (int)processId,
            ProcessName = processName,
            ExecutablePath = executablePath,
            Title = title,
            Rect = new WindowRect
            {
                Left = rect.Left,
                Top = rect.Top,
                Width = rect.Width,
                Height = rect.Height
            },
            IsMaximized = IsZoomed(hwnd),
            IsMinimized = isMinimized,
            IsVisible = isVisible,
            IsHidden = !isVisible
        };
    }

    public bool ApplyRule(WindowInfo window, AppRule rule, MonitorInfo monitor)
    {
        var state = rule.EffectiveWindowState;
        bool moved;
        if (state == SavedWindowState.Maximized)
        {
            moved = MoveAndMaximize(window.Handle, monitor.WorkArea);
        }
        else
        {
            moved = MoveToExactPosition(window.Handle, rule, monitor);
        }

        if (rule.EffectiveUseTrayMinimize)
        {
            TriggerTrayAction(window.Handle, rule.TrayAction);
        }
        else if (state == SavedWindowState.MinimizedToTaskbar)
        {
            ShowWindow(window.Handle, ShowWindowCommand.Minimize);
        }

        return moved;
    }

    private static bool MoveToExactPosition(nint hwnd, AppRule rule, MonitorInfo monitor)
    {
        var position = rule.Position;
        var left = monitor.Bounds.Left + position.Left;
        var top = monitor.Bounds.Top + position.Top;
        var width = Math.Max(120, position.Width);
        var height = Math.Max(80, position.Height);

        ShowWindow(hwnd, ShowWindowCommand.Restore);
        var positioned = SetWindowPos(
            hwnd,
            nint.Zero,
            left,
            top,
            width,
            height,
            SetWindowPositionFlags.ShowWindow
            | SetWindowPositionFlags.NoZOrder
            | SetWindowPositionFlags.NoOwnerZOrder
            | SetWindowPositionFlags.NoActivate
            | SetWindowPositionFlags.FrameChanged);

        var moved = MoveWindow(hwnd, left, top, width, height, true);
        return positioned || moved;
    }

    public bool Matches(WindowInfo window, AppRule rule)
    {
        if (!rule.Enabled)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.ExecutablePath)
            && !string.IsNullOrWhiteSpace(window.ExecutablePath)
            && string.Equals(rule.ExecutablePath, window.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(rule.ProcessName)
            && string.Equals(NormalizeProcessName(rule.ProcessName), NormalizeProcessName(window.ProcessName), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public WindowPosition CapturePosition(WindowInfo window, MonitorInfo monitor)
    {
        return new WindowPosition
        {
            Left = window.Rect.Left - monitor.Bounds.Left,
            Top = window.Rect.Top - monitor.Bounds.Top,
            Width = window.Rect.Width,
            Height = window.Rect.Height
        };
    }

    public SavedWindowState CaptureWindowState(WindowInfo window)
    {
        if (window.IsHidden)
        {
            return SavedWindowState.MinimizedToTray;
        }

        if (window.IsMinimized)
        {
            return SavedWindowState.MinimizedToTaskbar;
        }

        return window.IsMaximized ? SavedWindowState.Maximized : SavedWindowState.Normal;
    }

    public MonitorInfo? FindContainingMonitor(WindowInfo window, IReadOnlyList<MonitorInfo> monitors)
    {
        var screen = Forms.Screen.FromHandle(window.Handle);
        var monitorFromHandle = monitors.FirstOrDefault(monitor =>
            string.Equals(monitor.DeviceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase));
        if (monitorFromHandle is not null)
        {
            return monitorFromHandle;
        }

        return monitors.FirstOrDefault(monitor => monitor.Bounds.ContainsCenterOf(window.Rect))
            ?? monitors.FirstOrDefault(monitor => monitor.IsPrimary)
            ?? monitors.FirstOrDefault();
    }

    private static bool MoveAndMaximize(nint hwnd, ScreenRect workArea)
    {
        ShowWindow(hwnd, ShowWindowCommand.Restore);
        var moved = SetWindowPos(
            hwnd,
            nint.Zero,
            workArea.Left,
            workArea.Top,
            Math.Max(120, workArea.Width),
            Math.Max(80, workArea.Height),
            SetWindowPositionFlags.ShowWindow
            | SetWindowPositionFlags.NoZOrder
            | SetWindowPositionFlags.NoOwnerZOrder);
        MoveWindow(hwnd, workArea.Left, workArea.Top, Math.Max(120, workArea.Width), Math.Max(80, workArea.Height), true);
        ShowWindow(hwnd, ShowWindowCommand.Maximize);
        return moved;
    }

    private static void TriggerTrayAction(nint hwnd, MinimizeToTrayAction action)
    {
        var command = action == MinimizeToTrayAction.CloseButton
            ? SystemCommand.Close
            : SystemCommand.Minimize;
        PostMessage(hwnd, WindowMessage.SystemCommand, (nint)command, nint.Zero);
    }

    private static bool TryGetUsefulWindowRect(nint hwnd, bool isMinimized, out NativeRect rect)
    {
        var placement = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (isMinimized && GetWindowPlacement(hwnd, ref placement))
        {
            rect = placement.NormalPosition;
            return true;
        }

        return GetWindowRect(hwnd, out rect);
    }

    private static string NormalizeProcessName(string processName)
    {
        return Path.GetFileNameWithoutExtension(processName.Trim());
    }

    private static string GetWindowTitle(nint hwnd, int titleLength)
    {
        var builder = new StringBuilder(titleLength + 1);
        _ = GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }

    private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hwnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowPlacement(nint hwnd, ref WindowPlacement placement);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, ShowWindowCommand command);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hwnd, WindowMessage message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        nint hwnd,
        nint hwndInsertAfter,
        int x,
        int y,
        int cx,
        int cy,
        SetWindowPositionFlags flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool MoveWindow(
        nint hwnd,
        int x,
        int y,
        int width,
        int height,
        bool repaint);

    private enum ShowWindowCommand
    {
        Hide = 0,
        Minimize = 6,
        Restore = 9,
        Maximize = 3
    }

    private enum WindowMessage
    {
        SystemCommand = 0x0112
    }

    private enum SystemCommand
    {
        Minimize = 0xF020,
        Close = 0xF060
    }

    [Flags]
    private enum SetWindowPositionFlags : uint
    {
        NoZOrder = 0x0004,
        NoActivate = 0x0010,
        FrameChanged = 0x0020,
        ShowWindow = 0x0040,
        NoOwnerZOrder = 0x0200
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        public int Left { get; }
        public int Top { get; }
        public int Right { get; }
        public int Bottom { get; }
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPlacement
    {
        public int Length;
        public int Flags;
        public int ShowCommand;
        public NativePoint MinPosition;
        public NativePoint MaxPosition;
        public NativeRect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint
    {
        public int X { get; }
        public int Y { get; }
    }
}
