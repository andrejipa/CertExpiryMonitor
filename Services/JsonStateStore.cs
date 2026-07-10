using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CertExpiryMonitor.Models;

namespace CertExpiryMonitor.Services;

public sealed class JsonStateStore
{
    private const int FileLockTimeoutMilliseconds = 3000;
    private const int CurrentStateVersion = 1;
    private const int CurrentEncryptedStateVersion = 2;
    private const string EncryptedFormat = "dpapi-current-user";
    private const long MaxPlaintextStateBytes = 10_485_760; // 10 MB
    private const long MaxStoredStateBytes = 16_777_216;    // 16 MB: DPAPI + base64 overhead
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };
    private static readonly Mutex FileMutex = new(false, @"Local\CertExpiryMonitor.StateJson");

    private readonly AppPaths _paths;
    private readonly FileLogger _logger;

    public JsonStateStore(AppPaths paths, FileLogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    private sealed class StateFileEnvelope
    {
        public int Version { get; set; } = CurrentStateVersion;
        public List<CertificateStateRecord> Records { get; set; } = [];
    }

    private sealed class EncryptedStateFileEnvelope
    {
        public int Version { get; set; } = CurrentEncryptedStateVersion;
        public string Format { get; set; } = EncryptedFormat;
        public string Payload { get; set; } = string.Empty;
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

            // Size guard: state legitimo cresce ~200B por cert; 10MB suporta dezenas
            // de milhares de registros. O envelope DPAPI/base64 tem overhead, por isso
            // o limite em disco e maior que o limite do JSON claro depois de descriptar.
            var info = new FileInfo(_paths.StatePath);
            if (info.Length > MaxStoredStateBytes)
            {
                _logger.Error(
                    new InvalidDataException($"certificate-state.json too large ({info.Length} bytes); ignoring"),
                    "State file exceeded size guard");
                PreserveCorruptFile(_paths.StatePath);
                return new Dictionary<string, CertificateStateRecord>(StringComparer.OrdinalIgnoreCase);
            }

            var json = File.ReadAllText(_paths.StatePath);
            var records = DeserializeRecords(json);

            return records
                .Select(record => new
                {
                    Thumbprint = NormalizeThumbprint(record.Thumbprint ?? string.Empty),
                    Record = record
                })
                .Where(item => item.Thumbprint.Length > 0)
                .GroupBy(item => item.Thumbprint, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group =>
                    {
                        var record = group.Last().Record;
                        record.Thumbprint = group.Key;
                        return record;
                    },
                    StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "Failed to read certificate state");
            PreserveCorruptFile(_paths.StatePath);
            return new Dictionary<string, CertificateStateRecord>(StringComparer.OrdinalIgnoreCase);
        }
        catch (IOException ex)
        {
            _logger.Error(ex, "Transient IO error while reading certificate state");
            return new Dictionary<string, CertificateStateRecord>(StringComparer.OrdinalIgnoreCase);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Error(ex, "Transient access error while reading certificate state");
            return new Dictionary<string, CertificateStateRecord>(StringComparer.OrdinalIgnoreCase);
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

    public bool Save(Dictionary<string, CertificateStateRecord> state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var hasLock = false;
        try
        {
            hasLock = FileMutex.WaitOne(FileLockTimeoutMilliseconds);
            if (!hasLock)
            {
                _logger.Error(new TimeoutException("State file lock timeout"), "Failed to acquire state file lock");
                return false;
            }

            var records = state
                .Values
                .Where(record => record is not null)
                .Select(record => new
                {
                    Thumbprint = NormalizeThumbprint(record.Thumbprint ?? string.Empty),
                    Record = record
                })
                .Where(item => item.Thumbprint.Length > 0)
                .GroupBy(item => item.Thumbprint, StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    var record = group.Last().Record;
                    return new CertificateStateRecord
                    {
                        Thumbprint = group.Key,
                        NotAfter = record.NotAfter,
                        State = record.State,
                        LastNotifiedAt = record.LastNotifiedAt
                    };
                })
                .OrderBy(record => record.NotAfter)
                .ThenBy(record => record.Thumbprint, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var envelope = new StateFileEnvelope { Version = CurrentStateVersion, Records = records };
            var json = JsonSerializer.Serialize(envelope, JsonOptions);
            var encryptedJson = EncryptStateJson(json);
            AtomicWrite(_paths.StatePath, encryptedJson, deleteBackup: true);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save certificate state");
            return false;
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

        if (TryReadEncryptedPayload(doc.RootElement, out var encryptedPayload))
        {
            var decryptedJson = DecryptStateJson(encryptedPayload);
            return DeserializeRecords(decryptedJson);
        }

        // Formato legado v1: { "version": N, "records": [...] }. A propriedade
        // version pode vir ausente/string por edicao manual; se records existe,
        // ele deve prevalecer sobre a desserializacao do envelope inteiro.
        if (doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(property.Name, "records", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return property.Value.ValueKind == JsonValueKind.Null
                    ? []
                    : property.Value.Deserialize<List<CertificateStateRecord>>(JsonOptions) ?? [];
            }
        }

        return [];
    }

    private static string EncryptStateJson(string plaintextJson)
    {
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintextJson);
        if (plaintextBytes.Length > MaxPlaintextStateBytes)
        {
            throw new InvalidDataException($"certificate-state.json plaintext too large ({plaintextBytes.Length} bytes)");
        }

        var protectedBytes = ProtectedData.Protect(plaintextBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        var envelope = new EncryptedStateFileEnvelope
        {
            Version = CurrentEncryptedStateVersion,
            Format = EncryptedFormat,
            Payload = Convert.ToBase64String(protectedBytes)
        };

        return JsonSerializer.Serialize(envelope, JsonOptions);
    }

    private static string DecryptStateJson(string payload)
    {
        var protectedBytes = Convert.FromBase64String(payload);
        var plaintextBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
        if (plaintextBytes.Length > MaxPlaintextStateBytes)
        {
            throw new InvalidDataException($"certificate-state.json plaintext too large ({plaintextBytes.Length} bytes)");
        }

        return Encoding.UTF8.GetString(plaintextBytes);
    }

    private static bool TryReadEncryptedPayload(JsonElement root, out string payload)
    {
        payload = string.Empty;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var isEncrypted = false;
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "format", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String &&
                string.Equals(property.Value.GetString(), EncryptedFormat, StringComparison.OrdinalIgnoreCase))
            {
                isEncrypted = true;
                continue;
            }

            if (string.Equals(property.Name, "payload", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                payload = property.Value.GetString() ?? string.Empty;
            }
        }

        return isEncrypted && payload.Length > 0;
    }

    public static string NormalizeThumbprint(string thumbprint)
    {
        ArgumentNullException.ThrowIfNull(thumbprint);

        return new string(thumbprint
            .Where(c => !char.IsWhiteSpace(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)
            .ToArray())
            .ToUpperInvariant();
    }

    private static void AtomicWrite(string path, string content, bool deleteBackup = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var backupPath = $"{path}.bak";

        try
        {
            File.WriteAllText(tempPath, content);
            // Retry com backoff para sharing violation (antivirus, OneDrive, indexador).
            // Sem isso, save eventual perde dados quando cliente OneDrive locka por ms.
            ReplaceWithRetry(tempPath, path, backupPath);
            if (deleteBackup && File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
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

            var corruptPath = $"{path}.corrupt-{DateTimeOffset.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            File.Move(path, corruptPath);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to preserve corrupt state file");
        }
    }
}
