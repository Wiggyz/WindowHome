using System.Diagnostics;

namespace MonitorApp;

public static class ElevatedStartupTask
{
    public const string TaskName = "WindowHome Elevated Startup";

    public static string BuildCreateArguments(string executablePath, bool startMinimizedToTray)
    {
        var command = startMinimizedToTray
            ? $"\\\"{executablePath}\\\" --minimized-to-tray"
            : $"\\\"{executablePath}\\\"";
        return $"/Create /F /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /IT /TR \"{command}\"";
    }

    public static string BuildDeleteArguments()
    {
        return $"/Delete /F /TN \"{TaskName}\"";
    }

    public static string BuildQueryArguments()
    {
        return $"/Query /TN \"{TaskName}\"";
    }

    public static bool IsEnabled()
    {
        var result = RunSchtasks(BuildQueryArguments());
        return result.ExitCode == 0;
    }

    public static void SetEnabled(bool enabled, bool startMinimizedToTray)
    {
        if (!enabled)
        {
            var deleteResult = RunSchtasks(BuildDeleteArguments());
            if (deleteResult.ExitCode != 0 && !deleteResult.Output.Contains("cannot find", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(deleteResult.Output);
            }

            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Cannot resolve app path.");
        }

        var createResult = RunSchtasks(BuildCreateArguments(executablePath, startMinimizedToTray));
        if (createResult.ExitCode != 0)
        {
            throw new InvalidOperationException(createResult.Output);
        }
    }

    private static CommandResult RunSchtasks(string arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start schtasks.exe.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new CommandResult(process.ExitCode, $"{output}{error}".Trim());
    }

    private sealed record CommandResult(int ExitCode, string Output);
}
