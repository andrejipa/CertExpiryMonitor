using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class CertificateDocumentHelpersTests
{
    // -------------------------------------------------------------------------
    // FormatDocument — CPF
    // -------------------------------------------------------------------------

    [Fact]
    public void FormatDocument_Cpf_Returns11DigitsFormatted()
    {
        Assert.Equal("123.456.789-09", CertificateDocumentHelpers.FormatDocument("12345678909"));
    }

    [Fact]
    public void FormatDocument_CpfWithPunctuation_ReformatsCorrectly()
    {
        // Ja vem com pontuacao — deve ser re-formatado a partir dos digitos
        Assert.Equal("123.456.789-09", CertificateDocumentHelpers.FormatDocument("123.456.789-09"));
    }

    [Fact]
    public void FormatDocument_CpfLeadingZero_PreservesLeadingZero()
    {
        Assert.Equal("012.345.678-90", CertificateDocumentHelpers.FormatDocument("01234567890"));
    }

    // -------------------------------------------------------------------------
    // FormatDocument — CNPJ
    // -------------------------------------------------------------------------

    [Fact]
    public void FormatDocument_Cnpj_Returns14DigitsFormatted()
    {
        Assert.Equal("12.345.678/0001-95", CertificateDocumentHelpers.FormatDocument("12345678000195"));
    }

    [Fact]
    public void FormatDocument_CnpjWithPunctuation_ReformatsCorrectly()
    {
        Assert.Equal("12.345.678/0001-95", CertificateDocumentHelpers.FormatDocument("12.345.678/0001-95"));
    }

    // -------------------------------------------------------------------------
    // FormatDocument — outros casos
    // -------------------------------------------------------------------------

    [Fact]
    public void FormatDocument_EmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, CertificateDocumentHelpers.FormatDocument(string.Empty));
    }

    [Fact]
    public void FormatDocument_UnrecognizedLength_ReturnsOriginal()
    {
        Assert.Equal("12345", CertificateDocumentHelpers.FormatDocument("12345"));
    }

    [Fact]
    public void FormatDocument_NonDigits_ReturnsOriginalWhenNoRecognizableLength()
    {
        Assert.Equal("ABC-XYZ", CertificateDocumentHelpers.FormatDocument("ABC-XYZ"));
    }

    // -------------------------------------------------------------------------
    // ParseHolder
    // -------------------------------------------------------------------------

    [Fact]
    public void ParseHolder_WithColonSeparator_SplitsNameAndDocument()
    {
        var (name, doc) = CertificateDocumentHelpers.ParseHolder("JOAO DA SILVA:12345678909");

        Assert.Equal("JOAO DA SILVA", name);
        Assert.Equal("12345678909", doc);
    }

    [Fact]
    public void ParseHolder_WithoutColon_ReturnsFullNameAndEmptyDocument()
    {
        var (name, doc) = CertificateDocumentHelpers.ParseHolder("MARIA APARECIDA");

        Assert.Equal("MARIA APARECIDA", name);
        Assert.Equal(string.Empty, doc);
    }

    [Fact]
    public void ParseHolder_UsesLastColonAsSeparator()
    {
        // Nomes com ":" no meio — usa o ULTIMO separador
        var (name, doc) = CertificateDocumentHelpers.ParseHolder("EMPRESA: RAZAO SOCIAL:12345678000195");

        Assert.Equal("EMPRESA: RAZAO SOCIAL", name);
        Assert.Equal("12345678000195", doc);
    }

    [Fact]
    public void ParseHolder_TrimsWhitespace()
    {
        var (name, doc) = CertificateDocumentHelpers.ParseHolder("  JOSE SOUZA  :  98765432100  ");

        Assert.Equal("JOSE SOUZA", name);
        Assert.Equal("98765432100", doc);
    }

    // -------------------------------------------------------------------------
    // GetCommonNameFallback
    // -------------------------------------------------------------------------

    [Fact]
    public void GetCommonNameFallback_SimpleCn_ExtractsCnValue()
    {
        var cn = CertificateDocumentHelpers.GetCommonNameFallback("CN=JOAO DA SILVA, OU=PF A1, O=ICP-Brasil");

        Assert.Equal("JOAO DA SILVA", cn);
    }

    [Fact]
    public void GetCommonNameFallback_CnAtEnd_ExtractsCnValue()
    {
        var cn = CertificateDocumentHelpers.GetCommonNameFallback("O=ICP-Brasil, CN=MARIA JOSE");

        Assert.Equal("MARIA JOSE", cn);
    }

    [Fact]
    public void GetCommonNameFallback_NoCn_ReturnsOriginalString()
    {
        var input = "O=ICP-Brasil, OU=AC";
        var cn    = CertificateDocumentHelpers.GetCommonNameFallback(input);

        Assert.Equal(input, cn);
    }

    [Fact]
    public void GetCommonNameFallback_CaseInsensitive()
    {
        var cn = CertificateDocumentHelpers.GetCommonNameFallback("cn=NOME TESTE, O=ORG");

        Assert.Equal("NOME TESTE", cn);
    }
}
