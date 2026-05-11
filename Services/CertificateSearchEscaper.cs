namespace CertExpiryMonitor.Services;

/// <summary>
/// Escape robusto para texto de busca usado em <c>DataTable.RowFilter</c> com
/// operador <c>LIKE</c>. O parser do <c>DataView.RowFilter</c> trata varios
/// caracteres como metacharacteres alem das aspas:
/// <list type="bullet">
///   <item><c>'</c> — delimitador de string;</item>
///   <item><c>*</c> e <c>%</c> — wildcards (match qualquer);</item>
///   <item><c>[</c> e <c>]</c> — character class brackets;</item>
/// </list>
/// Sem escape adequado, um usuario digitando <c>[</c> na busca dispara
/// <c>SyntaxErrorException</c> nao tratada que sobe ate
/// <c>Application.ThreadException</c>.
/// </summary>
internal static class CertificateSearchEscaper
{
    /// <summary>
    /// Escapa <paramref name="value"/> para ser usado entre <c>%</c> em uma clausula
    /// <c>... LIKE '%{result}%'</c>. Cobre os 5 metacharacteres relevantes.
    /// </summary>
    /// <remarks>
    /// Ordem das substituicoes importa: <c>[</c> precisa ser escapado PRIMEIRO
    /// (e como <c>[[]</c>) porque o escape de outros caracteres usa colchetes.
    /// </remarks>
    internal static string EscapeForRowFilter(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new System.Text.StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '[':
                    sb.Append("[[]");
                    break;
                case ']':
                    sb.Append("[]]");
                    break;
                case '*':
                    sb.Append("[*]");
                    break;
                case '%':
                    sb.Append("[%]");
                    break;
                case '\'':
                    sb.Append("''");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }
}
