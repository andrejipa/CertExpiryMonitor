namespace CertExpiryMonitor.Models;

public sealed record CertificateSnapshot(
    string Thumbprint,
    string Subject,
    string Issuer,
    DateTime NotAfter,
    string SerialNumber,
    string SimpleName = "");
