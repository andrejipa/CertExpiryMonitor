namespace CertExpiryMonitor.Models;

public enum CertificateReadStatus
{
    Success = 0,
    StoreFailure = 1,
    PartialFailure = 2
}

public sealed record CertificateReadResult(
    IReadOnlyList<CertificateSnapshot> Certificates,
    CertificateReadStatus Status,
    int FailedCertificates = 0)
{
    public bool IsComplete => Status == CertificateReadStatus.Success;

    public static CertificateReadResult Complete(IReadOnlyList<CertificateSnapshot> certificates) =>
        new(certificates, CertificateReadStatus.Success);
}
