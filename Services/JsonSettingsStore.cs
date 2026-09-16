using System.Text.Json;
using CertExpiryMonitor.Models;

namespace CertExpiryMonitor.Services;

public sealed class JsonSettingsStore
{
    private const int FileLockTimeoutMilliseconds = 3000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented              = true,
        // Case-insensitive permite ler envelope com "settings" (do nosso writer) ou
        // "Settings" (qualquer ferramenta externa) sem cair no caminho legado.
        PropertyNameCaseInsensitive = true
    };
    private static readonly Mutex FileMutex = new(false, @"Local\CertExpiryMonitor.SettingsJson");

    private readonly AppPaths _paths;
    private readonly FileLogger _logger;
    private readonly JsonStoreReadHooks? _readHooks;

    public JsonSettingsStore(AppPaths paths, FileLogger logger)
        : this(paths, logger, readHooks: null)
    {
    }

    internal JsonSettingsStore(AppPaths paths, FileLogger logger, JsonStoreReadHooks? readHooks)
    {
        _paths = paths;
        _logger = logger;
        _readHooks = readHooks;
    }

    // Formato atual. Incrementar ao mudar o schema; manter parser legado abaixo.
    private const int CurrentSettingsVersion = 1;

    private sealed class SettingsFileEnvelope
    {
        public int Version { get; set; } = CurrentSettingsVersion;
        public AppSettings Settings { get; set; } = new AppSettings();
    }

    public bool TryLoad(out AppSettings settings)
    {
        settings = new AppSettings();
        var hasLock = false;
        try
        {
            hasLock = _readHooks?.AcquireLock?.Invoke(FileLockTimeoutMilliseconds)
                ?? FileMutex.WaitOne(FileLockTimeoutMilliseconds);
            if (!hasLock)
            {
                _logger.Error(new TimeoutException("Settings file lock timeout"), "Failed to acquire settings file lock");
                return false;
            }

            if (!File.Exists(_paths.SettingsPath))
            {
                return true;
            }

            settings = ReadExistingSettings();
            return true;
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "Failed to read settings");
            return false;
        }
        catch (IOException ex)
        {
            _logger.Error(ex, "Transient IO error while reading settings");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Error(ex, "Transient access error while reading settings");
            return false;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to read settings");
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

    /// <summary>
    /// Le settings tanto no formato envelope v1 ({ "version":1, "settings":{...} })
    /// quanto no formato legado (objeto AppSettings puro). Migracao para envelope
    /// acontece no proximo Save automaticamente.
    /// </summary>
    private static AppSettings DeserializeSettings(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Configuracoes devem conter um objeto JSON.");
        }

        // Detecta envelope v1+ procurando "settings" case-insensitive.
        // "version" pode vir ausente/string por edicao manual, mas o bloco
        // de settings ainda e aproveitavel e deve prevalecer sobre o parser legado.
        JsonElement? settingsElement = null;
        var hasVersion = false;
        foreach (var property in root.EnumerateObject())
        {
            hasVersion |= string.Equals(property.Name, "version", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(property.Name, "settings", StringComparison.OrdinalIgnoreCase))
            {
                settingsElement = property.Value;
            }
        }

        if (settingsElement is { } settings)
        {
            if (settings.ValueKind != JsonValueKind.Object)
                throw new JsonException("Envelope de configuracoes sem objeto settings valido.");
            return settings.Deserialize<AppSettings>(JsonOptions)!;
        }

        if (hasVersion)
            throw new JsonException("Envelope de configuracoes sem settings.");

        // Formato legado: AppSettings serializado diretamente.
        return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
    }

    public bool Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var hasLock = false;
        try
        {
            hasLock = FileMutex.WaitOne(FileLockTimeoutMilliseconds);
            if (!hasLock)
            {
                _logger.Error(new TimeoutException("Settings file lock timeout"), "Failed to acquire settings file lock");
                return false;
            }

            // Nao substituir dados ilegíveis por valores de fallback de uma leitura anterior.
            if (File.Exists(_paths.SettingsPath)) _ = ReadExistingSettings();

            var envelope = new SettingsFileEnvelope { Version = CurrentSettingsVersion, Settings = settings };
            var json = JsonSerializer.Serialize(envelope, JsonOptions);
            if (_readHooks?.WriteAtomically is { } writeAtomically) writeAtomically(_paths.SettingsPath, json);
            else AtomicWrite(_paths.SettingsPath, json);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to save settings");
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

    private static void AtomicWrite(string path, string content)
    {
        DurableFileWriter.WriteAtomic(path, content);
    }

    // Chamado somente dentro do mutex; preservar o original mantem a falha apos reiniciar.
    private AppSettings ReadExistingSettings()
    {
        const long maxSettingsBytes = 1_048_576;
        if (new FileInfo(_paths.SettingsPath).Length > maxSettingsBytes)
            throw new InvalidDataException("Arquivo de configuracoes excede o limite de tamanho.");
        return DeserializeSettings((_readHooks?.ReadAllText ?? File.ReadAllText)(_paths.SettingsPath));
    }
}
