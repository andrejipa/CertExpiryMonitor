using System.Text.Json;

namespace CertExpiryMonitor.Services;

/// <summary>
/// Coleta metricas anonimas localmente para ajudar a melhorar o app.
/// <para>
/// <b>Privacidade:</b> sem rede, sem thumbprints, sem nomes/documentos. Apenas
/// contadores agregados (quantos checks, quantas notificacoes, quantos dismisses).
/// Opt-in via <see cref="Models.AppSettings.TelemetryEnabled"/> (default <c>false</c>).
/// </para>
/// <para>
/// Persiste em <c>telemetry.json</c> com envelope versionado (v1).
/// </para>
/// </summary>
public sealed class TelemetryService
{
    private const int CurrentVersion = 1;
    private const long MaxTelemetryBytes = 1_048_576;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly AppPaths _paths;
    private readonly FileLogger _logger;
    private readonly object _gate = new();

    public TelemetryService(AppPaths paths, FileLogger logger)
    {
        _paths  = paths;
        _logger = logger;
    }

    /// <summary>Estado interno persistido. Publico para inspecao na UI "Ver estatisticas".</summary>
    public sealed class TelemetryEnvelope
    {
        public int Version { get; set; } = CurrentVersion;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Total de verificacoes executadas desde a criacao do arquivo.</summary>
        public long TotalChecks { get; set; }
        /// <summary>Verificacoes que produziram pelo menos 1 certificado notificavel.</summary>
        public long ChecksWithPlan { get; set; }
        /// <summary>Verificacoes puladas por horario/hash igual.</summary>
        public long ChecksSkipped { get; set; }
        /// <summary>Avisos de notificacao efetivamente exibidos.</summary>
        public long NotificationsShown { get; set; }
        /// <summary>Erros de notificacao (toast e popup proprio falharam).</summary>
        public long NotificationFailures { get; set; }
        /// <summary>Cliques em "Nao lembrar este" do usuario.</summary>
        public long DismissOne { get; set; }
        /// <summary>Cliques em "Nao lembrar nenhum" do usuario.</summary>
        public long DismissAll { get; set; }
        /// <summary>Cliques em "Voltar a lembrar" do usuario.</summary>
        public long Restore { get; set; }
        /// <summary>Verificacoes manuais (botao "Atualizar lista" / menu da bandeja).</summary>
        public long ManualChecks { get; set; }
        /// <summary>Vezes que o usuario salvou alteracoes nas faixas.</summary>
        public long ThresholdsChanged { get; set; }
        /// <summary>Vezes que o usuario salvou alteracao no horario.</summary>
        public long ScheduleChanged { get; set; }
    }

    public bool Enabled { get; set; }

    /// <summary>Le envelope (cria default se nao existir). Retorna sempre objeto valido.</summary>
    /// <remarks>
    /// API publica para inspecao (ex: TelemetryWindow). Em caso de IO/JSON erro,
    /// retorna envelope fresh — mas NAO persiste nada. O Increment usa <see cref="TryLoad"/>
    /// para distinguir cenarios e evitar perda de contadores.
    /// </remarks>
    public TelemetryEnvelope Load()
    {
        lock (_gate)
        {
            return TryLoad(out var env, out _) ? env : new TelemetryEnvelope();
        }
    }

    /// <summary>
    /// Tenta ler o envelope. Distingue 3 cenarios:
    /// <list type="bullet">
    ///   <item>Arquivo nao existe → retorna <c>true</c> com envelope vazio (caminho normal de bootstrap).</item>
    ///   <item>Arquivo lido com sucesso → retorna <c>true</c> com envelope deserializado.</item>
    ///   <item>Arquivo existe mas falhou ler/deserializar → retorna <c>false</c>; <paramref name="reason"/> indica se foi JSON corrompido (preserva o arquivo) ou erro de IO transitorio (nao preserva).</item>
    /// </list>
    /// Crucial: <see cref="Increment"/> usa este metodo e ABORTA quando retorna <c>false</c> —
    /// nao sobrescreve o arquivo, preservando os contadores acumulados.
    /// </summary>
    private bool TryLoad(out TelemetryEnvelope envelope, out string reason)
    {
        envelope = new TelemetryEnvelope();
        reason   = "ok";

        if (!File.Exists(_paths.TelemetryPath))
        {
            return true;  // primeiro Increment cria o arquivo
        }

        var info = new FileInfo(_paths.TelemetryPath);
        if (info.Length > MaxTelemetryBytes)
        {
            _logger.Error(
                new InvalidDataException($"telemetry.json too large ({info.Length} bytes); ignoring"),
                "Telemetry file exceeded size guard");
            PreserveCorruptFile();
            reason = "corrupt";
            return false;
        }

        string json;
        try
        {
            json = File.ReadAllText(_paths.TelemetryPath);
        }
        catch (Exception ex)
        {
            // IO error (antivirus, OneDrive, mapped drive ocupado). NAO preserva
            // como .corrupt porque o arquivo NAO esta corrompido — so nao pude ler.
            _logger.Error(ex, "Telemetry transient IO error during read; aborting update to preserve counters");
            reason = "io_error";
            return false;
        }

        if (TryDeserializeEnvelope(json, out var loaded))
        {
            envelope = loaded;
            return true;
        }

        // Corrompido: preserva o arquivo atual para diagnostico antes de qualquer
        // chance de sobrescrita. Increment vai abortar; Load (publico) vai retornar fresh.
        PreserveCorruptFile();
        reason = "corrupt";
        return false;
    }

