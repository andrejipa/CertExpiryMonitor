namespace CertExpiryMonitor.Models;

public sealed class CertificateStateRecord
{
    public string Thumbprint { get; set; } = string.Empty;
    public DateTime NotAfter { get; set; }
    public CertificateNotificationState State { get; set; } = CertificateNotificationState.None;
    public DateTimeOffset? LastNotifiedAt { get; set; }
}
