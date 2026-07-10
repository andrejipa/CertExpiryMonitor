using CertExpiryMonitor.Models;
using Microsoft.Data.Sqlite;

namespace CertExpiryMonitor.Services;

public sealed class DiagnosticEventStore
{
    private const int CurrentSchemaVersion = 1;

    private readonly AppPaths _paths;
    private readonly FileLogger _logger;
    private readonly DiagnosticEventStoreOptions _options;
    private readonly object _gate = new();

    public DiagnosticEventStore(AppPaths paths, FileLogger logger)
        : this(paths, logger, new DiagnosticEventStoreOptions())
    {
    }

    internal DiagnosticEventStore(AppPaths paths, FileLogger logger, DiagnosticEventStoreOptions options)
    {
        _paths = paths;
        _logger = logger;
        _options = options;
    }

    public bool Initialize()
    {
        lock (_gate)
        {
            return EnsureInitialized();
        }
    }

    public bool RecordInfo(string eventType, string source, string message, object? details = null)
        => Record("INFO", eventType, source, message, details);

    public bool RecordWarning(string eventType, string source, string message, object? details = null)
        => Record("WARN", eventType, source, message, details);

    public bool RecordError(Exception ex, string eventType, string source, string message, object? details = null)
    {
        var redactedDetails = new
        {
            details,
            exception_type = ex.GetType().FullName
        };

        return Record("ERROR", eventType, source, message, redactedDetails);
    }

    public bool RecordCertificateObservation(IReadOnlyList<CertificateSnapshot> certificates, ExpiryThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(thresholds);

        lock (_gate)
        {
            try
            {
                if (!EnsureInitialized())
                {
                    return false;
                }

                using var connection = OpenConnection();
                var today = DateTime.Today;
                var capturedAt = _options.UtcNow().ToString("O");
                var normalizedThresholds = thresholds.Normalized();

                using var transaction = connection.BeginTransaction();
                foreach (var certificate in certificates)
                {
                    var daysRemaining = (int)(certificate.NotAfter.Date - today).TotalDays;
                    var status = CertificateStatusHelpers.GetStatusCategory(daysRemaining, null, normalizedThresholds);

                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = """
                        insert into certificate_observations
                            (captured_at_utc, thumbprint_hash, serial_prefix, not_after, days_remaining, status, has_private_key)
                        values
                            ($captured_at_utc, $thumbprint_hash, $serial_prefix, $not_after, $days_remaining, $status, $has_private_key);
                        """;
                    command.Parameters.AddWithValue("$captured_at_utc", capturedAt);
                    command.Parameters.AddWithValue("$thumbprint_hash", DiagnosticRedactor.HashThumbprint(certificate.Thumbprint));
                    command.Parameters.AddWithValue("$serial_prefix", DiagnosticRedactor.SerialPrefix(certificate.SerialNumber));
                    command.Parameters.AddWithValue("$not_after", certificate.NotAfter.ToString("O"));
                    command.Parameters.AddWithValue("$days_remaining", daysRemaining);
                    command.Parameters.AddWithValue("$status", status);
                    command.Parameters.AddWithValue("$has_private_key", 1);
                    command.ExecuteNonQuery();
                }

                transaction.Commit();
                EnforceRetention(connection);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to record certificate observations");
                return false;
            }
        }
    }

