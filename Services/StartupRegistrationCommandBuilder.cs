namespace CertExpiryMonitor.Services;

internal static class StartupRegistrationCommandBuilder
{
    internal static string BuildCreateTaskArguments(string executablePath)
    {
        ValidateExecutablePath(executablePath);

        return string.Join(" ",
            "/create",
            "/tn \"CertExpiryMonitor\"",
            $"/tr \"\\\"{executablePath}\\\" --background\"",
            "/sc ONLOGON",
            "/rl LIMITED",
            "/f");
    }

    internal static string BuildRegistryCommand(string executablePath)
    {
        ValidateExecutablePath(executablePath);

        return $"\"{executablePath}\" --background";
    }

    internal static string? ExtractTaskSchedulerCommand(string stdout)
    {
        if (string.IsNullOrWhiteSpace(stdout)) return null;

        var lines = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return null;

        for (var i = 0; i < lines.Length - 1; i++)
        {
            var headers = SplitCsvLine(lines[i]);
            var values  = SplitCsvLine(lines[i + 1]);
            var index = headers.FindIndex(header => string.Equals(header, "Task To Run", StringComparison.OrdinalIgnoreCase));
            if (index >= 0 && index < values.Count)
            {
                return values[index];
            }
        }

        // Em Windows localizado, o nome da coluna pode variar. Nesse caso,
        // ainda precisamos devolver o campo de comando, nao a linha CSV inteira,
        // porque a validacao posterior compara o executavel de forma exata.
        foreach (var value in SplitCsvLine(lines[^1]))
        {
            if (value.Contains(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    internal static bool IsCommandForExecutable(string? command, string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (string.IsNullOrWhiteSpace(command)) return false;

        var trimmed = command.Trim();
        var normalizedExecutable = Path.GetFullPath(executablePath);
        if (trimmed.StartsWith('"'))
        {
            var closingQuote = trimmed.IndexOf('"', 1);
            if (closingQuote < 0) return false;

            var executable = trimmed[1..closingQuote];
            return PathsEqual(executable, normalizedExecutable) &&
                IsCommandBoundary(trimmed, closingQuote + 1);
        }

        var firstSpace = trimmed.IndexOf(' ');
        var candidate = firstSpace < 0 ? trimmed : trimmed[..firstSpace];
        return PathsEqual(candidate, normalizedExecutable);
    }

    private static List<string> SplitCsvLine(string line)
    {
        var values = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
                continue;
            }

            if (c == ',' && !inQuotes)
            {
                values.Add(current.ToString());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        values.Add(current.ToString());
        return values;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return Path.GetFullPath(left).Equals(Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return left.Equals(right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsCommandBoundary(string command, int index)
    {
        return index >= command.Length || char.IsWhiteSpace(command[index]);
    }

    private static void ValidateExecutablePath(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        if (executablePath.Any(c => c == '"' || char.IsControl(c)))
        {
            throw new ArgumentException("Executable path contains invalid command-line characters.", nameof(executablePath));
        }
    }
}
