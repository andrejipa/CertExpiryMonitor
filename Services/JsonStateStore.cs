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
    private readonly JsonStoreReadHooks? _readHooks;

    public JsonStateStore(AppPaths paths, FileLogger logger)
        : this(paths, logger, readHooks: null)
    {
    }

    internal JsonStateStore(AppPaths paths, FileLogger logger, JsonStoreReadHooks? readHooks)
    {
        _paths = paths;
        _logger = logger;
        _readHooks = readHooks;
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

    public bool TryLoad(out Dictionary<string, CertificateStateRecord> state)
    {
        state = new Dictionary<string, CertificateStateRecord>(StringComparer.OrdinalIgnoreCase);
        var hasLock = false;
        try
        {
            hasLock = _readHooks?.AcquireLock?.Invoke(FileLockTimeoutMilliseconds)
                ?? FileMutex.WaitOne(FileLockTimeoutMilliseconds);
            if (!hasLock)
            {
                _logger.Error(new TimeoutException("State file lock timeout"), "Failed to acquire state file lock");
                return false;
            }

            if (!File.Exists(_paths.StatePath))
            {
                return true;
            }

            var records = ReadExistingRecords();

            state = records
                .Select(record => new
                {
                    Thumbprint = CertificateIdentity.NormalizeThumbprint(record.Thumbprint ?? string.Empty),
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
            return true;
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "Failed to read certificate state");
            return false;
        }
        catch (IOException ex)
        {
            _logger.Error(ex, "Transient IO error while reading certificate state");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Error(ex, "Transient access error while reading certificate state");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to read certificate state");
            return false;
        }
        finally
        {
            if (hasLock)
            {
                if (_readHooks?.ReleaseLock is { } releaseLock) releaseLock();
                else FileMutex.ReleaseMutex();
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

            // Revalidar sob o mesmo mutex antes de substituir dados persistidos.
            if (File.Exists(_paths.StatePath)) _ = ReadExistingRecords();

            var records = state
                .Values
                .Where(record => record is not null)
                .Select(record => new
                {
                    Thumbprint = CertificateIdentity.NormalizeThumbprint(record.Thumbprint ?? string.Empty),
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
            if (_readHooks?.WriteAtomically is { } writeAtomically) writeAtomically(_paths.StatePath, encryptedJson);
            else AtomicWrite(_paths.StatePath, encryptedJson, deleteBackup: true);
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

    private static List<CertificateStateRecord> DeserializeRecords(string json, bool allowEncrypted = true)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            // Formato legado: lista na raiz (versao anterior ao envelope).
            return ReadRecordsArray(doc.RootElement);
        }

        if (TryReadEncryptedPayload(doc.RootElement, out var encryptedPayload))
        {
            if (!allowEncrypted)
                throw new JsonException("Envelope criptografado aninhado nao e permitido.");
            var decryptedJson = DecryptStateJson(encryptedPayload);
            return DeserializeRecords(decryptedJson, allowEncrypted: false);
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

                return ReadRecordsArray(property.Value);
            }
        }

        throw new JsonException("Estado sem lista de registros reconhecida.");
    }

    private static List<CertificateStateRecord> ReadRecordsArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new JsonException("Registros devem ser uma lista.");
        var records = element.Deserialize<List<CertificateStateRecord>>(JsonOptions)!;
        if (records.Any(record => record is null))
            throw new JsonException("Registro de certificado nulo.");
        return records;
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
        var hasEnvelopeMarker = false;
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "format", StringComparison.OrdinalIgnoreCase))
            {
                hasEnvelopeMarker = true;
                isEncrypted = property.Value.ValueKind == JsonValueKind.String &&
                    string.Equals(property.Value.GetString(), EncryptedFormat, StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (string.Equals(property.Name, "payload", StringComparison.OrdinalIgnoreCase))
            {
                hasEnvelopeMarker = true;
                payload = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() ?? string.Empty : string.Empty;
            }
        }

        if (hasEnvelopeMarker && (!isEncrypted || string.IsNullOrWhiteSpace(payload)))
            throw new JsonException("Envelope criptografado invalido ou incompleto.");
        return hasEnvelopeMarker;
    }

    public static string NormalizeThumbprint(string thumbprint)
    {
        return CertificateIdentity.NormalizeThumbprint(thumbprint);
    }

    private static void AtomicWrite(string path, string content, bool deleteBackup = false)
    {
        DurableFileWriter.WriteAtomic(path, content, deleteBackup);
    }

    // Chamado somente dentro do mutex; dados invalidos permanecem no caminho original.
    private List<CertificateStateRecord> ReadExistingRecords()
    {
        if (new FileInfo(_paths.StatePath).Length > MaxStoredStateBytes)
            throw new InvalidDataException("Arquivo de estado excede o limite de tamanho.");
        return DeserializeRecords((_readHooks?.ReadAllText ?? File.ReadAllText)(_paths.StatePath));
    }
}