    private static bool TryDeserializeEnvelope(string json, out TelemetryEnvelope envelope)
    {
        try
        {
            envelope = JsonSerializer.Deserialize<TelemetryEnvelope>(json, JsonOptions) ?? new TelemetryEnvelope();
            return true;
        }
        catch (JsonException)
        {
            // Cai para parser tolerante abaixo. O caso observado em stress e
            // edicao manual de "version" como string, sem dano aos contadores.
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                envelope = new TelemetryEnvelope();
                return false;
            }

            var root = document.RootElement;
            envelope = new TelemetryEnvelope
            {
                Version = ReadInt32(root, "version", CurrentVersion),
                CreatedAt = ReadDateTime(root, "createdAt", DateTime.UtcNow),
                UpdatedAt = ReadDateTime(root, "updatedAt", DateTime.UtcNow),
                TotalChecks = ReadInt64(root, "totalChecks"),
                ChecksWithPlan = ReadInt64(root, "checksWithPlan"),
                ChecksSkipped = ReadInt64(root, "checksSkipped"),
                NotificationsShown = ReadInt64(root, "notificationsShown"),
                NotificationFailures = ReadInt64(root, "notificationFailures"),
                DismissOne = ReadInt64(root, "dismissOne"),
                DismissAll = ReadInt64(root, "dismissAll"),
                Restore = ReadInt64(root, "restore"),
                ManualChecks = ReadInt64(root, "manualChecks"),
                ThresholdsChanged = ReadInt64(root, "thresholdsChanged"),
                ScheduleChanged = ReadInt64(root, "scheduleChanged")
            };
            return true;
        }
        catch (JsonException)
        {
            envelope = new TelemetryEnvelope();
            return false;
        }
    }

    private static long ReadInt64(JsonElement root, string propertyName, long fallback = 0)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var value))
            {
                return value;
            }

            if (property.Value.ValueKind == JsonValueKind.String &&
                long.TryParse(property.Value.GetString(), out value))
            {
                return value;
            }

            return fallback;
        }

        return fallback;
    }

    private static int ReadInt32(JsonElement root, string propertyName, int fallback)
    {
        var value = ReadInt64(root, propertyName, fallback);
        return value is >= int.MinValue and <= int.MaxValue ? (int)value : fallback;
    }

    private static DateTime ReadDateTime(JsonElement root, string propertyName, DateTime fallback)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind == JsonValueKind.String &&
                   property.Value.TryGetDateTime(out var value)
                ? value
                : fallback;
        }

        return fallback;
    }

    private void PreserveCorruptFile()
    {
        try
        {
            if (!File.Exists(_paths.TelemetryPath)) return;
            var corruptPath = $"{_paths.TelemetryPath}.corrupt-{DateTimeOffset.Now:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
            File.Move(_paths.TelemetryPath, corruptPath);
            _logger.Info($"Telemetry file was corrupt; preserved at {corruptPath}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to preserve corrupt telemetry file");
        }
    }

    /// <summary>Incrementa um contador (no-op se telemetria estiver desativada).</summary>
    public void Increment(Action<TelemetryEnvelope> mutator)
    {
        ArgumentNullException.ThrowIfNull(mutator);
        if (!Enabled) return;

        lock (_gate)
        {
            // Crucial: se TryLoad retorna false (IO error ou corrupcao), NAO escrever.
            // Caso contrario poderiamos zerar contadores acumulados sobre falha
            // transitoria (antivirus lockou o arquivo por ms durante scan).
            if (!TryLoad(out var env, out var reason))
            {
                _logger.Info($"Telemetry update skipped ({reason}); counters preserved.");
                return;
            }

            try
            {
                mutator(env);
                env.UpdatedAt = DateTime.UtcNow;
                var json = JsonSerializer.Serialize(env, JsonOptions);
                DurableFileWriter.WriteAtomic(_paths.TelemetryPath, json, deleteBackup: true);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to update telemetry");
            }
        }
    }

    /// <summary>Apaga todas as metricas (botao "Limpar estatisticas" na UI).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_paths.TelemetryPath))
                {
                    File.Delete(_paths.TelemetryPath);
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to reset telemetry");
            }
        }
    }
}
