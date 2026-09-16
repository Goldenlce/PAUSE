using System.Text;
using System.Text.RegularExpressions;

namespace LeiGodAutoPause;

public static class LeigodSessionReader
{
    private const int MaxTailBytes = 4 * 1024 * 1024;

    private static readonly Regex PauseUrlRegex = new(
        @"https://api[123]\.leigod\.com/client/(?:pause|recover)(?:/status)?\?[^\s""]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BodyTildeRegex = new(
        @"~\s*([A-Za-z0-9+/]+={0,2})\s*~",
        RegexOptions.Compiled);

    private static readonly Regex BodyDataRegex = new(
        @"""data""\s*:\s*""([A-Za-z0-9+/]+={0,2})""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DeviceIdRegex = new(
        @"(?:^|[?&])hardware_id=([^&""\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex NumberIdRegex = new(
        @"""nn_number""\s*:\s*(\d+)|X-User-Id[^\d]{0,20}(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static LeigodSession? TryReadLatest(out string message)
    {
        var installResources = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "LeiGod_Acc",
            "resources");

        if (!Directory.Exists(installResources))
        {
            message = "没有找到雷神加速器安装目录。";
            return null;
        }

        var files = EnumerateCandidateFiles(installResources)
            .OrderByDescending(SafeLastWriteTimeUtc)
            .Take(24)
            .ToList();

        if (files.Count == 0)
        {
            message = "没有找到雷神加速器日志文件。";
            return null;
        }

        LeigodSession? session = null;
        string? sessionSource = null;

        foreach (var file in files)
        {
            string content;
            try
            {
                content = ReadTail(file, MaxTailBytes);
            }
            catch
            {
                continue;
            }

            var lines = content.Split('\n');
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i];
                if (!line.Contains("/client/", StringComparison.OrdinalIgnoreCase) ||
                    !line.Contains("account_token=", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var urlMatch = PauseUrlRegex.Match(line);
                if (!urlMatch.Success || !Uri.TryCreate(urlMatch.Value, UriKind.Absolute, out var statusUri))
                {
                    continue;
                }

                var body = ExtractRequestBody(line);
                if (string.IsNullOrWhiteSpace(body))
                {
                    continue;
                }

                var deviceId = ExtractDeviceId(statusUri.Query) ?? "unknown-device";
                var userId = ExtractNumericId(content);

                session = new LeigodSession(statusUri, body, userId, deviceId);
                sessionSource = file;
                break;
            }

            if (session is not null)
            {
                break;
            }
        }

        if (session is null)
        {
            message = "没有在雷神日志中找到可读取的登录信息，请先启动并登录雷神加速器。";
            return null;
        }

        var sourceName = sessionSource is null ? "雷神日志" : Path.GetFileName(sessionSource);
        message = $"已从 {sourceName} 读取登录信息，账号 {session.MaskedAccountId}。";
        return session;
    }

    private static IEnumerable<string> EnumerateCandidateFiles(string resourcesDirectory)
    {
        var searchTargets = new (string Directory, string Pattern)[]
        {
            (Path.Combine(resourcesDirectory, "llogs"), "http-*.log"),
            (Path.Combine(resourcesDirectory, "llogs"), "log-*.log"),
            (Path.Combine(resourcesDirectory, "llogs"), "main-*.log"),
            (Path.Combine(resourcesDirectory, "leishenSdk", "log"), "*.log"),
            (Path.Combine(resourcesDirectory, "leishenSdk", "log"), "*.txt"),
            (Path.Combine(resourcesDirectory, "leishenSdk", "datacenter"), "*.json")
        };

        foreach (var (directory, pattern) in searchTargets)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            IEnumerable<string> matches;
            try
            {
                matches = Directory.EnumerateFiles(directory, pattern, SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            foreach (var match in matches)
            {
                yield return match;
            }
        }
    }

    private static string? ExtractRequestBody(string line)
    {
        var tildeMatch = BodyTildeRegex.Match(line);
        if (tildeMatch.Success)
        {
            return tildeMatch.Groups[1].Value;
        }

        var dataMatch = BodyDataRegex.Match(line);
        return dataMatch.Success ? dataMatch.Groups[1].Value : null;
    }

    private static string? ExtractDeviceId(string query)
    {
        var match = DeviceIdRegex.Match(query);
        return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : null;
    }

    private static string? ExtractNumericId(string content)
    {
        string? result = null;
        foreach (Match match in NumberIdRegex.Matches(content))
        {
            var value = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
            if (!string.IsNullOrWhiteSpace(value))
            {
                result = value;
            }
        }

        return result;
    }

    private static string ReadTail(string path, int maxBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var length = stream.Length;
        var start = Math.Max(0, length - maxBytes);
        stream.Seek(start, SeekOrigin.Begin);

        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static DateTime SafeLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }
}
