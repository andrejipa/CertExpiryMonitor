using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CertExpiryMonitor.Models;

namespace CertExpiryMonitor.Services;

public sealed class DiagnosticsBundleService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true
    };

    private readonly AppPaths _paths;
    private readonly FileLogger _logger;
    private readonly DiagnosticEventStore? _diagnosticEvents;
    private readonly Func<StartupRegistration.StartupStatus> _queryStartupStatus;
    private readonly Func<IReadOnlyList<CertificateSnapshot>> _readCertificates;

    public DiagnosticsBundleService(
        AppPaths paths,
        StartupRegistration startup,
        CertificateReader certificateReader,
        FileLogger logger,
        DiagnosticEventStore? diagnosticEvents = null)
        : this(paths, logger, startup.QueryStatus, certificateReader.ReadCurrentUserPersonalCertificates, diagnosticEvents)
    {
    }

    internal DiagnosticsBundleService(
        AppPaths paths,
        FileLogger logger,
        Func<StartupRegistration.StartupStatus> queryStartupStatus,
        Func<IReadOnlyList<CertificateSnapshot>> readCertificates,
        DiagnosticEventStore? diagnosticEvents = null)
    {
        _paths = paths;
        _logger = logger;
        _diagnosticEvents = diagnosticEvents;
        _queryStartupStatus = queryStartupStatus;
        _readCertificates = readCertificates;
    }

    public string CreateBundle(string destinationZipPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationZipPath);

        var destinationFullPath = Path.GetFullPath(destinationZipPath);
        if (IsInsideDirectory(destinationFullPath, _paths.RootDirectory))
        {
            throw new InvalidOperationException(
                "O pacote de diagnóstico deve ser salvo fora da pasta de dados do CertExpiryMonitor.");
        }

        var destinationDirectory = Path.GetDirectoryName(destinationFullPath);
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        var tempRoot = Path.Combine(_paths.RootDirectory, "diagnostics", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            WriteManifest(tempRoot);
            WriteStartupStatus(tempRoot);
            WriteCertificateSummary(tempRoot);
            WriteSettingsSnapshot(tempRoot);
            TryCopyOptionalFile(_paths.TelemetryPath, Path.Combine(tempRoot, "telemetry.json"), tempRoot);
            TryCopyDiagnosticsDatabase(tempRoot);
            CopyLogs(tempRoot);

            if (File.Exists(destinationFullPath))
            {
                File.Delete(destinationFullPath);
            }

            ZipFile.CreateFromDirectory(tempRoot, destinationFullPath, CompressionLevel.Optimal, includeBaseDirectory: false);
            _logger.Info($"Diagnostics bundle exported to {destinationFullPath}");
            _diagnosticEvents?.RecordInfo(
                "diagnostics.bundle_created",
                "DiagnosticsBundleService",
                "Pacote de diagnostico criado.",
                new { destination = destinationFullPath });
            return destinationFullPath;
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // best-effort cleanup; o pacote ja foi gerado ou a excecao original sera preservada.
            }
        }
    }

    private void WriteManifest(string tempRoot)
    {
        var assembly = Assembly.GetExecutingAssembly().GetName();
        var manifest = new
        {
            generatedAt = DateTimeOffset.Now,
            appVersion = assembly.Version?.ToString() ?? "unknown",
            executable = DiagnosticRedactor.RedactText(Environment.ProcessPath ?? Application.ExecutablePath),
            dataDirectory = DiagnosticRedactor.RedactText(_paths.RootDirectory),
            osVersion = Environment.OSVersion.VersionString,
            note = "Pacote local de diagnostico redigido do CertExpiryMonitor. Nao exporta chave privada, PFX, senha, usuario ou nome de maquina em texto puro."
        };

        File.WriteAllText(
            Path.Combine(tempRoot, "manifest.json"),
            JsonSerializer.Serialize(manifest, JsonOptions),
            Encoding.UTF8);
    }

    private void WriteStartupStatus(string tempRoot)
    {
        try
        {
            var status = _queryStartupStatus();
            var lines = new[]
            {
                $"TaskSchedulerRegistered: {status.TaskSchedulerRegistered}",
                $"TaskSchedulerMatchesCurrentExecutable: {status.TaskSchedulerMatchesCurrentExecutable}",
                $"TaskSchedulerCommand: {DiagnosticRedactor.RedactText(status.TaskSchedulerCommand ?? "(ausente)")}",
                $"RegistryRegistered: {status.RegistryRegistered}",
                $"RegistryMatchesCurrentExecutable: {status.RegistryMatchesCurrentExecutable}",
                $"RegistryCommand: {DiagnosticRedactor.RedactText(status.RegistryCommand ?? "(ausente)")}",
                $"ResolvedExecutablePath: {DiagnosticRedactor.RedactText(status.ResolvedExecutablePath)}"
            };

            File.WriteAllLines(Path.Combine(tempRoot, "startup-status.txt"), lines, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                Path.Combine(tempRoot, "startup-status.txt"),
                $"Falha ao consultar startup: {DiagnosticRedactor.RedactText(ex.Message)}",
                Encoding.UTF8);
        }
    }

    private void WriteCertificateSummary(string tempRoot)
    {
        try
        {
            var now = DateTime.Today;
            var lines = new List<string>
            {
                "thumbprint_hash,serial_prefix,not_after,days_remaining,holder_redacted,document_last4"
            };

            foreach (var cert in _readCertificates().OrderBy(c => c.NotAfter).ThenBy(c => c.Thumbprint, StringComparer.OrdinalIgnoreCase))
            {
                var commonName = string.IsNullOrWhiteSpace(cert.SimpleName)
                    ? CertificateDocumentHelpers.GetCommonNameFallback(cert.Subject)
                    : cert.SimpleName;
                var (_, document) = CertificateDocumentHelpers.ParseHolder(commonName);
                var documentLast4 = LastDigits(document, 4);

                lines.Add(string.Join(
                    ",",
                    Csv(DiagnosticRedactor.HashThumbprint(cert.Thumbprint)),
                    Csv(cert.SerialNumber.Length <= 8 ? cert.SerialNumber : cert.SerialNumber[..8]),
                    Csv(cert.NotAfter.ToString("O")),
                    Csv(((int)(cert.NotAfter.Date - now).TotalDays).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    Csv("(redacted)"),
                    Csv(documentLast4)));
            }

            File.WriteAllLines(Path.Combine(tempRoot, "certificate-summary.csv"), lines, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                Path.Combine(tempRoot, "certificate-summary.csv"),
                $"Falha ao ler certificados: {DiagnosticRedactor.RedactText(ex.Message)}",
                Encoding.UTF8);
        }
    }

    private void WriteSettingsSnapshot(string tempRoot)
    {
        if (!File.Exists(_paths.SettingsPath))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_paths.SettingsPath));
            using var stream = File.Create(Path.Combine(tempRoot, "settings.snapshot.json"));
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            WriteRedactedJson(document.RootElement, writer);
        }
        catch (Exception ex)
        {
            File.WriteAllText(
                Path.Combine(tempRoot, "settings.snapshot.txt"),
                $"Falha ao copiar settings: {DiagnosticRedactor.RedactText(ex.Message)}",
                Encoding.UTF8);
        }
    }

    private static void WriteRedactedJson(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    if (property.Name.Contains("hash", StringComparison.OrdinalIgnoreCase))
                    {
                        writer.WriteStringValue("(redacted)");
                    }
                    else
                    {
                        WriteRedactedJson(property.Value, writer);
                    }
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteRedactedJson(item, writer);
                }
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }

    private void CopyLogs(string tempRoot)
    {
        var logsDir = Path.Combine(tempRoot, "logs");
        Directory.CreateDirectory(logsDir);
        var copyErrors = new List<string>();

        foreach (var file in Directory.GetFiles(_paths.RootDirectory, "monitor.log*"))
        {
            try
            {
                CopyRedactedLog(file, Path.Combine(logsDir, Path.GetFileName(file)));
            }
            catch (Exception ex)
            {
                copyErrors.Add($"{Path.GetFileName(file)}: {DiagnosticRedactor.RedactText(ex.Message)}");
                _logger.Error(ex, $"Failed to copy diagnostic log {Path.GetFileName(file)}");
            }
        }

        if (copyErrors.Count > 0)
        {
            File.WriteAllLines(Path.Combine(logsDir, "copy-errors.txt"), copyErrors, Encoding.UTF8);
        }
    }

    private void TryCopyOptionalFile(string source, string destination, string tempRoot)
    {
        try
        {
            CopyIfExists(source, destination);
        }
        catch (Exception ex)
        {
            AppendCopyError(tempRoot, Path.GetFileName(source), DiagnosticRedactor.RedactText(ex.Message));
            _logger.Error(ex, $"Failed to copy diagnostic file {Path.GetFileName(source)}");
        }
    }

    private void TryCopyDiagnosticsDatabase(string tempRoot)
    {
        if (!File.Exists(_paths.DiagnosticsDbPath))
        {
            return;
        }

        var destination = Path.Combine(tempRoot, "diagnostics.db");
        if (_diagnosticEvents is not null)
        {
            if (!_diagnosticEvents.CopyDatabaseSnapshot(destination))
            {
                AppendCopyError(tempRoot, "diagnostics.db", "Falha ao copiar snapshot SQLite.");
            }

            return;
        }

        TryCopyOptionalFile(_paths.DiagnosticsDbPath, destination, tempRoot);
    }

    private static void AppendCopyError(string tempRoot, string fileName, string message)
    {
        var diagnosticsPath = Path.Combine(tempRoot, "copy-errors.txt");
        File.AppendAllText(
            diagnosticsPath,
            $"{fileName}: {message}{Environment.NewLine}",
            Encoding.UTF8);
    }

    private static void CopyIfExists(string source, string destination)
    {
        if (!File.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
    }

    private static void CopyRedactedLog(string source, string destination)
    {
        if (!File.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var content = File.ReadAllText(source, Encoding.UTF8);
        File.WriteAllText(destination, DiagnosticRedactor.RedactText(content), Encoding.UTF8);
    }

    private static bool IsInsideDirectory(string path, string directory)
    {
        var fullPath = Path.GetFullPath(path);
        var fullDirectory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var directoryWithSeparator = fullDirectory + Path.DirectorySeparatorChar;

        return fullPath.Equals(fullDirectory, StringComparison.OrdinalIgnoreCase) ||
            fullPath.StartsWith(directoryWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    private static string LastDigits(string value, int count)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length == 0) return string.Empty;
        return digits.Length <= count ? digits : digits[^count..];
    }

    private static string Csv(string value)
    {
        var escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }
}
