namespace LeiGodAutoPause;

public sealed record LeigodSession(
    Uri StatusUri,
    string RequestBody,
    string? UserId,
    string DeviceId)
{
    public IEnumerable<Uri> BuildCandidateUris(string relativePath)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var original = BuildUri(StatusUri.Host, relativePath);
        if (seen.Add(original.AbsoluteUri))
        {
            yield return original;
        }

        foreach (var host in new[] { "api2.leigod.com", "api1.leigod.com", "api3.leigod.com" })
        {
            var candidate = BuildUri(host, relativePath);
            if (seen.Add(candidate.AbsoluteUri))
            {
                yield return candidate;
            }
        }
    }

    public Uri BuildUri(string host, string relativePath)
    {
        var builder = new UriBuilder(StatusUri)
        {
            Host = host,
            Path = relativePath.StartsWith('/') ? relativePath : "/" + relativePath
        };

        return builder.Uri;
    }

    public string MaskedAccountId => Mask(UserId);

    public static string Mask(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "未知账号";
        }

        if (value.Length <= 4)
        {
            return new string('*', value.Length);
        }

        return $"{value[..2]}***{value[^2..]}";
    }
}
