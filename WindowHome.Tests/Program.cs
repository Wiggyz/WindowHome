using MonitorApp;
using System.Diagnostics;
using System.Runtime.InteropServices;

if (args.Contains("--integration", StringComparer.OrdinalIgnoreCase))
{
    IntegrationTests.Run();
    return;
}

static WindowInfo Window(nint handle, string processName, string exePath = "")
{
    return new WindowInfo
    {
        Handle = handle,
        ProcessId = (int)handle,
        ProcessName = processName,
        ExecutablePath = exePath,
        Title = $"Window {handle}",
        Rect = new WindowRect { Left = 0, Top = 0, Width = 800, Height = 600 }
    };
}

static WindowInfo NamedWindow(nint handle, string processName, string title)
{
    return new WindowInfo
    {
        Handle = handle,
        ProcessId = (int)handle,
        ProcessName = processName,
        Title = title,
        Rect = new WindowRect { Left = 0, Top = 0, Width = 800, Height = 600 }
    };
}

var autoRule = new AppRule
{
    DisplayName = "Auto app",
    ExecutablePath = @"C:\Tools\app.exe",
    ProcessName = "app",
    AutoStart = true
};
var manualRule = new AppRule
{
    DisplayName = "Manual app",
    ExecutablePath = @"C:\Tools\manual.exe",
    ProcessName = "manual",
    AutoStart = false
};
var noPathRule = new AppRule
{
    DisplayName = "Process-only app",
    ProcessName = "processOnly",
    AutoStart = true
};

var launchable = RuleAutomation.GetLaunchableAutoStartRules(new AppSettings
{
    Rules = [autoRule, manualRule, noPathRule]
});

TestAssert.True(launchable.Count == 1, "Only enabled autostart rules with executable paths should launch.");
TestAssert.True(ReferenceEquals(launchable[0], autoRule), "Launchable rule should be returned unchanged.");
TestAssert.True(autoRule.AutomationLabel == "Autostart enabled", "Autostart label should describe enabled state.");

var launchableWithRunningProcess = RuleAutomation.GetLaunchableAutoStartRules(
    new AppSettings { Rules = [autoRule] },
    [new RunningProcessInfo("APP", "")]);
TestAssert.True(launchableWithRunningProcess.Count == 0, "Autostart should skip app when matching process name is already running.");

launchableWithRunningProcess = RuleAutomation.GetLaunchableAutoStartRules(
    new AppSettings { Rules = [autoRule] },
    [new RunningProcessInfo("different", @"C:\Tools\app.exe")]);
TestAssert.True(launchableWithRunningProcess.Count == 0, "Autostart should skip app when matching executable path is already running.");
var runningRuleIds = RuleAutomation.GetAlreadyRunningRuleIds(
    new AppSettings { Rules = [autoRule] },
    [new RunningProcessInfo("APP", "")]);
TestAssert.True(runningRuleIds.Contains(autoRule.Id), "Already-running saved apps should be suppressible by rule id during startup.");
var stoppedRuleIds = RuleAutomation.GetStoppedSuppressedRuleIds(
    new AppSettings { Rules = [autoRule] },
    [],
    [autoRule.Id]);
TestAssert.True(stoppedRuleIds.Contains(autoRule.Id), "Suppressed saved apps should be released after the app process exits.");
stoppedRuleIds = RuleAutomation.GetStoppedSuppressedRuleIds(
    new AppSettings { Rules = [autoRule] },
    [new RunningProcessInfo("APP", "")],
    [autoRule.Id]);
TestAssert.True(stoppedRuleIds.Count == 0, "Suppressed saved apps should stay suppressed while the original app process is still running.");

var pathOnlyRunningRuleIds = RuleAutomation.GetAlreadyRunningRuleIds(
    new AppSettings
    {
        Rules =
        [
            new AppRule
            {
                DisplayName = "Path only app",
                ExecutablePath = @"C:\Apps\GameTool\GameToolUI.exe"
            }
        ]
    },
    [new RunningProcessInfo("GameToolUI", "")]);
TestAssert.True(pathOnlyRunningRuleIds.Count == 1, "Already-running detection should fall back to executable file name when process path is unavailable.");

