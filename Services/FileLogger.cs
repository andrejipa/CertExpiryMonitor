namespace CertExpiryMonitor.Services;

public sealed class FileLogger
{
    private const long MaxLogBytes = 1_048_576;
    private readonly AppPaths _paths;
    private readonly object _gate = new();

    public FileLogger(AppPaths paths)
    {
        _paths = paths;
    }

    public void Info(string message) => Write("INFO", message);

    public void Notification(string message) => Write("NOTIFICATION", message);

    public void Error(Exception exception, string message)
    {
        Write("ERROR", $"{message}:{Environment.NewLine}{exception}");
    }

    private void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                // Rotacao nao-fatal: se falhar (arquivo bloqueado por antivirus, etc.),
                // ainda tentamos persistir o log — o arquivo apenas cresce alem do limite.
                try { RotateIfNeeded(); } catch { /* preferimos perder o cap a perder o log */ }

                File.AppendAllText(
                    _paths.LogPath,
                    $"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the monitor.
        }
    }

    private const int MaxBackups = 3;

    private void RotateIfNeeded()
    {
        var logFile = new FileInfo(_paths.LogPath);
        if (!logFile.Exists || logFile.Length < MaxLogBytes)
        {
            return;
        }

        // Rotacao em cascata: monitor.log.3 apagado, .2→.3, .1→.2, monitor.log→.1
        for (var i = MaxBackups; i >= 1; i--)
        {
            var src  = i == 1
                ? _paths.LogPath
                : Path.Combine(_paths.RootDirectory, $"monitor.log.{i - 1}");
            var dest = Path.Combine(_paths.RootDirectory, $"monitor.log.{i}");

            if (!File.Exists(src)) continue;
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(src, dest);
        }
    }
}
