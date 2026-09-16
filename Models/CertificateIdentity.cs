using System.Globalization;

namespace CertExpiryMonitor.Models;

/// <summary>Normalizacao da identidade do certificado, independente da persistencia.</summary>
public static class CertificateIdentity
{
    public static string NormalizeThumbprint(string thumbprint)
    {
        ArgumentNullException.ThrowIfNull(thumbprint);
        return new string(thumbprint
            .Where(c => !char.IsWhiteSpace(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.Format)
            .ToArray())
            .ToUpperInvariant();
    }
}