    public bool CopyDatabaseSnapshot(string destinationPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        lock (_gate)
        {
            try
            {
                if (!File.Exists(_paths.DiagnosticsDbPath) || !EnsureInitialized())
                {
                    return false;
                }

                var destinationDirectory = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }

                if (File.Exists(destinationPath))
                {
                    File.Delete(destinationPath);
                }

                using var source = OpenConnection();
                using var destination = new SqliteConnection(BuildConnectionString(destinationPath));
                destination.Open();
                source.BackupDatabase(destination);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to copy diagnostics database snapshot");
                return false;
            }
        }
    }

    private bool Record(string severity, string eventType, string source, string message, object? details)
    {
        lock (_gate)
        {
            try
            {
                if (!EnsureInitialized())
                {
                    return false;
                }

                using var connection = OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = """
                    insert into events
                        (occurred_at_utc, severity, event_type, source, message, details_json)
                    values
                        ($occurred_at_utc, $severity, $event_type, $source, $message, $details_json);
                    """;
                command.Parameters.AddWithValue("$occurred_at_utc", _options.UtcNow().ToString("O"));
                command.Parameters.AddWithValue("$severity", severity);
                command.Parameters.AddWithValue("$event_type", eventType);
                command.Parameters.AddWithValue("$source", source);
                command.Parameters.AddWithValue("$message", message);
                command.Parameters.AddWithValue("$details_json", (object?)DiagnosticRedactor.RedactDetailsToJson(details) ?? DBNull.Value);
                command.ExecuteNonQuery();

                EnforceRetention(connection);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"Failed to record diagnostic event {eventType}");
                return false;
            }
        }
    }

    private bool EnsureInitialized()
    {
        try
        {
            Directory.CreateDirectory(_paths.RootDirectory);
            using var connection = OpenConnection();
            CreateSchema(connection);
            EnforceRetention(connection);
            return true;
        }
        catch (Exception ex) when (IsLikelyCorruptDatabase(ex))
        {
            _logger.Error(ex, "Diagnostics database appears corrupt; preserving and recreating");
            TryPreserveCorruptDatabase();

            try
            {
                using var connection = OpenConnection();
                CreateSchema(connection);
                return true;
            }
            catch (Exception recreateEx)
            {
                _logger.Error(recreateEx, "Failed to recreate diagnostics database");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to initialize diagnostics database");
            return false;
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(BuildConnectionString(_paths.DiagnosticsDbPath));
        try
        {
            connection.Open();

            ExecuteNonQuery(connection, "pragma busy_timeout = 1000;");
            ExecuteNonQuery(connection, "pragma journal_mode = wal;");
            ExecuteNonQuery(connection, "pragma synchronous = normal;");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static string BuildConnectionString(string path)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        };
        return builder.ToString();
    }

    private static void CreateSchema(SqliteConnection connection)
    {
        ExecuteNonQuery(connection, """
            create table if not exists schema_info (
                version integer not null
            );

            delete from schema_info;
            insert into schema_info (version) values ($version);

            create table if not exists events (
                id integer primary key,
                occurred_at_utc text not null,
                severity text not null,
                event_type text not null,
                source text not null,
                message text not null,
                details_json text null
            );

            create index if not exists ix_events_occurred_at_utc on events (occurred_at_utc);
            create index if not exists ix_events_event_type on events (event_type);

            create table if not exists certificate_observations (
                id integer primary key,
                captured_at_utc text not null,
                thumbprint_hash text not null,
                serial_prefix text not null,
                not_after text not null,
                days_remaining integer not null,
                status text not null,
                has_private_key integer not null
            );

            create index if not exists ix_certificate_observations_captured_at_utc
                on certificate_observations (captured_at_utc);
            """, ("$version", CurrentSchemaVersion));
    }

    private void EnforceRetention(SqliteConnection connection)
    {
        try
        {
            var cutoff = _options.UtcNow().Subtract(_options.Retention).ToString("O");
            ExecuteNonQuery(connection, "delete from events where occurred_at_utc < $cutoff;", ("$cutoff", cutoff));
            ExecuteNonQuery(connection, "delete from certificate_observations where captured_at_utc < $cutoff;", ("$cutoff", cutoff));
            CheckpointWal(connection);

            var info = new FileInfo(_paths.DiagnosticsDbPath);
            if (!info.Exists || info.Length <= _options.MaxDatabaseBytes)
            {
                return;
            }

            for (var i = 0; i < 10 && info.Exists && info.Length > _options.MaxDatabaseBytes; i++)
            {
                ExecuteNonQuery(connection, """
                    delete from events
                    where id in (select id from events order by occurred_at_utc asc limit 1000);
                    """);
                ExecuteNonQuery(connection, """
                    delete from certificate_observations
                    where id in (select id from certificate_observations order by captured_at_utc asc limit 1000);
                    """);
                ExecuteNonQuery(connection, "vacuum;");
                CheckpointWal(connection);
                info.Refresh();

                if (CountRows(connection, "events") == 0 &&
                    CountRows(connection, "certificate_observations") == 0)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to enforce diagnostics database retention");
        }
    }

    private void CheckpointWal(SqliteConnection connection)
    {
        try
        {
            ExecuteNonQuery(connection, "pragma wal_checkpoint(truncate);");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to checkpoint diagnostics database WAL");
        }
    }

    private static int CountRows(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"select count(*) from {tableName};";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private void TryPreserveCorruptDatabase()
    {
        try
        {
            if (!File.Exists(_paths.DiagnosticsDbPath)) return;

            var corruptPath = $"{_paths.DiagnosticsDbPath}.corrupt-{DateTimeOffset.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            File.Move(_paths.DiagnosticsDbPath, corruptPath);
            TryDeleteSidecar($"{_paths.DiagnosticsDbPath}-wal");
            TryDeleteSidecar($"{_paths.DiagnosticsDbPath}-shm");
            _logger.Info($"Diagnostics database was corrupt; preserved at {corruptPath}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to preserve corrupt diagnostics database");
        }
    }

    private static void TryDeleteSidecar(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // best-effort
        }
    }

    private static bool IsLikelyCorruptDatabase(Exception exception)
    {
        if (exception is SqliteException sqlite &&
            (sqlite.SqliteErrorCode == 11 || sqlite.Message.Contains("not a database", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return exception.InnerException is not null && IsLikelyCorruptDatabase(exception.InnerException);
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
}
