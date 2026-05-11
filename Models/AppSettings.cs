namespace CertExpiryMonitor.Models;

/// <summary>
/// Formato de log persistido em <c>monitor.log</c>.
/// </summary>
public enum LogFormat
{
    /// <summary>Texto humano-legivel: <c>2026-05-10T20:00:00 [INFO] mensagem</c>.</summary>
    Text = 0,
    /// <summary>JSON estruturado por linha — facilita SIEM (Splunk/ELK/Sentinel).</summary>
    Json = 1
}

public sealed class AppSettings
{
    public TimeSpan DailyCheckTime { get; set; } = TimeSpan.FromHours(9);
    public DateOnly? LastCheckDate { get; set; }
    public int InitialDelayMinutes { get; set; } = 5;
    public bool StartupEnabled { get; set; } = true;
    public bool NotificationSoundEnabled { get; set; } = true;
    public string LastCertificateSnapshotHash { get; set; } = string.Empty;
    public ExpiryThresholds Thresholds { get; set; } = new();

    /// <summary>Formato do arquivo de log. Default: texto humano-legivel.</summary>
    public LogFormat LogFormat { get; set; } = LogFormat.Text;

    /// <summary>
    /// Se verdadeiro, espelha eventos ERROR e WARN para o Windows Event Log
    /// (canal "Application", source "CertExpiryMonitor"). Padrao Windows para
    /// monitoramento corporativo via SCOM/Sentinel. Default: <c>false</c>.
    /// </summary>
    public bool EventLogEnabled { get; set; } = false;

    /// <summary>
    /// Se verdadeiro, registra metricas anonimas localmente em <c>telemetry.json</c>
    /// (sem rede, sem dados pessoais — apenas contadores agregados). Default: <c>false</c>.
    /// </summary>
    public bool TelemetryEnabled { get; set; } = false;
}
