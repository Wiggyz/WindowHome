namespace MonitorApp;

public static class WindowDisplay
{
    public static IReadOnlyList<WindowInfo> OrderForDropdown(IEnumerable<WindowInfo> windows)
    {
        return windows
            .OrderBy(window => window.ProcessName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(window => window.Title, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(window => window.ProcessId)
            .ToList();
    }

    public static IReadOnlyList<WindowInfo> FilterForDropdown(IEnumerable<WindowInfo> windows, string filter)
    {
        var ordered = OrderForDropdown(windows);
        if (string.IsNullOrWhiteSpace(filter))
        {
            return ordered;
        }

        var terms = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return ordered
            .Where(window => terms.All(term =>
                Contains(window.ProcessName, term)
                || Contains(window.Title, term)
                || Contains(window.ExecutablePath, term)
                || Contains(window.DisplayLabel, term)))
            .ToList();
    }

    private static bool Contains(string value, string term)
    {
        return value.Contains(term, StringComparison.CurrentCultureIgnoreCase);
    }
}
