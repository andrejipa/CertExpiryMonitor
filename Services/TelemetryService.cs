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
        /// <summary>Toast notifications efetivamente exibidas.</summary>
        public long NotificationsShown { get; set; }
        /// <summary>Erros de notificacao (toast falhou + fallback balloon).</summary>
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
    public TelemetryEnvelope Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_paths.TelemetryPath))
                {
                    return new TelemetryEnvelope();
                }
                var json = File.ReadAllText(_paths.TelemetryPath);
                return JsonSerializer.Deserialize<TelemetryEnvelope>(json, JsonOptions) ?? new TelemetryEnvelope();
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to read telemetry file; returning fresh envelope");
                return new TelemetryEnvelope();
            }
        }
    }

    /// <summary>Incrementa um contador (no-op se telemetria estiver desativada).</summary>
    public void Increment(Action<TelemetryEnvelope> mutator)
    {
        if (!Enabled) return;

        lock (_gate)
        {
            try
            {
                var env = Load();
                mutator(env);
                env.UpdatedAt = DateTime.UtcNow;
                var json = JsonSerializer.Serialize(env, JsonOptions);
                File.WriteAllText(_paths.TelemetryPath, json);
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
