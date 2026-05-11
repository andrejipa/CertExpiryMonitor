namespace CertExpiryMonitor.Models;

public sealed class NotificationPlan
{
    public IReadOnlyList<CertificateDueNotification> DueCertificates { get; init; } = [];

    public bool HasItems => DueCertificates.Count > 0;

    public int Count(ExpiryBucket bucket) => DueCertificates.Count(item => item.Bucket == bucket);
}
