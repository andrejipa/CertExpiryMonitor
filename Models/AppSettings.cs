namespace CertExpiryMonitor.Models;

public sealed class AppSettings
{
    public TimeSpan DailyCheckTime { get; set; } = TimeSpan.FromHours(9);
    public DateOnly? LastCheckDate { get; set; }
    public int InitialDelayMinutes { get; set; } = 5;
    public bool StartupEnabled { get; set; } = true;
    public bool NotificationSoundEnabled { get; set; } = true;
    public string LastCertificateSnapshotHash { get; set; } = string.Empty;
    public ExpiryThresholds Thresholds { get; set; } = new();
}
