using System.Text.Json;
using CertExpiryMonitor.Models;

namespace CertExpiryMonitor.Services;

public sealed class JsonStateStore
{
    private const int FileLockTimeoutMilliseconds = 3000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true
    };
    private static readonly Mutex FileMutex = new(false, @"Local\CertExpiryMonitor.StateJson");

    private readonly AppPaths _paths;
    private readonly FileLogger _logger;

    public JsonStateStore(AppPaths paths, FileLogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    // Formato atual do arquivo de estado. Incrementar ao mudar o schema.
    private const int CurrentStateVersion = 1;

    private sealed class StateFileEnvelope
    {
        public int Version { get; set; } = CurrentStateVersion;
        public List<CertificateStateRecord> Records { get; set; } = [];
    }

    public Dictionary<string, CertificateStateRecord> Load()
    {
        var hasLock = false;
        try
        {
            hasLock = FileMutex.WaitOne(FileLockTimeoutMilliseconds);
            if (!hasLock)
            {
                _logger.Error(new TimeoutException("State file lock timeout"), "Failed to acquire state file lock");
                return new Dictionary<string, CertificateStateRecord>(StringComparer.OrdinalIgnoreCase);
            }

            if (!File.Exists(_paths.StatePath))
            {
                return new Dictionary<string, CertificateStateRecord>(StringComparer.OrdinalIgnoreCase);
            }

            var json = File.ReadAllText(_paths.StatePath);
            var records = DeserializeRecords(json);

            return records
                .Where(record => !string.IsNullOrWhiteSpace(record.Thumbprint))
                .GroupBy(record => NormalizeThumbprint(record.Thumbprint), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to read certificate state");
            PreserveCorruptFile(_paths.StatePath);
            return new Dictionary<string, CertificateStateRecord>(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            if (hasLock)
            {
                FileMutex.ReleaseMutex();
            }
        }
    }

    public void Save(Dictionary<string, CertificateStateRecord> state)
    {
        var hasLock = false;
        try
        {
            hasLock = FileMutex.WaitOne(FileLockTimeoutMilliseconds);
            if (!hasLock)
            {
                _logger.Error(new TimeoutException("State file lock timeout"), "Failed to acquire state file lock");
                return;
            }

            var records = state
                .Values
                .OrderBy(record => record.NotAfter)
                .ThenBy(record => record.Thumbprint, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var envelope = new StateFileEnvelope { Version = CurrentStateVersion, Records = records };
            var json = JsonSerializer.Serialize(envelope, JsonOptions);
            AtomicWrite(_paths.StatePath, json);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save certificate state");
        }
        finally
        {
            if (hasLock)
            {
                FileMutex.ReleaseMutex();
            }
        }
    }

    private static List<CertificateStateRecord> DeserializeRecords(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            // Formato legado: lista na raiz (versao anterior ao envelope).
            return JsonSerializer.Deserialize<List<CertificateStateRecord>>(json, JsonOptions) ?? [];
        }

        // Formato atual: { "version": N, "records": [...] }
        var envelope = JsonSerializer.Deserialize<StateFileEnvelope>(json, JsonOptions) ?? new StateFileEnvelope();
        return envelope.Records;
    }

    public static string NormalizeThumbprint(string thumbprint)
    {
        return thumbprint.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();
    }

    private static void AtomicWrite(string path, string content)
    {
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var backupPath = $"{path}.bak";

        try
        {
            File.WriteAllText(tempPath, content);
            // Retry com backoff para sharing violation (antivirus, OneDrive, indexador).
            // Sem isso, save eventual perde dados quando cliente OneDrive locka por ms.
            ReplaceWithRetry(tempPath, path, backupPath);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void ReplaceWithRetry(string tempPath, string path, string backupPath)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, path);
                }
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                // Backoff exponencial: 25ms, 50ms, 100ms, 200ms.
                Thread.Sleep(25 * (1 << (attempt - 1)));
            }
        }
    }

    private void PreserveCorruptFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var corruptPath = $"{path}.corrupt-{DateTimeOffset.Now:yyyyMMddHHmmss}";
            File.Move(path, corruptPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to preserve corrupt state file");
        }
    }
}
