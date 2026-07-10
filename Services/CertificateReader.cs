using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;
using CertExpiryMonitor.Models;

namespace CertExpiryMonitor.Services;

/// <remarks>
/// <para>
/// Nao e <c>sealed</c> para permitir override em testes — <see cref="CertificateCheckServiceTests"/>
/// usa uma subclasse com <c>ReadCurrentUserPersonalCertificates</c> override para evitar
/// dependencia do store X.509 real do SO (que e nao-deterministico em CI).
/// </para>
/// <para>
/// Convencao: <c>RemoveFromCurrentUserPersonalStore</c> NAO e virtual porque nenhum teste
/// precisa interceptar; apenas o caminho de leitura tem essa necessidade.
/// </para>
/// </remarks>
public class CertificateReader
{
    private const string CertificatePoliciesOid = "2.5.29.32";
    private const string IcpBrasilA1PolicyPrefix = "2.16.76.1.2.1.";

    private readonly FileLogger _logger;
    private readonly Action<Action<X509Certificate2>> _scanStore;
    private readonly Func<X509Certificate2, CertificateSnapshot?> _snapshotFactory;

    public CertificateReader(FileLogger logger)
        : this(logger, ScanCurrentUserStore, TryCreateSnapshot)
    {
    }

    internal CertificateReader(
        FileLogger logger,
        Action<Action<X509Certificate2>> scanStore,
        Func<X509Certificate2, CertificateSnapshot?> snapshotFactory)
    {
        _logger = logger;
        _scanStore = scanStore;
        _snapshotFactory = snapshotFactory;
    }

    public virtual CertificateReadResult ReadCurrentUserPersonalCertificates()
    {
        var results = new List<CertificateSnapshot>();
        var failedCertificates = 0;

        try
        {
            _scanStore(certificate =>
            {
                try
                {
                    var snapshot = _snapshotFactory(certificate);
                    if (snapshot is null)
                    {
                        return;
                    }

                    results.Add(snapshot);
                }
                catch (Exception ex)
                {
                    failedCertificates++;
                    _logger.Error(ex, "Failed to inspect one certificate");
                }
            });
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to read CurrentUser Personal store");
            return new CertificateReadResult(
                Deduplicate(results),
                CertificateReadStatus.StoreFailure,
                Math.Max(1, failedCertificates));
        }

        // Dedup por thumbprint normalizado. Cenarios raros mas reais:
        // - cert reinstalado (mesmo thumbprint, NotAfter diferente),
        // - store corrompido com duplicatas,
        // - dois containers PFX com mesmo certificado.
        // Sem dedup, DetailsForm exibiria duas linhas para o mesmo cert e dismiss
        // marcaria ambas inconsistentemente. Manter o mais recente (maior NotAfter).
        var certificates = Deduplicate(results);
        return failedCertificates == 0
            ? CertificateReadResult.Complete(certificates)
            : new CertificateReadResult(certificates, CertificateReadStatus.PartialFailure, failedCertificates);
    }

    private static IReadOnlyList<CertificateSnapshot> Deduplicate(IEnumerable<CertificateSnapshot> results) =>
        results
            .GroupBy(c => c.Thumbprint, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(c => c.NotAfter).First())
            .ToList();

    private static void ScanCurrentUserStore(Action<X509Certificate2> inspect)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        foreach (var certificate in store.Certificates)
        {
            inspect(certificate);
        }
    }

    public bool RemoveFromCurrentUserPersonalStore(string thumbprint)
    {
        ArgumentNullException.ThrowIfNull(thumbprint);

        var normalizedThumbprint = JsonStateStore.NormalizeThumbprint(thumbprint);

        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);

            var matches = store.Certificates.Find(
                X509FindType.FindByThumbprint,
                normalizedThumbprint,
                validOnly: false);

            if (matches.Count == 0)
            {
                return false;
            }

            foreach (var certificate in matches)
            {
                store.Remove(certificate);
            }

            _logger.Info("Expired certificate removed from CurrentUser/My store");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to remove certificate from CurrentUser Personal store");
            return false;
        }
    }

    public static CertificateSnapshot? TryCreateSnapshot(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        if (!certificate.HasPrivateKey ||
            string.IsNullOrWhiteSpace(certificate.Thumbprint) ||
            certificate.NotAfter == DateTime.MinValue ||
            !HasIcpBrasilA1Policy(certificate))
        {
            return null;
        }

        // GetNameInfo respeita DN encoding correto (ex.: virgulas escapadas no CN).
        var simpleName = certificate.GetNameInfo(X509NameType.SimpleName, false) ?? string.Empty;

        return new CertificateSnapshot(
            JsonStateStore.NormalizeThumbprint(certificate.Thumbprint),
            certificate.Subject ?? string.Empty,
            certificate.Issuer ?? string.Empty,
            certificate.NotAfter,
            certificate.SerialNumber ?? string.Empty,
            simpleName);
    }

    internal static bool HasIcpBrasilA1Policy(X509Certificate2 certificate)
    {
        ArgumentNullException.ThrowIfNull(certificate);

        foreach (var extension in certificate.Extensions)
        {
            if (!string.Equals(extension.Oid?.Value, CertificatePoliciesOid, StringComparison.Ordinal))
            {
                continue;
            }

            if (ContainsIcpBrasilA1Policy(extension.RawData))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsIcpBrasilA1Policy(byte[] rawData)
    {
        try
        {
            var reader = new AsnReader(rawData, AsnEncodingRules.DER);
            var policies = reader.ReadSequence();
            while (policies.HasData)
            {
                var policyInfo = policies.ReadSequence();
                var policyOid = policyInfo.ReadObjectIdentifier();
                if (policyOid.StartsWith(IcpBrasilA1PolicyPrefix, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }
        catch (AsnContentException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }

        return false;
    }
}
