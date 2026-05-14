using System.IO;

namespace MonitorApp;

public static class RuleAutomation
{
    public static IReadOnlyList<AppRule> GetLaunchableAutoStartRules(AppSettings settings)
    {
        return GetLaunchableAutoStartRules(settings, []);
    }

    public static IReadOnlyList<AppRule> GetLaunchableAutoStartRules(
        AppSettings settings,
        IEnumerable<RunningProcessInfo> runningProcesses)
    {
        var running = runningProcesses.ToList();
        return settings.Rules
            .Where(rule =>
                rule.Enabled
                && rule.AutoStart
                && !string.IsNullOrWhiteSpace(rule.ExecutablePath)
                && !IsAlreadyRunning(rule, running))
            .ToList();
    }

    public static IReadOnlySet<Guid> GetAlreadyRunningRuleIds(
        AppSettings settings,
        IEnumerable<RunningProcessInfo> runningProcesses)
    {
        var running = runningProcesses.ToList();
        return settings.Rules
            .Where(rule => rule.Enabled && IsAlreadyRunning(rule, running))
            .Select(rule => rule.Id)
            .ToHashSet();
    }

    public static IReadOnlySet<Guid> GetStoppedSuppressedRuleIds(
        AppSettings settings,
        IEnumerable<RunningProcessInfo> runningProcesses,
        IEnumerable<Guid> suppressedRuleIds)
    {
        var running = runningProcesses.ToList();
        var suppressed = suppressedRuleIds.ToHashSet();
        return settings.Rules
            .Where(rule => suppressed.Contains(rule.Id) && !IsAlreadyRunning(rule, running))
            .Select(rule => rule.Id)
            .ToHashSet();
    }

    public static bool IsAlreadyRunning(AppRule rule, IEnumerable<RunningProcessInfo> runningProcesses)
    {
        var processNames = new[]
        {
            rule.ProcessName,
            Path.GetFileNameWithoutExtension(rule.ExecutablePath)
        }
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(NormalizeProcessName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return runningProcesses.Any(process =>
            (!string.IsNullOrWhiteSpace(rule.ExecutablePath)
                && !string.IsNullOrWhiteSpace(process.ExecutablePath)
                && string.Equals(rule.ExecutablePath, process.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            || processNames.Any(ruleProcessName =>
                string.Equals(ruleProcessName, NormalizeProcessName(process.ProcessName), StringComparison.OrdinalIgnoreCase)));
    }

    public static IReadOnlyList<RuleWindowAssignment> MatchRulesToWindows(
        IEnumerable<AppRule> rules,
        IEnumerable<WindowInfo> windows,
        Func<WindowInfo, AppRule, bool> matches,
        ISet<nint>? skippedWindowHandles = null)
    {
        var availableWindows = windows.ToList();
        var usedHandles = new HashSet<nint>();
        var assignments = new List<RuleWindowAssignment>();

        foreach (var rule in rules.Where(rule => rule.Enabled))
        {
            var window = availableWindows.FirstOrDefault(candidate =>
                !usedHandles.Contains(candidate.Handle)
                && skippedWindowHandles?.Contains(candidate.Handle) != true
                && matches(candidate, rule));
            if (window is null)
            {
                continue;
            }

            usedHandles.Add(window.Handle);
            assignments.Add(new RuleWindowAssignment(rule, window));
        }

        return assignments;
    }

    private static string NormalizeProcessName(string processName)
    {
        return Path.GetFileNameWithoutExtension(processName.Trim());
    }
}

public sealed record RunningProcessInfo(string ProcessName, string ExecutablePath);
