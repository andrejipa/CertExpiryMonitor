namespace CertExpiryMonitor.Models;

/// <summary>
/// Faixa de urgencia de vencimento. Os nomes (Days30, Days7 etc.) refletem os
/// <b>valores padrao</b> dos thresholds configurados em <see cref="ExpiryThresholds"/>,
/// nao dias fixos. Com thresholds customizados, <c>Days30</c> pode representar
/// 45 dias, por exemplo. O bucket e escolhido por <c>ExpiryEvaluator.SelectBucket</c>
/// com base nos valores de <see cref="ExpiryThresholds"/> vigentes.
/// </summary>
public enum ExpiryBucket
{
    /// <summary>Faixa menos urgente (padrao: ate 30 dias).</summary>
    Days30 = 30,
    /// <summary>Faixa intermediaria (padrao: ate 15 dias).</summary>
    Days15 = 15,
    /// <summary>Faixa urgente (padrao: ate 7 dias).</summary>
    Days7 = 7,
    /// <summary>Faixa critica (padrao: ate 1 dia).</summary>
    Days1 = 1
}
