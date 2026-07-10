using System.IO.Compression;
using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class DiagnosticsBundleServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppPaths _paths;
    private readonly FileLogger _logger;
    private readonly List<string> _externalArtifacts = [];
    private readonly List<string> _externalDirectories = [];

    public DiagnosticsBundleServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"DiagnosticsBundleTests_{Guid.NewGuid():N}");
        _paths = new AppPaths(_tempDir);
        _logger = new FileLogger(_paths);
    }

    public void Dispose()
    {
        foreach (var path in _externalArtifacts)
        {
            try { File.Delete(path); } catch { /* best-effort */ }
        }

        foreach (var path in _externalDirectories)
        {
            try { Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
        }

        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void CreateBundleIncludesUsefulFilesAndRedactsSensitiveCertificateData()
    {
        File.WriteAllText(_paths.LogPath, "2026-05-12T10:00:00 [INFO] User opened settings window.");
        File.WriteAllText(_paths.SettingsPath, """{"version":1,"settings":{"LastCertificateSnapshotHash":"SECRET_HASH","DailyCheckTime":"09:00:00"}}""");
        File.WriteAllText(_paths.TelemetryPath, """{"Version":1,"TotalChecks":3}""");
        var diagnosticEvents = new DiagnosticEventStore(_paths, _logger);
        Assert.True(diagnosticEvents.RecordInfo("diagnostics.test", "tests", "Evento para pacote."));
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => new StartupRegistration.StartupStatus(
                TaskSchedulerRegistered: true,
                TaskSchedulerCommand: "\"app.exe\" --background",
                RegistryRegistered: false,
                RegistryCommand: null,
                ResolvedExecutablePath: "app.exe"),
            () =>
            [
                new CertificateSnapshot(
                    Thumbprint: "AABBCCDDEEFF0011223344556677889900AABBCC",
                    Subject: "CN=EMPRESA TESTE:12345678901",
                    Issuer: "CN=Issuer",
                    NotAfter: new DateTime(2026, 6, 30),
                    SerialNumber: "001122334455",
                    SimpleName: "EMPRESA TESTE:12345678901")
            ],
            diagnosticEvents);
        var zipPath = NewExternalZipPath("diagnostics.zip");

        service.CreateBundle(zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries.Select(entry => entry.FullName).ToArray();
        Assert.Contains("manifest.json", entries);
        Assert.Contains("startup-status.txt", entries);
        Assert.Contains("settings.snapshot.json", entries);
        Assert.Contains("telemetry.json", entries);
        Assert.Contains("certificate-summary.csv", entries);
        Assert.Contains("diagnostics.db", entries);
        Assert.Contains("logs/monitor.log", entries);

        var startupStatus = ReadEntry(archive, "startup-status.txt");
        Assert.Contains("TaskSchedulerMatchesCurrentExecutable: True", startupStatus, StringComparison.Ordinal);
        Assert.Contains("RegistryMatchesCurrentExecutable: False", startupStatus, StringComparison.Ordinal);

        var certSummary = ReadEntry(archive, "certificate-summary.csv");
        Assert.Contains(DiagnosticRedactor.HashThumbprint("AABBCCDDEEFF0011223344556677889900AABBCC"), certSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("AABBCCDD", certSummary, StringComparison.Ordinal);
        Assert.Contains("8901", certSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("12345678901", certSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("EMPRESA TESTE", certSummary, StringComparison.Ordinal);

        var settings = ReadEntry(archive, "settings.snapshot.json");
        Assert.Contains("(redacted)", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_HASH", settings, StringComparison.Ordinal);

        var diagnosticsDbCopy = ExtractEntryToExternalFile(archive, "diagnostics.db");
        using var connection = new SqliteConnection($"Data Source={diagnosticsDbCopy}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from events where event_type = 'diagnostics.test';";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public void CreateBundleWritesManifestWithExpectedPrivacyContract()
    {
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => new StartupRegistration.StartupStatus(false, null, false, null, "app.exe"),
            () => []);
        var zipPath = NewExternalZipPath("diagnostics-manifest.zip");

        service.CreateBundle(zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        var manifest = ReadEntry(archive, "manifest.json");

        Assert.Contains("\"appVersion\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"executable\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"dataDirectory\"", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("\"machineHash\"", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("\"userHash\"", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("\"machineName\"", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain("\"userName\"", manifest, StringComparison.Ordinal);
        Assert.DoesNotContain(Environment.MachineName, manifest, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, manifest, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nao exporta chave privada, PFX, senha, usuario ou nome de maquina em texto puro", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateBundleOverwritesExistingDestinationZip()
    {
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => new StartupRegistration.StartupStatus(false, null, false, null, "app.exe"),
            () => []);
        var zipPath = NewExternalZipPath("diagnostics-overwrite.zip");
        File.WriteAllText(zipPath, "arquivo antigo");

        service.CreateBundle(zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        Assert.Contains("manifest.json", archive.Entries.Select(entry => entry.FullName));
    }

    [Fact]
    public void CreateBundleCreatesMissingDestinationDirectoryAndCleansTemporaryDirectory()
    {
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => new StartupRegistration.StartupStatus(false, null, false, null, "app.exe"),
            () => []);
        var zipPath = NewExternalZipPathInDirectory("missing-parent", "diagnostics.zip");

        service.CreateBundle(zipPath);

        Assert.True(File.Exists(zipPath));
        var diagnosticsRoot = Path.Combine(_paths.RootDirectory, "diagnostics");
        Assert.False(Directory.Exists(diagnosticsRoot) && Directory.EnumerateFileSystemEntries(diagnosticsRoot).Any());
    }

    [Fact]
    public void CreateBundleReportsStartupCommandMismatch()
    {
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => new StartupRegistration.StartupStatus(
                TaskSchedulerRegistered: true,
                TaskSchedulerCommand: "\"C:\\Users\\Antigo\\AppData\\Local\\Programs\\CertExpiryMonitor\\CertExpiryMonitor.exe\" --background",
                RegistryRegistered: true,
                RegistryCommand: "\"C:\\Users\\Fulano\\AppData\\Local\\Programs\\CertExpiryMonitor\\CertExpiryMonitor.exe\" --background",
                ResolvedExecutablePath: "C:\\Users\\Fulano\\AppData\\Local\\Programs\\CertExpiryMonitor\\CertExpiryMonitor.exe"),
            () => []);
        var zipPath = NewExternalZipPath("diagnostics-startup-mismatch.zip");

        service.CreateBundle(zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        var startupStatus = ReadEntry(archive, "startup-status.txt");

        Assert.Contains("TaskSchedulerRegistered: True", startupStatus, StringComparison.Ordinal);
        Assert.Contains("TaskSchedulerMatchesCurrentExecutable: False", startupStatus, StringComparison.Ordinal);
        Assert.Contains("RegistryRegistered: True", startupStatus, StringComparison.Ordinal);
        Assert.Contains("RegistryMatchesCurrentExecutable: True", startupStatus, StringComparison.Ordinal);
        Assert.Contains("C:\\Users\\(redacted)\\AppData\\Local\\Programs\\CertExpiryMonitor\\CertExpiryMonitor.exe", startupStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("Antigo", startupStatus, StringComparison.Ordinal);
        Assert.DoesNotContain("Fulano", startupStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateBundleWritesStartupFailureWhenQueryThrows()
    {
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => throw new InvalidOperationException("schtasks indisponivel"),
            () => []);
        var zipPath = NewExternalZipPath("diagnostics-startup-failure.zip");

        service.CreateBundle(zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        var startupStatus = ReadEntry(archive, "startup-status.txt");
        Assert.Contains("Falha ao consultar startup", startupStatus, StringComparison.Ordinal);
        Assert.Contains("schtasks indisponivel", startupStatus, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateBundleWritesCertificateFailureWhenReaderThrows()
    {
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => new StartupRegistration.StartupStatus(false, null, false, null, "app.exe"),
            () => throw new InvalidOperationException("store indisponivel"));
        var zipPath = NewExternalZipPath("diagnostics-cert-failure.zip");

        service.CreateBundle(zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        var certificateSummary = ReadEntry(archive, "certificate-summary.csv");
        Assert.Contains("Falha ao ler certificados", certificateSummary, StringComparison.Ordinal);
        Assert.Contains("store indisponivel", certificateSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateBundleRecordsPartialCertificateReadStatusAndKeepsSuccessfulRows()
    {
        var certificate = new CertificateSnapshot(
            "AA00000000000000000000000000000000000001",
            "CN=Teste",
            "CN=Issuer",
            DateTime.Today.AddDays(5),
            "12345678",
            "Teste");
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => new StartupRegistration.StartupStatus(false, null, false, null, "app.exe"),
            new CertificateReadResult([certificate], CertificateReadStatus.PartialFailure, 1));
        var zipPath = NewExternalZipPath("partial-read.zip");

        service.CreateBundle(zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        var status = ReadEntry(archive, "certificate-read-status.txt");
        var summary = ReadEntry(archive, "certificate-summary.csv");
        Assert.Contains("PartialFailure", status, StringComparison.Ordinal);
        Assert.Contains(DiagnosticRedactor.HashThumbprint(certificate.Thumbprint), summary, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateBundleOrdersCertificateSummaryUsesFallbackAndEscapesCsv()
    {
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => new StartupRegistration.StartupStatus(false, null, false, null, "app.exe"),
            () =>
            [
                new CertificateSnapshot(
                    Thumbprint: "AA\"BB,CCDDEE0011223344556677889900AABBCC",
                    Subject: "CN=EMPRESA\\, FILIAL:12345678901, O=Teste",
                    Issuer: "CN=Issuer",
                    NotAfter: new DateTime(2026, 6, 1),
                    SerialNumber: "S\"R,123456789",
                    SimpleName: ""),
                new CertificateSnapshot(
                    Thumbprint: "BB0000000011223344556677889900AABBCCDDEE",
                    Subject: "CN=SEM DOCUMENTO",
                    Issuer: "CN=Issuer",
                    NotAfter: new DateTime(2026, 6, 1),
                    SerialNumber: "SERIALB",
                    SimpleName: "SEM DOCUMENTO"),
                new CertificateSnapshot(
                    Thumbprint: "SHORT7",
                    Subject: "CN=CURTO:33333333333",
                    Issuer: "CN=Issuer",
                    NotAfter: new DateTime(2026, 7, 1),
                    SerialNumber: "SER7",
                    SimpleName: ""),
                new CertificateSnapshot(
                    Thumbprint: "ZZZZZZZZ99999999999999999999999999999999",
                    Subject: "CN=POSTERIOR_SUBJECT:22222222222",
                    Issuer: "CN=Issuer",
                    NotAfter: new DateTime(2026, 8, 1),
                    SerialNumber: "SERIALPOSTERIOR",
                    SimpleName: "POSTERIOR_SIMPLE:11111111111")
            ]);
        var zipPath = NewExternalZipPath("diagnostics-certs.csv.zip");

        service.CreateBundle(zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        var lines = ReadEntry(archive, "certificate-summary.csv")
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("thumbprint_hash,serial_prefix,not_after,days_remaining,holder_redacted,document_last4", lines[0]);
        Assert.Contains(DiagnosticRedactor.HashThumbprint("AA\"BB,CCDDEE0011223344556677889900AABBCC"), lines[1], StringComparison.Ordinal);
        Assert.DoesNotContain("\"AA\"\"BB,CC\"", lines[1], StringComparison.Ordinal);
        Assert.Contains("\"S\"\"R,1234\"", lines[1], StringComparison.Ordinal);
        Assert.EndsWith("\"8901\"", lines[1], StringComparison.Ordinal);
        Assert.Contains(DiagnosticRedactor.HashThumbprint("BB0000000011223344556677889900AABBCCDDEE"), lines[2], StringComparison.Ordinal);
        Assert.DoesNotContain("\"BB000000\"", lines[2], StringComparison.Ordinal);
        Assert.EndsWith("\"\"", lines[2], StringComparison.Ordinal);
        Assert.Contains(DiagnosticRedactor.HashThumbprint("SHORT7"), lines[3], StringComparison.Ordinal);
        Assert.DoesNotContain("\"SHORT7\"", lines[3], StringComparison.Ordinal);
        Assert.Contains("\"SER7\"", lines[3], StringComparison.Ordinal);
        Assert.EndsWith("\"3333\"", lines[3], StringComparison.Ordinal);
        Assert.Contains(DiagnosticRedactor.HashThumbprint("ZZZZZZZZ99999999999999999999999999999999"), lines[4], StringComparison.Ordinal);
        Assert.DoesNotContain("\"ZZZZZZZZ\"", lines[4], StringComparison.Ordinal);
        Assert.EndsWith("\"1111\"", lines[4], StringComparison.Ordinal);
        Assert.DoesNotContain("12345678901", string.Join("\n", lines), StringComparison.Ordinal);
        Assert.DoesNotContain("22222222222", string.Join("\n", lines), StringComparison.Ordinal);
        Assert.DoesNotContain("EMPRESA", string.Join("\n", lines), StringComparison.Ordinal);
    }

    [Fact]
    public void CreateBundleRedactsSensitivePatternsFromCopiedLogs()
    {
        File.WriteAllText(
            _paths.LogPath,
            "thumb=AABBCCDDEEFF0011223344556677889900AABBCC cpf=12345678901 senha=segredo C:\\temp\\certificado.pfx");
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => new StartupRegistration.StartupStatus(false, null, false, null, "app.exe"),
            () => []);
        var zipPath = NewExternalZipPath("diagnostics-redacted-logs.zip");

        service.CreateBundle(zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        var log = ReadEntry(archive, "logs/monitor.log");
        Assert.Contains("thumb=(redacted-thumbprint)", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("document_last4:8901", log, StringComparison.Ordinal);
        Assert.Contains("senha=(redacted)", log, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("(redacted-pfx)", log, StringComparison.Ordinal);
        Assert.DoesNotContain("AABBCCDDEEFF0011223344556677889900AABBCC", log, StringComparison.Ordinal);
        Assert.DoesNotContain("12345678901", log, StringComparison.Ordinal);
        Assert.DoesNotContain("segredo", log, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("certificado.pfx", log, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateBundleContinuesWhenLogFileIsLocked()
    {
        File.WriteAllText(_paths.LogPath, "locked");
        using var locked = new FileStream(_paths.LogPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => new StartupRegistration.StartupStatus(false, null, false, null, "app.exe"),
            () => []);
        var zipPath = NewExternalZipPath("diagnostics-locked.zip");

        service.CreateBundle(zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        var copyErrors = ReadEntry(archive, "logs/copy-errors.txt");
        Assert.Contains("monitor.log", copyErrors, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateBundleContinuesWhenTelemetryFileIsLocked()
    {
        File.WriteAllText(_paths.TelemetryPath, """{"Version":1,"TotalChecks":3}""");
        using var locked = new FileStream(_paths.TelemetryPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => new StartupRegistration.StartupStatus(false, null, false, null, "app.exe"),
            () => []);
        var zipPath = NewExternalZipPath("diagnostics-telemetry-locked.zip");

        service.CreateBundle(zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        var copyErrors = ReadEntry(archive, "copy-errors.txt");
        Assert.Contains("telemetry.json", copyErrors, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateBundleOmitsAbsentTelemetryWithoutCopyError()
    {
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => new StartupRegistration.StartupStatus(false, null, false, null, "app.exe"),
            () => []);
        var zipPath = NewExternalZipPath("diagnostics-no-telemetry.zip");

        service.CreateBundle(zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        Assert.Null(archive.GetEntry("telemetry.json"));
        Assert.Null(archive.GetEntry("copy-errors.txt"));
    }

    [Fact]
    public void CreateBundleContinuesWhenDiagnosticsDatabaseCannotBeCopied()
    {
        var diagnosticEvents = new DiagnosticEventStore(_paths, _logger);
        Assert.True(diagnosticEvents.RecordInfo("diagnostics.locked", "tests", "Evento antes do lock."));
        using var locked = new FileStream(_paths.DiagnosticsDbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => new StartupRegistration.StartupStatus(false, null, false, null, "app.exe"),
            () => [],
            diagnosticEvents);
        var zipPath = NewExternalZipPath("diagnostics-db-locked.zip");

        service.CreateBundle(zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        var copyErrors = ReadEntry(archive, "copy-errors.txt");
        Assert.Contains("diagnostics.db", copyErrors, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateBundleOmitsAbsentDiagnosticsDatabaseWithoutCopyError()
    {
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => new StartupRegistration.StartupStatus(false, null, false, null, "app.exe"),
            () => []);
        var zipPath = NewExternalZipPath("diagnostics-no-db.zip");

        service.CreateBundle(zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        Assert.Null(archive.GetEntry("diagnostics.db"));
        Assert.Null(archive.GetEntry("copy-errors.txt"));
    }

    [Fact]
    public void CreateBundleCopiesDiagnosticsDatabaseDirectlyWhenNoStoreIsProvided()
    {
        var diagnosticEvents = new DiagnosticEventStore(_paths, _logger);
        Assert.True(diagnosticEvents.RecordInfo("diagnostics.raw_copy", "tests", "Evento para copia direta."));
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => new StartupRegistration.StartupStatus(false, null, false, null, "app.exe"),
            () => [],
            diagnosticEvents: null);
        var zipPath = NewExternalZipPath("diagnostics-db-raw.zip");

        service.CreateBundle(zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        Assert.NotNull(archive.GetEntry("diagnostics.db"));
        var diagnosticsDbCopy = ExtractEntryToExternalFile(archive, "diagnostics.db");
        using var connection = new SqliteConnection($"Data Source={diagnosticsDbCopy}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "select count(*) from events where event_type = 'diagnostics.raw_copy';";
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public void CreateBundleCopiesRotatedLogs()
    {
        File.WriteAllText(_paths.LogPath, "log atual");
        File.WriteAllText(Path.Combine(_paths.RootDirectory, "monitor.log.1"), "log rotacionado");
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => new StartupRegistration.StartupStatus(false, null, false, null, "app.exe"),
            () => []);
        var zipPath = NewExternalZipPath("diagnostics-rotated-logs.zip");

        service.CreateBundle(zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        Assert.Equal("log atual", ReadEntry(archive, "logs/monitor.log"));
        Assert.Equal("log rotacionado", ReadEntry(archive, "logs/monitor.log.1"));
        Assert.Null(archive.GetEntry("logs/copy-errors.txt"));
    }

    [Fact]
    public void CreateBundleWritesCorruptSettingsSnapshotText()
    {
        File.WriteAllText(_paths.SettingsPath, "{ settings ");
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => new StartupRegistration.StartupStatus(false, null, false, null, "app.exe"),
            () => []);
        var zipPath = NewExternalZipPath("diagnostics-corrupt-settings.zip");

        service.CreateBundle(zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        var settings = ReadEntry(archive, "settings.snapshot.txt");
        Assert.Contains("Falha ao copiar settings", settings, StringComparison.Ordinal);
        Assert.Null(archive.GetEntry("settings.snapshot.json"));
    }

    [Fact]
    public void CreateBundleOmitsSettingsSnapshotWhenSettingsFileIsAbsent()
    {
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => new StartupRegistration.StartupStatus(false, null, false, null, "app.exe"),
            () => []);
        var zipPath = NewExternalZipPath("diagnostics-no-settings.zip");

        service.CreateBundle(zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        Assert.Null(archive.GetEntry("settings.snapshot.json"));
        Assert.Null(archive.GetEntry("settings.snapshot.txt"));
    }

    [Fact]
    public void CreateBundleRedactsNestedHashesInSettingsArrays()
    {
        File.WriteAllText(_paths.SettingsPath, """
            {
              "version": 1,
              "settings": {
                "LastCertificateSnapshotHash": "SECRET_HASH",
                "nested": [
                  { "thumbHash": "NESTED_SECRET" },
                  { "safe": "visivel" }
                ]
              }
            }
            """);
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => new StartupRegistration.StartupStatus(false, null, false, null, "app.exe"),
            () => []);
        var zipPath = NewExternalZipPath("diagnostics-settings-redaction.zip");

        service.CreateBundle(zipPath);

        using var archive = ZipFile.OpenRead(zipPath);
        var settings = ReadEntry(archive, "settings.snapshot.json");
        Assert.Contains("\"LastCertificateSnapshotHash\": \"(redacted)\"", settings, StringComparison.Ordinal);
        Assert.Contains("\"thumbHash\": \"(redacted)\"", settings, StringComparison.Ordinal);
        Assert.Contains("\"safe\": \"visivel\"", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_HASH", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("NESTED_SECRET", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateBundleRejectsDestinationInsideAppDataDirectory()
    {
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => new StartupRegistration.StartupStatus(false, null, false, null, "app.exe"),
            () => []);
        var zipPath = Path.Combine(_paths.RootDirectory, "diagnostics.zip");

        var exception = Assert.Throws<InvalidOperationException>(() => service.CreateBundle(zipPath));

        Assert.Contains("fora da pasta de dados", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(zipPath));
    }

    [Fact]
    public void CreateBundleRejectsNestedAppDataDestinationBeforeCreatingDirectory()
    {
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => new StartupRegistration.StartupStatus(false, null, false, null, "app.exe"),
            () => []);
        var nestedDirectory = Path.Combine(_paths.RootDirectory, "exports");
        var zipPath = Path.Combine(nestedDirectory, "diagnostics.zip");

        Assert.Throws<InvalidOperationException>(() => service.CreateBundle(zipPath));

        Assert.False(Directory.Exists(nestedDirectory));
    }

    [Fact]
    public void CreateBundleAllowsDestinationInSiblingDirectoryWithSamePrefix()
    {
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => new StartupRegistration.StartupStatus(false, null, false, null, "app.exe"),
            () => []);
        var siblingDirectory = $"{_paths.RootDirectory}-exports";
        _externalDirectories.Add(siblingDirectory);
        var zipPath = Path.Combine(siblingDirectory, "diagnostics.zip");
        _externalArtifacts.Add(zipPath);

        service.CreateBundle(zipPath);

        Assert.True(File.Exists(zipPath));
    }

    [Fact]
    public void CreateBundleRejectsWhitespaceDestination()
    {
        var service = new DiagnosticsBundleService(
            _paths,
            _logger,
            () => new StartupRegistration.StartupStatus(false, null, false, null, "app.exe"),
            () => []);

        Assert.Throws<ArgumentException>(() => service.CreateBundle("   "));
    }

    private static string ReadEntry(ZipArchive archive, string name)
    {
        var entry = archive.GetEntry(name) ?? throw new FileNotFoundException(name);
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private string NewExternalZipPath(string fileName)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{fileName}");
        _externalArtifacts.Add(path);
        return path;
    }

    private string NewExternalZipPathInDirectory(string directoryName, string fileName)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{directoryName}");
        _externalDirectories.Add(directory);
        var path = Path.Combine(directory, fileName);
        _externalArtifacts.Add(path);
        return path;
    }

    private string ExtractEntryToExternalFile(ZipArchive archive, string name)
    {
        var path = NewExternalZipPath(name.Replace('/', '-'));
        var entry = archive.GetEntry(name) ?? throw new FileNotFoundException(name);
        entry.ExtractToFile(path, overwrite: true);
        return path;
    }
}
