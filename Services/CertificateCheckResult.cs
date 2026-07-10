using CertExpiryMonitor.Models;

namespace CertExpiryMonitor.Services;

public enum CertificateCheckStatus
{
    Skipped = 0,
    Completed = 1,
    ReadFailed = 2,
    StatePersistFailed = 3,
    Failed = 4
}

public sealed record CertificateCheckResult
{
    public CertificateCheckResult(CertificateCheckStatus status, NotificationPlan? plan = null)
    {
        Status = status;
        Plan = plan;
    }

    public CertificateCheckStatus Status { get; }
    public NotificationPlan? Plan { get; }
    public bool Ran => Status == CertificateCheckStatus.Completed;
    public bool ShouldRetry => Status is CertificateCheckStatus.ReadFailed or CertificateCheckStatus.StatePersistFailed or CertificateCheckStatus.Failed;

    public void Deconstruct(out bool ran, out NotificationPlan? plan)
    {
        ran = Ran;
        plan = Plan;
    }
}
