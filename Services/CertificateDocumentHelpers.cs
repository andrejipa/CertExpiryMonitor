using System.Globalization;

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
        var start = IndexOfAttribute(distinguishedName, prefix);
        if (start < 0) return distinguishedName;

        start += prefix.Length;
        var end   = IndexOfUnescapedComma(distinguishedName, start);
        var value = end < 0
            ? distinguishedName[start..]
            : distinguishedName[start..end];

        return UnescapeDistinguishedNameValue(value).Trim();
    }

    /// <summary>
    /// Separa nome e numero de documento do CN. Convenção: "NOME COMPLETO:DOCUMENTO".
    /// Trunca em 256 chars para evitar que CNs patologicos (com Unicode RTL,
    /// zero-width chars ou 10K caracteres) degradem o paint do DataGridView.
    /// </summary>
    internal static (string Name, string Document) ParseHolder(string commonName)
    {
        const int MaxNameChars = 256;
        var sep = commonName.LastIndexOf(':');
        if (sep <= 0) return (Truncate(commonName.Trim(), MaxNameChars), string.Empty);

        return (Truncate(commonName[..sep].Trim(), MaxNameChars), commonName[(sep + 1)..].Trim());
    }

    private static string Truncate(string value, int maxChars) =>
        value.Length <= maxChars ? value : value[..maxChars] + "…";

    private static int IndexOfUnescapedComma(string value, int start)
    {
        for (var i = start; i < value.Length; i++)
        {
            if (value[i] == ',' && CountPreviousBackslashes(value, i) % 2 == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static int IndexOfAttribute(string distinguishedName, string prefix)
    {
        var start = 0;
        while (start < distinguishedName.Length)
        {
            var index = distinguishedName.IndexOf(prefix, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0) return -1;
            if (IsStartBoundary(distinguishedName, index) || IsAttributeBoundary(distinguishedName, index))
            {
                return index;
            }

            start = index + prefix.Length;
        }

        return -1;
    }

    private static bool IsStartBoundary(string value, int index)
    {
        for (var i = 0; i < index; i++)
        {
            if (!char.IsWhiteSpace(value[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAttributeBoundary(string value, int index)
    {
        var previous = index - 1;
        while (previous >= 0 && char.IsWhiteSpace(value[previous]))
        {
            previous--;
        }

        return previous >= 0 &&
            value[previous] == ',' &&
            CountPreviousBackslashes(value, previous) % 2 == 0;
    }

    private static int CountPreviousBackslashes(string value, int index)
    {
        var count = 0;
        for (var i = index - 1; i >= 0 && value[i] == '\\'; i--)
        {
            count++;
        }

        return count;
    }

    private static string UnescapeDistinguishedNameValue(string value)
    {
        return value
            .Replace(@"\,", ",", StringComparison.Ordinal)
            .Replace(@"\+", "+", StringComparison.Ordinal)
            .Replace(@"\""", "\"", StringComparison.Ordinal)
            .Replace(@"\\", @"\", StringComparison.Ordinal);
    }

    /// <summary>
    /// Formata CPF (11 digitos) ou CNPJ (14 digitos) com pontuacao padrao.
    /// Retorna o valor original se nao reconhecer o tamanho ou nao for numerico.
    /// </summary>
    internal static string FormatDocument(string document)
    {
        if (!IsDocumentLike(document))
        {
            return document;
        }

        var digits = new string(document.Where(char.IsDigit).ToArray());

        if (digits.Length == 11 && ulong.TryParse(digits, out var cpf))
        {
            return cpf.ToString(@"000\.000\.000\-00", CultureInfo.InvariantCulture);
        }

        if (digits.Length == 14 && ulong.TryParse(digits, out var cnpj))
        {
            return cnpj.ToString(@"00\.000\.000\/0000\-00", CultureInfo.InvariantCulture);
        }

        return document;
    }

    private static bool IsDocumentLike(string document)
    {
        return document.All(c =>
            char.IsDigit(c) ||
            char.IsWhiteSpace(c) ||
            c is '.' or '-' or '/');
    }
}
