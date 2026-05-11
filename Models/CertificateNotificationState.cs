namespace CertExpiryMonitor.Models;

/// <summary>
/// Estado de notificacao de um certificado. Rastreia o nivel de urgencia mais
/// recente em que o usuario foi notificado.
/// <para>
/// Os nomes referem-se ao <i>bucket</i> abstrato (Long/Medium/Short/Urgent), nao a
/// dias especificos — com thresholds customizados, "NotifiedLong" pode ter sido
/// disparado por 45 dias em vez dos 30 padrao. O estado e usado apenas para
/// comparacao de progressao entre buckets via <c>ExpiryEvaluator.AlreadyNotified</c>.
/// </para>
/// <para>
/// <b>Compatibilidade JSON:</b> os valores numericos (30, 15, 7, 1, 999) sao preservados
/// para nao quebrar arquivos de estado existentes no disco. <c>System.Text.Json</c>
/// serializa enums como numero por padrao, entao renomear os identificadores e
/// transparente para o arquivo persistido.
/// </para>
/// </summary>
public enum CertificateNotificationState
{
    /// <summary>Nenhuma notificacao enviada.</summary>
    None = 0,

    /// <summary>Notificado na faixa mais distante (bucket Days30 — "Faixa longa").</summary>
    NotifiedLong = 30,

    /// <summary>Notificado na segunda faixa (bucket Days15 — "Faixa media").</summary>
    NotifiedMedium = 15,

    /// <summary>Notificado na faixa de proximidade (bucket Days7 — "Faixa curta").</summary>
    NotifiedShort = 7,

    /// <summary>Notificado na faixa critica (bucket Days1 — "Urgente").</summary>
    NotifiedUrgent = 1,

    /// <summary>Ignorado manualmente pelo usuario; nao sera re-notificado.</summary>
    Dismissed = 999
}
