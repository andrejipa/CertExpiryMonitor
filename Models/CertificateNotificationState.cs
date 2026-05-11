namespace CertExpiryMonitor.Models;

/// <summary>
/// Estado de notificacao de um certificado. Rastreia o nivel de urgencia mais
/// recente em que o usuario foi notificado.
/// <para>
/// <b>Acoplamento semantico:</b> os valores numericos (30, 15, 7, 1) coincidem com
/// os valores padrao de <see cref="ExpiryThresholds"/>. Com thresholds customizados,
/// <c>Notified30</c> pode ter sido disparado por um threshold de 45 dias. Isso nao
/// afeta a corretude — o estado e usado apenas para comparacao de progressao entre
/// buckets via <c>ExpiryEvaluator.AlreadyNotified</c>.
/// </para>
/// </summary>
public enum CertificateNotificationState
{
    /// <summary>Nenhuma notificacao enviada.</summary>
    None = 0,
    /// <summary>Notificado na faixa menos urgente (bucket Days30).</summary>
    Notified30 = 30,
    /// <summary>Notificado na segunda faixa (bucket Days15).</summary>
    Notified15 = 15,
    /// <summary>Notificado na faixa urgente (bucket Days7).</summary>
    Notified7 = 7,
    /// <summary>Notificado na faixa critica (bucket Days1).</summary>
    Notified1 = 1,
    /// <summary>Ignorado manualmente pelo usuario; nao sera re-notificado.</summary>
    Dismissed = 999
}
