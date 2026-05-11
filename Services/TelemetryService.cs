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

        try
        {
            var loaded = JsonSerializer.Deserialize<TelemetryEnvelope>(json, JsonOptions);
            if (loaded is not null)
            {
                envelope = loaded;
                return true;
            }
            // null deserializacao (raro) — trata como corrompido
        }
        catch (Exception)
        {
            // Vai cair no caminho de corrupcao abaixo
        }

        // Corrompido: preserva o arquivo atual para diagnostico antes de qualquer
        // chance de sobrescrita. Increment vai abortar; Load (publico) vai retornar fresh.
        PreserveCorruptFile();
        reason = "corrupt";
        return false;
    }

    private void PreserveCorruptFile()
    {
        try
        {
            if (!File.Exists(_paths.TelemetryPath)) return;
            var corruptPath = $"{_paths.TelemetryPath}.corrupt-{DateTimeOffset.Now:yyyyMMddHHmmss}";
            File.Move(_paths.TelemetryPath, corruptPath, overwrite: true);
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
                // WriteAllText nao e atomico em sentido estrito, mas Telemetry
                // nao e critica — last-write-wins em caso de race e aceitavel.
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
