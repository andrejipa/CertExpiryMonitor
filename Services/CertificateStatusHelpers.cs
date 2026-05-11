using CertExpiryMonitor.Models;

namespace CertExpiryMonitor.Services;

/// <summary>
/// Utilitarios para classificacao visual de certificados na grade do
/// <see cref="DetailsForm"/>. Extraidos do form para permitir testes
/// unitarios isolados com diferentes <see cref="ExpiryThresholds"/>.
/// </summary>
/// <remarks>
/// Estes helpers nao decidem se um certificado sera notificado — isso e
/// responsabilidade de <see cref="ExpiryEvaluator"/>. Aqui apenas se
/// traduz dias restantes + estado em categorias de UI (Critical, Warning,
/// Expired, Valid, Dismissed) e em rotulos de status legivel.
/// </remarks>
internal static class CertificateStatusHelpers
{
    /// <summary>
    /// Retorna o texto exibido na coluna "Status" da grade.
    /// </summary>
    /// <param name="daysRemaining">Dias entre hoje e <c>NotAfter</c>; negativo indica vencido.</param>
    /// <param name="state">Estado persistido do certificado, ou <c>null</c> se nunca foi notificado.</param>
    /// <param name="thresholds">Faixas configuradas (deve estar normalizado).</param>
    internal static string GetStatusText(
        int daysRemaining,
        CertificateNotificationState? state,
        ExpiryThresholds thresholds)
    {
        if (state == CertificateNotificationState.Dismissed) return "Ignorado";
        if (daysRemaining < 0)  return "Vencido";
        return daysRemaining <= thresholds.Level30 ? "Próximo do vencimento" : "Válido";
    }

    /// <summary>
    /// Retorna a categoria de UI usada para colorir a linha e filtrar a grade.
    /// </summary>
    /// <remarks>
    /// Categorias possiveis: <c>"Dismissed"</c>, <c>"Expired"</c>, <c>"Critical"</c>,
    /// <c>"Warning"</c>, <c>"Valid"</c>. Sao strings (nao enum) porque tambem sao usadas
    /// em <c>DataTable.Select("StatusCategory = '...'")</c>.
    /// </remarks>
    internal static string GetStatusCategory(
        int daysRemaining,
        CertificateNotificationState? state,
        ExpiryThresholds thresholds)
    {
        if (state == CertificateNotificationState.Dismissed) return "Dismissed";
        if (daysRemaining < 0)  return "Expired";
        if (daysRemaining <= thresholds.Level7)  return "Critical";
        if (daysRemaining <= thresholds.Level30) return "Warning";
        return "Valid";
    }
}
