namespace CertExpiryMonitor.Services;

internal sealed class DiagnosticEventStoreOptions
{
    public long MaxDatabaseBytes { get; init; } = 25L * 1024L * 1024L;
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(180);
    public Func<DateTimeOffset> UtcNow { get; init; } = () => DateTimeOffset.UtcNow;
}