var firstExplorerRule = new AppRule { ProcessName = "explorer", TargetMonitorDeviceName = @"\\.\DISPLAY1" };
var secondExplorerRule = new AppRule { ProcessName = "explorer", TargetMonitorDeviceName = @"\\.\DISPLAY2" };
var assignments = RuleAutomation.MatchRulesToWindows(
    [firstExplorerRule, secondExplorerRule],
    [Window((nint)101, "explorer"), Window((nint)202, "explorer")],
    (window, rule) => string.Equals(window.ProcessName, rule.ProcessName, StringComparison.OrdinalIgnoreCase));

TestAssert.True(assignments.Count == 2, "Duplicate app rules should bind to separate matching windows.");
TestAssert.True(assignments[0].Window.Handle != assignments[1].Window.Handle, "Duplicate app rules should not reuse the same window.");

var oneWindowAssignments = RuleAutomation.MatchRulesToWindows(
    [firstExplorerRule, secondExplorerRule],
    [Window((nint)303, "explorer")],
    (window, rule) => string.Equals(window.ProcessName, rule.ProcessName, StringComparison.OrdinalIgnoreCase));

TestAssert.True(oneWindowAssignments.Count == 1, "One open window should satisfy only one duplicate app rule.");

var trayRule = new AppRule
{
    ProcessName = "mixer",
    WindowState = SavedWindowState.Normal,
    UseTrayMinimize = true,
    TrayAction = MinimizeToTrayAction.CloseButton
};

TestAssert.True(trayRule.PlacementLabel.Contains("tray", StringComparison.OrdinalIgnoreCase), "Placement label should show minimized-to-tray state.");
TestAssert.True(trayRule.PlacementLabel.Contains("close button", StringComparison.OrdinalIgnoreCase), "Tray placement label should show selected tray action.");
TestAssert.True(trayRule.EffectiveWindowState == SavedWindowState.Normal, "Tray action should be independent from placement state.");

var orderedWindows = WindowDisplay.OrderForDropdown([
    NamedWindow((nint)1, "zeta", "Window"),
    NamedWindow((nint)2, "Alpha", "Zed"),
    NamedWindow((nint)3, "alpha", "Alpha")
]);
TestAssert.True(orderedWindows[0].Handle == (nint)3, "Current windows dropdown should sort alphabetically by app and title.");
TestAssert.True(orderedWindows[1].Handle == (nint)2, "Current windows dropdown should sort case-insensitively.");
TestAssert.True(orderedWindows[2].Handle == (nint)1, "Current windows dropdown should keep later apps lower.");
TestAssert.True(orderedWindows[0].ToString() == orderedWindows[0].DisplayLabel, "Selected window display text should show the selected window label.");

var filteredWindows = WindowDisplay.FilterForDropdown(orderedWindows, "zed");
TestAssert.True(filteredWindows.Count == 1 && filteredWindows[0].Handle == (nint)2, "Current windows filter should match partial title text.");
filteredWindows = WindowDisplay.FilterForDropdown(orderedWindows, "ALP");
TestAssert.True(filteredWindows.Count == 2, "Current windows filter should match process names case-insensitively.");
filteredWindows = WindowDisplay.FilterForDropdown(orderedWindows, "");
TestAssert.True(filteredWindows.Count == orderedWindows.Count, "Empty current windows filter should show all windows.");

var createTaskArgs = ElevatedStartupTask.BuildCreateArguments(@"C:\Apps\WindowHome.exe", startMinimizedToTray: true);
TestAssert.True(createTaskArgs.Contains("/RL HIGHEST", StringComparison.OrdinalIgnoreCase), "Elevated startup task should request highest privileges.");
TestAssert.True(createTaskArgs.Contains("/IT", StringComparison.OrdinalIgnoreCase), "Elevated startup task should run interactively so tray icon can appear.");
TestAssert.True(createTaskArgs.Contains("--minimized-to-tray", StringComparison.OrdinalIgnoreCase), "Elevated startup task should preserve minimized-to-tray startup.");
TestAssert.True(ElevatedStartupTask.BuildDeleteArguments().Contains(ElevatedStartupTask.TaskName, StringComparison.Ordinal), "Elevated startup delete command should target WindowHome task.");

Console.WriteLine("WindowHome.Tests passed.");

internal static class TestAssert
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

internal static class IntegrationTests
{
    private const int ShowMinimize = 6;
    private const int ShowHide = 0;
    private const int WmClose = 0x0010;

