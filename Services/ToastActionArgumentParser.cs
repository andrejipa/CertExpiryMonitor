namespace CertExpiryMonitor.Services;

internal static class ToastActionArgumentParser
{
    internal static Dictionary<string, string> Parse(string? arguments)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(arguments)) return values;

        foreach (var part in arguments.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = part.Split('=', 2);
            var key = pieces.Length == 2 ? SafeUnescape(pieces[0]).Trim() : string.Empty;
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            values[key] = SafeUnescape(pieces[1]);
        }

        return values;
    }

    private static string SafeUnescape(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }
}
