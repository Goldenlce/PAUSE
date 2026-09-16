using Microsoft.Win32;

namespace LeiGodAutoPause;

public static class StartupManager
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LeiGodAutoPause";

    public static bool IsEnabled(string executablePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var rawValue = key?.GetValue(ValueName) as string;
            var registeredPath = ExtractExecutablePath(rawValue);
            return !string.IsNullOrWhiteSpace(registeredPath) &&
                   PathsEqual(registeredPath, executablePath);
        }
        catch
        {
            return false;
        }
    }

    public static bool Apply(bool enabled, string executablePath, out string message)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                message = "无法打开 Windows 自启动设置。";
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                message = "已关闭开机自动启动。";
                return true;
            }

            var command = $"\"{executablePath}\" --startup";
            key.SetValue(ValueName, command, RegistryValueKind.String);
            message = "已开启开机自动启动，登录 Windows 后会缩到托盘运行。";
            return true;
        }
        catch (Exception ex)
        {
            message = "设置开机自动启动失败：" + ex.Message;
            return false;
        }
    }

    private static string? ExtractExecutablePath(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var value = commandLine.Trim();
        if (value.StartsWith('"'))
        {
            var closingQuote = value.IndexOf('"', 1);
            return closingQuote > 1 ? value[1..closingQuote] : null;
        }

        var firstSpace = value.IndexOf(' ');
        var candidate = firstSpace > 0 ? value[..firstSpace] : value;
        return File.Exists(candidate) ? candidate : value;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
