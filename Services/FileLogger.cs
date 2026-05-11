using System.Diagnostics;
using System.Text.Json;
using CertExpiryMonitor.Models;

namespace CertExpiryMonitor.Services;

public sealed class FileLogger
{
    private const long MaxLogBytes = 1_048_576;
    private const string EventLogSource = "CertExpiryMonitor";
    private const string EventLogName   = "Application";

    private readonly AppPaths _paths;
    private readonly object _gate = new();

    /// <summary>Formato dos logs (Text ou Json). Atualizado dinamicamente quando settings mudam.</summary>
    public LogFormat Format { get; set; } = LogFormat.Text;

    /// <summary>Espelha ERROR para o Windows Event Log. Atualizado dinamicamente.</summary>
    public bool EventLogEnabled { get; set; } = false;

    public FileLogger(AppPaths paths)
    {
        _paths = paths;
    }

    /// <summary>Aplica preferencias do usuario (formato + Event Log).</summary>
    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Format          = settings.LogFormat;
        EventLogEnabled = settings.EventLogEnabled;
    }

    public void Info(string message) => Write("INFO", message, exception: null);

    public void Notification(string message) => Write("NOTIFICATION", message, exception: null);

    public void Error(Exception exception, string message)
    {
        Write("ERROR", message, exception);
        if (EventLogEnabled) WriteToEventLog(message, exception);
    }

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (_gate)
            {
                // Rotacao nao-fatal: se falhar (arquivo bloqueado por antivirus, etc.),
                // ainda tentamos persistir o log — o arquivo apenas cresce alem do limite.
                try { RotateIfNeeded(); } catch { /* preferimos perder o cap a perder o log */ }

                var line = Format == LogFormat.Json
                    ? FormatJson(level, message, exception)
                    : FormatText(level, message, exception);

                File.AppendAllText(_paths.LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never break the monitor.
        }
    }

    private static string FormatText(string level, string message, Exception? exception)
    {
        var body = exception is null ? message : $"{message}:{Environment.NewLine}{exception}";
        return $"{DateTimeOffset.Now:O} [{level}] {body}";
    }

    private static string FormatJson(string level, string message, Exception? exception)
    {
        // Linha JSON unica (JSONL — facil de ingerir em SIEM).
        // Campos minimos: ts (ISO-8601), level, message, exception (opcional).
        using var ms = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("ts", DateTimeOffset.Now);
            writer.WriteString("level", level);
            writer.WriteString("message", message);
            if (exception is not null)
            {
                writer.WriteString("exceptionType", exception.GetType().FullName ?? "");
                writer.WriteString("exceptionMessage", exception.Message);
                writer.WriteString("stackTrace", exception.StackTrace ?? "");
            }
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Espelha o evento para o Windows Event Log. Operacao best-effort: se a source nao
    /// existir e nao houver permissao para cria-la (sem admin), apenas ignora silenciosamente.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void WriteToEventLog(string message, Exception exception)
    {
        try
        {
            // EventLog.SourceExists/CreateEventSource exigem admin para CRIAR a source.
            // Em ambiente corporativo o admin pode pre-criar via PowerShell (instalador).
            // Se a source nao existir e nao pudermos cria-la, usamos a source padrao "Application"
            // que sempre existe.
            string sourceToUse;
            try
            {
                if (EventLog.SourceExists(EventLogSource))
                {
                    sourceToUse = EventLogSource;
                }
                else
                {
                    // Tentativa de criar (so funciona se elevado)
                    try
                    {
                        EventLog.CreateEventSource(new EventSourceCreationData(EventLogSource, EventLogName));
                        sourceToUse = EventLogSource;
                    }
                    catch
                    {
                        sourceToUse = "Application";
                    }
                }
            }
            catch
            {
                sourceToUse = "Application";
            }

            var body = $"[CertExpiryMonitor] {message}{Environment.NewLine}{exception}";
            EventLog.WriteEntry(sourceToUse, body, EventLogEntryType.Error, eventID: 1000);
        }
        catch
        {
            // Event Log e best-effort; nao quebra o app se falhar.
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
