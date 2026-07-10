using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class CertificateReaderTests
{
    private const string A1PolicyOid = "2.16.76.1.2.1.1";
    private const string A3PolicyOid = "2.16.76.1.2.3.1";

    [Fact]
    public void CertificateWithoutPrivateKeyIsIgnored()
    {
        using var certificate = CreateCertificate(hasPrivateKey: false, commonName: "NoPrivateKey", A1PolicyOid);

        var snapshot = CertificateReader.TryCreateSnapshot(certificate);

        Assert.Null(snapshot);
    }

    [Fact]
    public void TryCreateSnapshotThrowsOnNullCertificate()
    {
        Assert.Throws<ArgumentNullException>(() => CertificateReader.TryCreateSnapshot(null!));
    }

    [Fact]
    public void CertificateWithPrivateKeyAndIcpBrasilA1PolicyCreatesSnapshot()
    {
        using var certificate = CreateCertificate(hasPrivateKey: true, commonName: "ValidPrivateKey", A1PolicyOid);

        var snapshot = CertificateReader.TryCreateSnapshot(certificate);

        Assert.NotNull(snapshot);
        Assert.Equal(JsonStateStore.NormalizeThumbprint(certificate.Thumbprint), snapshot!.Thumbprint);
        Assert.Equal(certificate.Subject, snapshot.Subject);
        Assert.Equal(certificate.Issuer, snapshot.Issuer);
        Assert.Equal(certificate.SerialNumber, snapshot.SerialNumber);
        Assert.Equal("ValidPrivateKey", snapshot.SimpleName);
    }

    [Fact]
    public void CertificateWithPrivateKeyButNoIcpBrasilA1PolicyIsIgnored()
    {
        using var certificate = CreateCertificate(hasPrivateKey: true, commonName: "GenericPrivateKey");

        var snapshot = CertificateReader.TryCreateSnapshot(certificate);

        Assert.Null(snapshot);
    }

    [Fact]
    public void CertificateWithPrivateKeyAndIcpBrasilA3PolicyIsIgnored()
    {
        using var certificate = CreateCertificate(hasPrivateKey: true, commonName: "A3PrivateKey", A3PolicyOid);

        var snapshot = CertificateReader.TryCreateSnapshot(certificate);

        Assert.Null(snapshot);
        Assert.False(CertificateReader.HasIcpBrasilA1Policy(certificate));
    }

    [Fact]
    public void CertificateWithPrivateKeyAndMultiplePoliciesAcceptsIcpBrasilA1()
    {
        using var certificate = CreateCertificate(
            hasPrivateKey: true,
            commonName: "MultiplePolicies",
            A3PolicyOid,
            A1PolicyOid);

        Assert.True(CertificateReader.HasIcpBrasilA1Policy(certificate));
        Assert.NotNull(CertificateReader.TryCreateSnapshot(certificate));
    }

    private static X509Certificate2 CreateCertificate(
        bool hasPrivateKey,
        string commonName,
        params string[] policyOids)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={commonName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        if (policyOids.Length > 0)
        {
            request.CertificateExtensions.Add(CreateCertificatePoliciesExtension(policyOids));
        }

        using var certificateWithPrivateKey = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(30));

        return hasPrivateKey
            ? new X509Certificate2(certificateWithPrivateKey.Export(X509ContentType.Pkcs12))
            : new X509Certificate2(certificateWithPrivateKey.Export(X509ContentType.Cert));
    }

    private static X509Extension CreateCertificatePoliciesExtension(params string[] policyOids)
    {
        var writer = new AsnWriter(AsnEncodingRules.DER);
        writer.PushSequence();
        foreach (var policyOid in policyOids)
        {
            writer.PushSequence();
            writer.WriteObjectIdentifier(policyOid);
            writer.PopSequence();
        }
        writer.PopSequence();

        return new X509Extension(new Oid("2.5.29.32"), writer.Encode(), critical: false);
    }
}
