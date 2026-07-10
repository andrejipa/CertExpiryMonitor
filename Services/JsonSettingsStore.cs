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

    public JsonSettingsStore(AppPaths paths, FileLogger logger)
    {
        _paths = paths;
        _logger = logger;
    }

    // Formato atual. Incrementar ao mudar o schema; manter parser legado abaixo.
    private const int CurrentSettingsVersion = 1;

    private sealed class SettingsFileEnvelope
    {
        public int Version { get; set; } = CurrentSettingsVersion;
        public AppSettings Settings { get; set; } = new AppSettings();
    }

    public AppSettings Load()
    {
        var hasLock = false;
        try
        {
            hasLock = FileMutex.WaitOne(FileLockTimeoutMilliseconds);
            if (!hasLock)
            {
                _logger.Error(new TimeoutException("Settings file lock timeout"), "Failed to acquire settings file lock");
                return new AppSettings();
            }

            if (!File.Exists(_paths.SettingsPath))
            {
                return new AppSettings();
            }

            // Size guard: settings legitimo nunca passa de poucos KB. Se algum
            // processo do usuario gravar um JSON gigante (DoS persistente, OOM
            // a cada startup), preservamos e voltamos aos defaults.
            const long MaxSettingsBytes = 1_048_576;  // 1 MB
            var info = new FileInfo(_paths.SettingsPath);
            if (info.Length > MaxSettingsBytes)
            {
                _logger.Error(
                    new InvalidDataException($"settings.json too large ({info.Length} bytes); ignoring"),
                    "Settings file exceeded size guard");
                PreserveCorruptFile(_paths.SettingsPath);
                return new AppSettings();
            }

            var json = File.ReadAllText(_paths.SettingsPath);
            return DeserializeSettings(json);
        }
        catch (JsonException ex)
        {
            _logger.Error(ex, "Failed to read settings");
            PreserveCorruptFile(_paths.SettingsPath);
            return new AppSettings();
        }
        catch (IOException ex)
        {
            _logger.Error(ex, "Transient IO error while reading settings");
            return new AppSettings();
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.Error(ex, "Transient access error while reading settings");
            return new AppSettings();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to read settings");
            PreserveCorruptFile(_paths.SettingsPath);
            return new AppSettings();
        }
        finally
        {
            if (hasLock)
            {
                FileMutex.ReleaseMutex();
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
            return new AppSettings();
        }

        // Detecta envelope v1+ procurando "settings" case-insensitive.
        // "version" pode vir ausente/string por edicao manual, mas o bloco
        // de settings ainda e aproveitavel e deve prevalecer sobre o parser legado.
        JsonElement? settingsElement = null;
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, "settings", StringComparison.OrdinalIgnoreCase))
            {
                settingsElement = property.Value;
                break;
            }
        }

        if (settingsElement is { } settings)
        {
            return settings.ValueKind == JsonValueKind.Null
                ? new AppSettings()
                : settings.Deserialize<AppSettings>(JsonOptions) ?? new AppSettings();
        }

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

            var envelope = new SettingsFileEnvelope { Version = CurrentSettingsVersion, Settings = settings };
            var json = JsonSerializer.Serialize(envelope, JsonOptions);
            AtomicWrite(_paths.SettingsPath, json);
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
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var backupPath = $"{path}.bak";

        try
        {
            File.WriteAllText(tempPath, content);
            // Retry com backoff para mitigar sharing violation transitoria de
            // antivirus, OneDrive, indexador de pesquisa. Sem isso, save eventual
            // perde dados quando o cliente do OneDrive locka o arquivo por ms.
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
                // Backoff exponencial: 25ms, 50ms, 100ms, 200ms (total ~375ms na pior).
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
            _logger.Error(ex, "Failed to preserve corrupt settings file");
        }
    }
}
