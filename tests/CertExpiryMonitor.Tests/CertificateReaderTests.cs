using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class CertificateReaderTests
{
    [Fact]
    public void CertificateWithoutPrivateKeyIsIgnored()
    {
        using var certificate = CreateCertificateWithoutPrivateKey();

        var snapshot = CertificateReader.TryCreateSnapshot(certificate);

        Assert.Null(snapshot);
    }

    private static X509Certificate2 CreateCertificateWithoutPrivateKey()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=NoPrivateKey",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        using var certificateWithPrivateKey = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));

        return new X509Certificate2(certificateWithPrivateKey.Export(X509ContentType.Cert));
    }
}
