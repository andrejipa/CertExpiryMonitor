using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CertExpiryMonitor.Services;
using CertExpiryMonitor.Models;
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

    [Fact]
    public void StoreOpenFailureIsReportedInsteadOfReturningSuccessfulEmptyList()
    {
        var root = Path.Combine(Path.GetTempPath(), $"CertificateReader-{Guid.NewGuid():N}");
        var paths = new AppPaths(root);
        try
        {
            var reader = new CertificateReader(
                new FileLogger(paths),
                _ => throw new IOException("store unavailable"),
                CertificateReader.TryCreateSnapshot);

            var result = reader.ReadCurrentUserPersonalCertificates();

            Assert.Equal(CertificateReadStatus.StoreFailure, result.Status);
            Assert.False(result.IsComplete);
            Assert.Empty(result.Certificates);
            Assert.Equal(1, result.FailedCertificates);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void OneCertificateInspectionFailureProducesPartialResultWithSuccessfulItems()
    {
        using var first = CreateCertificate(hasPrivateKey: true, commonName: "First", A1PolicyOid);
        using var second = CreateCertificate(hasPrivateKey: true, commonName: "Second", A1PolicyOid);
        var root = Path.Combine(Path.GetTempPath(), $"CertificateReader-{Guid.NewGuid():N}");
        var paths = new AppPaths(root);
        try
        {
            var inspected = 0;
            var reader = new CertificateReader(
                new FileLogger(paths),
                inspect =>
                {
                    inspect(first);
                    inspect(second);
                },
                certificate => ++inspected == 1
                    ? CertificateReader.TryCreateSnapshot(certificate)
                    : throw new CryptographicException("broken certificate"));

            var result = reader.ReadCurrentUserPersonalCertificates();

            Assert.Equal(CertificateReadStatus.PartialFailure, result.Status);
            Assert.Equal(1, result.FailedCertificates);
            Assert.Single(result.Certificates);
            Assert.Equal("First", result.Certificates[0].SimpleName);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void SuccessfulScanReturnsCompleteResultWithoutFailures()
    {
        using var certificate = CreateCertificate(hasPrivateKey: true, commonName: "Complete", A1PolicyOid);
        var root = Path.Combine(Path.GetTempPath(), $"CertificateReader-{Guid.NewGuid():N}");
        try
        {
            var reader = new CertificateReader(
                new FileLogger(new AppPaths(root)),
                inspect => inspect(certificate),
                CertificateReader.TryCreateSnapshot);

            var result = reader.ReadCurrentUserPersonalCertificates();

            Assert.Equal(CertificateReadStatus.Success, result.Status);
            Assert.True(result.IsComplete);
            Assert.Equal(0, result.FailedCertificates);
            Assert.Single(result.Certificates);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
    }

    [Fact]
    public void DuplicateThumbprintKeepsCertificateWithLatestExpiration()
    {
        using var certificate = CreateCertificate(hasPrivateKey: true, commonName: "Duplicate", A1PolicyOid);
        var root = Path.Combine(Path.GetTempPath(), $"CertificateReader-{Guid.NewGuid():N}");
        var calls = 0;
        try
        {
            var reader = new CertificateReader(
                new FileLogger(new AppPaths(root)),
                inspect =>
                {
                    inspect(certificate);
                    inspect(certificate);
                },
                _ => new CertificateSnapshot(
                    "AABBCC",
                    "CN=Duplicate",
                    "CN=Issuer",
                    DateTime.Today.AddDays(++calls == 1 ? 5 : 30),
                    calls.ToString()));

            var result = reader.ReadCurrentUserPersonalCertificates();

            var snapshot = Assert.Single(result.Certificates);
            Assert.Equal(DateTime.Today.AddDays(30), snapshot.NotAfter);
            Assert.Equal("2", snapshot.SerialNumber);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort */ }
        }
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