    public static void Run()
    {
        TestExplorerMultipleWindowsAndTaskbarMinimize();
        TestMixerHiddenTrayState();
        Console.WriteLine("WindowHome integration tests passed.");
    }

    private static void TestExplorerMultipleWindowsAndTaskbarMinimize()
    {
        var service = new NativeWindowService();
        var before = service.GetOpenWindows()
            .Where(window => string.Equals(window.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase))
            .Select(window => window.Handle)
            .ToHashSet();

        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = @"C:\Windows", UseShellExecute = true });
        Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = @"C:\Program Files", UseShellExecute = true });

        var explorerWindows = WaitForWindows(service, "explorer", before, 2);
        TestAssert.True(explorerWindows.Count >= 2, "Explorer should expose two new top-level windows.");

        var rules = new[]
        {
            new AppRule { ProcessName = "explorer", TargetMonitorDeviceName = @"\\.\DISPLAY1", AutoStart = true },
            new AppRule { ProcessName = "explorer", TargetMonitorDeviceName = @"\\.\DISPLAY2", AutoStart = true }
        };
        var assignments = RuleAutomation.MatchRulesToWindows(
            rules,
            explorerWindows,
            (window, rule) => string.Equals(window.ProcessName, rule.ProcessName, StringComparison.OrdinalIgnoreCase));

        TestAssert.True(assignments.Count == 2, "Two Explorer rules should map to two Explorer windows.");

        var minimizedHandle = explorerWindows[0].Handle;
        ShowWindow(minimizedHandle, ShowMinimize);
        var minimizedWindow = WaitForWindowState(service, minimizedHandle, SavedWindowState.MinimizedToTaskbar);
        TestAssert.True(minimizedWindow is not null, "Minimized Explorer window should remain capturable.");

        foreach (var window in explorerWindows)
        {
            PostMessage(window.Handle, WmClose, nint.Zero, nint.Zero);
        }
    }

    private static void TestMixerHiddenTrayState()
    {
        var mixerPath = Environment.GetEnvironmentVariable("WINDOWHOME_MIXER_TEST_EXE");
        if (string.IsNullOrWhiteSpace(mixerPath) || !File.Exists(mixerPath))
        {
            Console.WriteLine("Skipping mixer tray integration test. Set WINDOWHOME_MIXER_TEST_EXE to run it.");
            return;
        }

        var service = new NativeWindowService();
        var processName = Path.GetFileNameWithoutExtension(mixerPath);
        var before = service.GetOpenWindows()
            .Where(window => string.Equals(window.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
            .Select(window => window.Handle)
            .ToHashSet();

        using var process = Process.Start(new ProcessStartInfo { FileName = mixerPath, UseShellExecute = true });
        TestAssert.True(process is not null, "Mixer process should start.");
        if (process is null)
        {
            return;
        }

        var mixerWindows = WaitForWindows(service, processName, before, 1);
        TestAssert.True(mixerWindows.Count >= 1, "Mixer should expose a top-level window.");

        var handle = mixerWindows[0].Handle;
        ShowWindow(handle, ShowHide);
        var hiddenWindow = WaitForWindowState(service, handle, SavedWindowState.MinimizedToTray);
        TestAssert.True(hiddenWindow is not null, "Hidden mixer window should be capturable as minimized to tray.");

        PostMessage(handle, WmClose, nint.Zero, nint.Zero);
        if (!process.WaitForExit(2500) && !process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
    }

    private static List<WindowInfo> WaitForWindows(
        NativeWindowService service,
        string processName,
        HashSet<nint> excludedHandles,
        int count)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(12);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var windows = service.GetOpenWindows()
                .Where(window =>
                    !excludedHandles.Contains(window.Handle)
                    && string.Equals(window.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (windows.Count >= count)
            {
                return windows;
            }

            Thread.Sleep(300);
        }

        return [];
    }

    private static WindowInfo? WaitForWindowState(NativeWindowService service, nint handle, SavedWindowState state)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(8);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var window = service.GetWindowInfo(handle);
            if (window is not null && service.CaptureWindowState(window) == state)
            {
                return window;
            }

            Thread.Sleep(250);
        }

        return null;
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(nint hwnd, int message, nint wParam, nint lParam);
}
