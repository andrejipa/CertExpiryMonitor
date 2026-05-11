namespace CertExpiryMonitor.Services;

/// <summary>
/// Utilitarios para parsing de nome e numero de documento a partir do CN
/// de certificados A1 brasileiros. Extraidos de DetailsForm para permitir
/// testes unitarios isolados.
/// </summary>
internal static class CertificateDocumentHelpers
{
    /// <summary>
    /// Extrai CN do Subject DN como fallback quando SimpleName nao esta disponivel.
    /// Usado apenas para snapshots legados sem o campo SimpleName preenchido.
    /// </summary>
    internal static string GetCommonNameFallback(string distinguishedName)
    {
        const string prefix = "CN=";
        var start = distinguishedName.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return distinguishedName;

        start += prefix.Length;
        var end   = distinguishedName.IndexOf(',', start);
        var value = end < 0
            ? distinguishedName[start..]
            : distinguishedName[start..end];

        return value.Trim();
    }

    /// <summary>
    /// Separa nome e numero de documento do CN. Convenção: "NOME COMPLETO:DOCUMENTO".
    /// </summary>
    internal static (string Name, string Document) ParseHolder(string commonName)
    {
        var sep = commonName.LastIndexOf(':');
        if (sep < 0) return (commonName.Trim(), string.Empty);

        return (commonName[..sep].Trim(), commonName[(sep + 1)..].Trim());
    }

    /// <summary>
    /// Formata CPF (11 digitos) ou CNPJ (14 digitos) com pontuacao padrao.
    /// Retorna o valor original se nao reconhecer o tamanho ou nao for numerico.
    /// </summary>
    internal static string FormatDocument(string document)
    {
        var digits = new string(document.Where(char.IsDigit).ToArray());

        if (digits.Length == 11 && ulong.TryParse(digits, out var cpf))
        {
            return cpf.ToString(@"000\.000\.000\-00");
        }

        if (digits.Length == 14 && ulong.TryParse(digits, out var cnpj))
        {
            return cnpj.ToString(@"00\.000\.000\/0000\-00");
        }

        return document;
    }
}
