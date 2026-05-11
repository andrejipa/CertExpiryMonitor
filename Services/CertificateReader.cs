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
    private readonly FileLogger _logger;

    public CertificateReader(FileLogger logger)
    {
        _logger = logger;
    }

    public virtual IReadOnlyList<CertificateSnapshot> ReadCurrentUserPersonalCertificates()
    {
        var results = new List<CertificateSnapshot>();

        try
        {
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

            foreach (var certificate in store.Certificates)
            {
                try
                {
                    var snapshot = TryCreateSnapshot(certificate);
                    if (snapshot is null)
                    {
                        continue;
                    }

                    results.Add(snapshot);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to inspect one certificate");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to read CurrentUser Personal store");
        }

        return results;
    }

    public bool RemoveFromCurrentUserPersonalStore(string thumbprint)
    {
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
        if (!certificate.HasPrivateKey ||
            string.IsNullOrWhiteSpace(certificate.Thumbprint) ||
            certificate.NotAfter == DateTime.MinValue)
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
}
