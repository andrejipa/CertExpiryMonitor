using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using Microsoft.Data.Sqlite;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class DiagnosticEventStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppPaths _paths;
    private readonly FileLogger _logger;
    private readonly DiagnosticEventStore _store;

    public DiagnosticEventStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"DiagnosticEventStoreTests_{Guid.NewGuid():N}");
        _paths = new AppPaths(_tempDir);
        _logger = new FileLogger(_paths);
        _store = new DiagnosticEventStore(_paths, _logger);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void RecordInfoCreatesSchemaAndPersistsEventAcrossReopen()
    {
        Assert.True(_store.RecordInfo("test.event", "tests", "Evento de teste.", new { answer = 42 }));

        using var connection = OpenConnection();

        Assert.Equal(1, ExecuteScalar<long>(connection, "select version from schema_info limit 1;"));
        Assert.Equal(1, ExecuteScalar<long>(connection, "select count(*) from events where event_type = 'test.event';"));
        Assert.Equal("INFO", ExecuteScalar<string>(connection, "select severity from events where event_type = 'test.event';"));

        var reopened = new DiagnosticEventStore(_paths, _logger);
        Assert.True(reopened.RecordInfo("test.reopen", "tests", "Evento apos reabrir."));
        Assert.Equal(1, ExecuteScalar<long>(connection, "select count(*) from events where event_type = 'test.reopen';"));
    }

    [Fact]
    public void InitializeCreatesIndexesAndNullDetailsStayDbNull()
    {
        var fixedNow = new DateTimeOffset(2026, 5, 23, 10, 30, 0, TimeSpan.Zero);
        var store = new DiagnosticEventStore(
            _paths,
            _logger,
            new DiagnosticEventStoreOptions { UtcNow = () => fixedNow });

        Assert.True(store.RecordInfo("test.null_details", "tests", "Evento sem detalhes."));

        using var connection = OpenConnection();
        Assert.Equal(fixedNow.ToString("O"), ExecuteScalar<string>(connection, "select occurred_at_utc from events where event_type = 'test.null_details';"));
        Assert.Equal(1, ExecuteScalar<long>(connection, "select count(*) from events where event_type = 'test.null_details' and details_json is null;"));

        var indexes = QueryStrings(connection, "select name from sqlite_master where type = 'index' order by name;");
        Assert.Contains("ix_events_occurred_at_utc", indexes);
        Assert.Contains("ix_events_event_type", indexes);
        Assert.Contains("ix_certificate_observations_captured_at_utc", indexes);
    }

    [Fact]
    public void RecordInfoRecreatesMissingDataDirectory()
    {
        Directory.Delete(_paths.RootDirectory, recursive: true);

        Assert.True(_store.RecordInfo("test.recreate_directory", "tests", "Evento com pasta ausente."));

        Assert.True(File.Exists(_paths.DiagnosticsDbPath));
    }

    [Fact]
    public void RecordMethodsReturnFalseWhenDataRootCannotBeCreated()
    {
        var rootFile = Path.Combine(_tempDir, "root-file");
        var badPaths = new AppPaths(rootFile);
        Directory.Delete(rootFile, recursive: true);
        File.WriteAllText(rootFile, "not a directory");
        var badStore = new DiagnosticEventStore(badPaths, new FileLogger(badPaths));

        Assert.False(badStore.Initialize());
        Assert.False(badStore.RecordInfo("test.bad_root", "tests", "Evento em raiz invalida."));
        Assert.False(badStore.RecordCertificateObservation(
            [NewCertificate("CC00000000000000000000000000000000000001", DateTime.Today.AddDays(1), "66")],
            new ExpiryThresholds().Normalized()));
    }

    [Fact]
    public void RecordCertificateObservationRejectsNullArgumentsBeforeOpeningDatabase()
    {
        Assert.Throws<ArgumentNullException>(() => _store.RecordCertificateObservation(null!, new ExpiryThresholds()));
        Assert.Throws<ArgumentNullException>(() => _store.RecordCertificateObservation([], null!));
        Assert.False(File.Exists(_paths.DiagnosticsDbPath));
    }

    [Fact]
    public void CorruptDatabaseIsPreservedAndRecreated()
    {
        File.WriteAllText(_paths.DiagnosticsDbPath, "not sqlite");

        Assert.True(_store.RecordInfo("test.after_corrupt", "tests", "Evento apos corrupcao."));

        Assert.NotEmpty(Directory.GetFiles(_paths.RootDirectory, "diagnostics.db.corrupt-*"));
        using var connection = OpenConnection();
        Assert.Equal(1, ExecuteScalar<long>(connection, "select count(*) from events where event_type = 'test.after_corrupt';"));
    }

    [Fact]
    public void CorruptDatabasePreservesOriginalAndReplacesStaleWalSidecars()
    {
        File.WriteAllText(_paths.DiagnosticsDbPath, "not sqlite");
        File.WriteAllText($"{_paths.DiagnosticsDbPath}-wal", "old-wal");
        File.WriteAllText($"{_paths.DiagnosticsDbPath}-shm", "old-shm");

        Assert.True(_store.RecordInfo("test.after_corrupt_sidecars", "tests", "Evento apos corrupcao com sidecars."));

        Assert.NotEmpty(Directory.GetFiles(_paths.RootDirectory, "diagnostics.db.corrupt-*"));
        Assert.False(File.Exists($"{_paths.DiagnosticsDbPath}-wal") && File.ReadAllText($"{_paths.DiagnosticsDbPath}-wal") == "old-wal");
        Assert.False(File.Exists($"{_paths.DiagnosticsDbPath}-shm") && File.ReadAllText($"{_paths.DiagnosticsDbPath}-shm") == "old-shm");
    }

    [Fact]
    public void DetailsAreRedactedBeforePersisting()
    {
        const string thumbprint = "AABBCCDDEEFF0011223344556677889900AABBCC";

        Assert.True(_store.RecordInfo(
            "test.redaction",
            "tests",
            "Evento com detalhes sensiveis.",
            new
            {
                thumbprint,
                cpf = "12345678901",
                password = "senha-secreta",
                pfx = "conteudo-pfx",
                subject = "CN=EMPRESA TESTE:12345678901",
                nome = "EMPRESA TESTE",
                note = $"thumb {thumbprint} doc 123.456.789-01"
            }));

        using var connection = OpenConnection();
        var details = ExecuteScalar<string>(connection, "select details_json from events where event_type = 'test.redaction';");

        Assert.DoesNotContain(thumbprint, details, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("12345678901", details, StringComparison.Ordinal);
        Assert.DoesNotContain("senha-secreta", details, StringComparison.Ordinal);
        Assert.DoesNotContain("conteudo-pfx", details, StringComparison.Ordinal);
        Assert.DoesNotContain("EMPRESA TESTE", details, StringComparison.Ordinal);
        Assert.Contains("sha256:", details, StringComparison.Ordinal);
        Assert.Contains("last4:8901", details, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticRedactorHandlesNullAndPrimitiveBoundaries()
    {
        Assert.Null(DiagnosticRedactor.RedactDetailsToJson(null));
        Assert.Equal(string.Empty, DiagnosticRedactor.HashThumbprint(null));
        Assert.Equal(string.Empty, DiagnosticRedactor.SerialPrefix(null));
        Assert.Equal("00112233", DiagnosticRedactor.SerialPrefix("00 11 22 33 44 55"));
        Assert.Equal("1234", DiagnosticRedactor.DocumentLast4("1234"));
        Assert.Equal("8901", DiagnosticRedactor.DocumentLast4("123.456.789-01"));
    }

    [Fact]
    public void DiagnosticRedactorHandlesNestedSecretsAndDocumentBoundaries()
    {
        var fortyHex = new string('A', 40);
        var thirtyNineHex = new string('B', 39);

        var json = DiagnosticRedactor.RedactDetailsToJson(new
        {
            nested = new
            {
                values = new object[]
                {
                    new { senha = "abc123", pfxPath = "certificado.pfx", privateKey = "PRIVATE", chavePrivada = "KEY" },
                    new { thumbprint = fortyHex, serialNumber = "aa bb cc dd ee ff", cpf = "12345678901", cnpj = "11222333000181" }
                }
            },
            shortHex = thirtyNineHex,
            numericBoundaries = "1234567890 987654321098765",
            note = $"{fortyHex} 12345678901 11222333000181"
        });

        Assert.NotNull(json);
        Assert.Contains(thirtyNineHex, json, StringComparison.Ordinal);
        Assert.DoesNotContain(fortyHex, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abc123", json, StringComparison.Ordinal);
        Assert.DoesNotContain("certificado.pfx", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", json, StringComparison.Ordinal);
        Assert.DoesNotContain("12345678901", json, StringComparison.Ordinal);
        Assert.DoesNotContain("11222333000181", json, StringComparison.Ordinal);
        Assert.Contains("1234567890", json, StringComparison.Ordinal);
        Assert.Contains("987654321098765", json, StringComparison.Ordinal);
        Assert.Contains("last4:8901", json, StringComparison.Ordinal);
        Assert.Contains("last4:0181", json, StringComparison.Ordinal);
        Assert.Contains("\"serialNumber\":\"AABBCCDD\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiagnosticRedactorRedactsWindowsUserProfilePathsInFreeText()
    {
        var redacted = DiagnosticRedactor.RedactText(
            "cmd=\"C:\\Users\\Fulano\\AppData\\Local\\Programs\\CertExpiryMonitor\\CertExpiryMonitor.exe\" --background");

        Assert.Contains("C:\\Users\\(redacted)\\AppData\\Local\\Programs\\CertExpiryMonitor\\CertExpiryMonitor.exe", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("Fulano", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticRedactorHandlesEmptyDocumentAndPrimitiveSensitiveValues()
    {
        var json = DiagnosticRedactor.RedactDetailsToJson(new
        {
            document = "",
            cpf = "sem-digitos",
            thumbprint = 12345,
            serial = true,
            subject = false
        });

        Assert.NotNull(json);
        Assert.Contains("\"document\":\"(redacted)\"", json, StringComparison.Ordinal);
        Assert.Contains("\"cpf\":\"(redacted)\"", json, StringComparison.Ordinal);
        Assert.Contains("sha256:", json, StringComparison.Ordinal);
        Assert.Contains("\"serial\":\"TRUE\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"subject\":\"(redacted)\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticRedactorReturnsFallbackWhenDetailsCannotBeSerialized()
    {
        Assert.Equal("""{"serialization":"failed"}""", DiagnosticRedactor.RedactDetailsToJson(new ThrowingDetails()));
    }

    [Fact]
    public void DiagnosticRedactorSerialAndDocumentHelpersCoverShortAndEmptyInputs()
    {
        Assert.Equal(string.Empty, DiagnosticRedactor.SerialPrefix(" - / "));
        Assert.Equal("ABC123", DiagnosticRedactor.SerialPrefix("abc-123"));
        Assert.Equal("ABC12345", DiagnosticRedactor.SerialPrefix("abc123456789"));
        Assert.Equal(string.Empty, DiagnosticRedactor.DocumentLast4("sem digitos"));
        Assert.Equal("123", DiagnosticRedactor.DocumentLast4("123"));
        Assert.Equal("7890", DiagnosticRedactor.DocumentLast4("1234567890"));
    }

    [Fact]
    public void RecordWarningAndErrorPersistSeverityWithoutSensitiveExceptionMessage()
    {
        Assert.True(_store.RecordWarning("test.warning", "tests", "Aviso de teste."));
        Assert.True(_store.RecordError(
            new InvalidOperationException("senha-secreta 12345678901"),
            "test.error",
            "tests",
            "Erro de teste.",
            new { thumbprint = "AABBCCDDEEFF0011223344556677889900AABBCC" }));

        using var connection = OpenConnection();
        Assert.Equal("WARN", ExecuteScalar<string>(connection, "select severity from events where event_type = 'test.warning';"));
        Assert.Equal("ERROR", ExecuteScalar<string>(connection, "select severity from events where event_type = 'test.error';"));

        var details = ExecuteScalar<string>(connection, "select details_json from events where event_type = 'test.error';");
        Assert.Contains("InvalidOperationException", details, StringComparison.Ordinal);
        Assert.DoesNotContain("senha-secreta", details, StringComparison.Ordinal);
        Assert.DoesNotContain("12345678901", details, StringComparison.Ordinal);
        Assert.DoesNotContain("AABBCCDDEEFF0011223344556677889900AABBCC", details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecordCertificateObservationStoresOnlyRedactedCertificateData()
    {
        const string thumbprint = "AABBCCDDEEFF0011223344556677889900AABBCC";
        var certificate = new CertificateSnapshot(
            thumbprint,
            "CN=EMPRESA TESTE:12345678901",
            "CN=Issuer",
            DateTime.Today.AddDays(5),
            "001122334455",
            "EMPRESA TESTE:12345678901");

        Assert.True(_store.RecordCertificateObservation([certificate], new ExpiryThresholds().Normalized()));

        using var connection = OpenConnection();
        var storedThumbprint = ExecuteScalar<string>(connection, "select thumbprint_hash from certificate_observations limit 1;");
        var serialPrefix = ExecuteScalar<string>(connection, "select serial_prefix from certificate_observations limit 1;");
        var status = ExecuteScalar<string>(connection, "select status from certificate_observations limit 1;");

        Assert.False(string.Equals(thumbprint, storedThumbprint, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(DiagnosticRedactor.HashThumbprint(thumbprint), storedThumbprint);
        Assert.Equal("00112233", serialPrefix);
        Assert.Equal("Critical", status);
    }

    [Fact]
    public void RecordCertificateObservationStoresExpectedStatusBuckets()
    {
        var fixedNow = new DateTimeOffset(2026, 5, 23, 13, 0, 0, TimeSpan.Zero);
        var store = new DiagnosticEventStore(
            _paths,
            _logger,
            new DiagnosticEventStoreOptions { UtcNow = () => fixedNow });
        var certificates = new[]
        {
            NewCertificate("AA00000000000000000000000000000000000001", DateTime.Today.AddDays(-2), "11"),
            NewCertificate("AA00000000000000000000000000000000000002", DateTime.Today.AddDays(3), "22"),
            NewCertificate("AA00000000000000000000000000000000000003", DateTime.Today.AddDays(10), "33"),
            NewCertificate("AA00000000000000000000000000000000000004", DateTime.Today.AddDays(45), "44")
        };

        Assert.True(store.RecordCertificateObservation(certificates, new ExpiryThresholds().Normalized()));

        using var connection = OpenConnection();
        var statuses = QueryStrings(connection, "select status from certificate_observations order by not_after;");

        Assert.Equal(new[] { "Expired", "Critical", "Warning", "Valid" }, statuses);
        Assert.Equal(4, ExecuteScalar<long>(connection, "select count(*) from certificate_observations where has_private_key = 1;"));
        Assert.Equal(new[] { fixedNow.ToString("O") }, QueryStrings(connection, "select distinct captured_at_utc from certificate_observations;"));
    }

    [Fact]
    public void RecordCertificateObservationAcceptsEmptyListWithoutCreatingRows()
    {
        Assert.True(_store.RecordCertificateObservation([], new ExpiryThresholds().Normalized()));

        using var connection = OpenConnection();
        Assert.Equal(0, ExecuteScalar<long>(connection, "select count(*) from certificate_observations;"));
    }

    [Fact]
    public void RetentionRemovesOldRowsAndKeepsRecentRows()
    {
        Assert.True(_store.Initialize());
        using (var connection = OpenConnection())
        {
            ExecuteNonQuery(connection, """
                insert into events (occurred_at_utc, severity, event_type, source, message, details_json)
                values ($old_date, 'INFO', 'old.event', 'tests', 'old', null);
                """, ("$old_date", DateTimeOffset.UtcNow.AddDays(-181).ToString("O")));
            ExecuteNonQuery(connection, """
                insert into events (occurred_at_utc, severity, event_type, source, message, details_json)
                values ($recent_date, 'INFO', 'recent.event', 'tests', 'recent', null);
                """, ("$recent_date", DateTimeOffset.UtcNow.ToString("O")));
        }

        Assert.True(_store.RecordInfo("test.retention_trigger", "tests", "Dispara retencao."));

        using var check = OpenConnection();
        Assert.Equal(0, ExecuteScalar<long>(check, "select count(*) from events where event_type = 'old.event';"));
        Assert.Equal(1, ExecuteScalar<long>(check, "select count(*) from events where event_type = 'recent.event';"));
    }

    [Fact]
    public void RetentionRemovesOldCertificateObservationsAndKeepsRecentRows()
    {
        Assert.True(_store.Initialize());
        using (var connection = OpenConnection())
        {
            ExecuteNonQuery(connection, """
                insert into certificate_observations
                    (captured_at_utc, thumbprint_hash, serial_prefix, not_after, days_remaining, status, has_private_key)
                values
                    ($old_date, 'old', '00112233', '2026-01-01T00:00:00.0000000', 1, 'Warning', 1);
                """, ("$old_date", DateTimeOffset.UtcNow.AddDays(-181).ToString("O")));
            ExecuteNonQuery(connection, """
                insert into certificate_observations
                    (captured_at_utc, thumbprint_hash, serial_prefix, not_after, days_remaining, status, has_private_key)
                values
                    ($recent_date, 'recent', '00112233', '2026-01-01T00:00:00.0000000', 1, 'Warning', 1);
                """, ("$recent_date", DateTimeOffset.UtcNow.ToString("O")));
        }

        Assert.True(_store.RecordInfo("test.retention_observations_trigger", "tests", "Dispara retencao."));

        using var check = OpenConnection();
        Assert.Equal(0, ExecuteScalar<long>(check, "select count(*) from certificate_observations where thumbprint_hash = 'old';"));
        Assert.Equal(1, ExecuteScalar<long>(check, "select count(*) from certificate_observations where thumbprint_hash = 'recent';"));
    }

    [Fact]
    public void SizeRetentionRemovesOldestRowsWhenDatabaseIsAboveConfiguredLimit()
    {
        var store = new DiagnosticEventStore(
            _paths,
            _logger,
            new DiagnosticEventStoreOptions
            {
                MaxDatabaseBytes = 1,
                Retention = TimeSpan.FromDays(3650),
                UtcNow = () => new DateTimeOffset(2026, 5, 23, 12, 0, 0, TimeSpan.Zero)
            });

        Assert.True(store.RecordInfo("test.size_retention", "tests", "Evento que sera podado por tamanho."));
        Assert.True(store.RecordCertificateObservation(
            [NewCertificate("BB00000000000000000000000000000000000001", DateTime.Today.AddDays(2), "55")],
            new ExpiryThresholds().Normalized()));

        using var connection = OpenConnection();
        Assert.Equal(0, ExecuteScalar<long>(connection, "select count(*) from events;"));
        Assert.Equal(0, ExecuteScalar<long>(connection, "select count(*) from certificate_observations;"));
    }

    [Fact]
    public void SizeRetentionKeepsRowsWhenDatabaseSizeIsExactlyAtConfiguredLimit()
    {
        Assert.True(_store.Initialize());
        using (var connection = OpenConnection())
        {
            ExecuteNonQuery(connection, """
                insert into events (occurred_at_utc, severity, event_type, source, message, details_json)
                values ($date, 'INFO', 'size.equal', 'tests', 'keep', null);
                """, ("$date", DateTimeOffset.UtcNow.ToString("O")));
        }

        var currentLength = new FileInfo(_paths.DiagnosticsDbPath).Length;
        var store = new DiagnosticEventStore(
            _paths,
            _logger,
            new DiagnosticEventStoreOptions { MaxDatabaseBytes = currentLength, Retention = TimeSpan.FromDays(3650) });

        Assert.True(store.Initialize());

        using var check = OpenConnection();
        Assert.Equal(1, ExecuteScalar<long>(check, "select count(*) from events where event_type = 'size.equal';"));
    }

    [Fact]
    public void SizeRetentionPrunesRowsWhenDatabaseSizeExceedsConfiguredLimitByOneByte()
    {
        Assert.True(_store.Initialize());
        using (var connection = OpenConnection())
        {
            ExecuteNonQuery(connection, """
                insert into events (occurred_at_utc, severity, event_type, source, message, details_json)
                values ($date, 'INFO', 'size.exceeds', 'tests', 'delete', null);
                """, ("$date", DateTimeOffset.UtcNow.ToString("O")));
        }

        var currentLength = new FileInfo(_paths.DiagnosticsDbPath).Length;
        var store = new DiagnosticEventStore(
            _paths,
            _logger,
            new DiagnosticEventStoreOptions { MaxDatabaseBytes = currentLength - 1, Retention = TimeSpan.FromDays(3650) });

        Assert.True(store.Initialize());

        using var check = OpenConnection();
        Assert.Equal(0, ExecuteScalar<long>(check, "select count(*) from events where event_type = 'size.exceeds';"));
    }

    [Fact]
    public void CopyDatabaseSnapshotCreatesQueryableCopy()
    {
        Assert.True(_store.RecordInfo("test.copy", "tests", "Evento para copia."));
        var copyPath = Path.Combine(_tempDir, "copy", "diagnostics.db");

        Assert.True(_store.CopyDatabaseSnapshot(copyPath));

        using var connection = new SqliteConnection($"Data Source={copyPath}");
        connection.Open();
        Assert.Equal(1, ExecuteScalar<long>(connection, "select count(*) from events where event_type = 'test.copy';"));
    }

    [Fact]
    public void CopyDatabaseSnapshotOverwritesExistingDestination()
    {
        Assert.True(_store.RecordInfo("test.copy_overwrite", "tests", "Evento para sobrescrever copia."));
        var copyPath = Path.Combine(_tempDir, "copy-overwrite", "diagnostics.db");
        Directory.CreateDirectory(Path.GetDirectoryName(copyPath)!);
        File.WriteAllText(copyPath, "not sqlite");

        Assert.True(_store.CopyDatabaseSnapshot(copyPath));

        using var connection = new SqliteConnection($"Data Source={copyPath}");
        connection.Open();
        Assert.Equal(1, ExecuteScalar<long>(connection, "select count(*) from events where event_type = 'test.copy_overwrite';"));
    }

    [Fact]
    public void CopyDatabaseSnapshotReturnsFalseForInvalidDestinationPath()
    {
        Assert.True(_store.RecordInfo("test.copy_invalid_destination", "tests", "Evento antes de destino invalido."));

        Assert.False(_store.CopyDatabaseSnapshot(Path.Combine(_tempDir, "bad\0name.db")));
    }

    [Fact]
    public void CopyDatabaseSnapshotRejectsWhitespaceDestination()
    {
        Assert.Throws<ArgumentException>(() => _store.CopyDatabaseSnapshot("   "));
    }

    [Fact]
    public void CopyDatabaseSnapshotReturnsFalseWhenDatabaseDoesNotExist()
    {
        var emptyRoot = Path.Combine(Path.GetTempPath(), $"DiagnosticEventStoreEmpty_{Guid.NewGuid():N}");
        try
        {
            var emptyPaths = new AppPaths(emptyRoot);
            var emptyStore = new DiagnosticEventStore(emptyPaths, new FileLogger(emptyPaths));

            Assert.False(emptyStore.CopyDatabaseSnapshot(Path.Combine(emptyRoot, "copy.db")));
        }
        finally
        {
            try { Directory.Delete(emptyRoot, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void OpenConnectionUsesWalJournalMode()
    {
        Assert.True(_store.Initialize());

        using var connection = OpenConnection();
        Assert.Equal("wal", ExecuteScalar<string>(connection, "pragma journal_mode;"), ignoreCase: true);
    }

    [Fact]
    public void PrivateOpenConnectionAppliesConnectionPragmas()
    {
        using var connection = (SqliteConnection)InvokePrivateInstance(_store, "OpenConnection")!;

        Assert.Equal(1000, ExecuteScalar<long>(connection, "pragma busy_timeout;"));
        Assert.Equal(1, ExecuteScalar<long>(connection, "pragma synchronous;"));
    }

    [Fact]
    public void PrivateCorruptionHelpersHandleSidecarAndNestedExceptionBoundaries()
    {
        var sidecarPath = Path.Combine(_tempDir, "sidecar.tmp");
        File.WriteAllText(sidecarPath, "sidecar");

        InvokePrivateStatic("TryDeleteSidecar", sidecarPath);
        InvokePrivateStatic("TryDeleteSidecar", Path.Combine(_tempDir, "missing-sidecar.tmp"));

        Assert.False(File.Exists(sidecarPath));
        Assert.False((bool)InvokePrivateStatic(
            "IsLikelyCorruptDatabase",
            new InvalidOperationException("outer", new InvalidOperationException("inner")))!);
        Assert.False((bool)InvokePrivateStatic(
            "IsLikelyCorruptDatabase",
            new InvalidOperationException("plain"))!);
        Assert.True((bool)InvokePrivateStatic(
            "IsLikelyCorruptDatabase",
            new InvalidOperationException("outer", new SqliteException("not a database", 11)))!);
        Assert.True((bool)InvokePrivateStatic(
            "IsLikelyCorruptDatabase",
            new SqliteException("database disk image is malformed", 11))!);
        Assert.False((bool)InvokePrivateStatic(
            "IsLikelyCorruptDatabase",
            new SqliteException("permission denied", 5))!);
    }

    private static CertificateSnapshot NewCertificate(string thumbprint, DateTime notAfter, string serialSuffix)
        => new(
            thumbprint,
            $"CN=EMPRESA TESTE {serialSuffix}:12345678901",
            "CN=Issuer",
            notAfter,
            $"0011223344{serialSuffix}",
            $"EMPRESA TESTE {serialSuffix}:12345678901");

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={_paths.DiagnosticsDbPath}");
        connection.Open();
        return connection;
    }

    private static T ExecuteScalar<T>(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var value = command.ExecuteScalar();
        Assert.NotNull(value);
        return (T)Convert.ChangeType(value, typeof(T));
    }

    private static string[] QueryStrings(SqliteConnection connection, string commandText)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(0));
        }

        return values.ToArray();
    }

    private static object? InvokePrivateStatic(string methodName, params object[] arguments)
    {
        var method = typeof(DiagnosticEventStore).GetMethod(
            methodName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return method.Invoke(null, arguments);
    }

    private static object? InvokePrivateInstance(DiagnosticEventStore store, string methodName, params object[] arguments)
    {
        var method = typeof(DiagnosticEventStore).GetMethod(
            methodName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(method);
        return method.Invoke(store, arguments);
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string commandText, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = commandText;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        command.ExecuteNonQuery();
    }

    private sealed class ThrowingDetails
    {
        public string Value => throw new InvalidOperationException("serialization failed");
    }
}
