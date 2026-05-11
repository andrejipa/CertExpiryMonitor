namespace CertExpiryMonitor.Models;

public sealed record CertificateDueNotification(
    CertificateSnapshot Certificate,
    ExpiryBucket Bucket,
    int DaysRemaining);
