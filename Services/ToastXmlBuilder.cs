using CertExpiryMonitor.Models;

namespace CertExpiryMonitor.Services;

internal static class ToastXmlBuilder
{
    internal static string Build(NotificationPlan plan, ExpiryThresholds thresholds, bool soundEnabled)
    {
        var line = string.Join(" | ", new[]
        {
            FormatBucket($"{thresholds.Level30}d", plan.Count(ExpiryBucket.Days30)),
            FormatBucket($"{thresholds.Level15}d", plan.Count(ExpiryBucket.Days15)),
            FormatBucket($"{thresholds.Level7}d",  plan.Count(ExpiryBucket.Days7)),
            FormatBucket($"{thresholds.Level1}d",  plan.Count(ExpiryBucket.Days1))
        }.Where(text => text.Length > 0));

        var title = EscapeXml("Certificados vencendo");
        var summary = EscapeXml(plan.DueCertificates.Count == 1
            ? "1 certificado requer atenção."
            : $"{plan.DueCertificates.Count} certificados requerem atenção.");
        var buckets = EscapeXml(line);
        var audio = soundEnabled
            ? string.Empty
            : """

              <audio silent="true"/>
            """;
        return $"""
        <toast scenario="urgent" activationType="protocol" launch="{ToastNotifierService.DetailsProtocolUri}">
          <visual>
            <binding template="ToastGeneric">
              <text hint-maxLines="1">{title}</text>
              <text>{summary}</text>
              <text>{buckets}</text>
            </binding>
          </visual>{audio}
        </toast>
        """;
    }

    private static string FormatBucket(string label, int count)
    {
        return count == 0 ? string.Empty : $"{label}: {count}";
    }

    private static string EscapeXml(string value)
    {
        return System.Security.SecurityElement.Escape(value) ?? string.Empty;
    }
}
