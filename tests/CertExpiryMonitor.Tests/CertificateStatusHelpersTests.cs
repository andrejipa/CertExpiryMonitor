using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class CertificateStatusHelpersTests
{
    private static ExpiryThresholds Default() => new ExpiryThresholds().Normalized();

    // -------------------------------------------------------------------------
    // GetStatusText
    // -------------------------------------------------------------------------

    [Fact]
    public void GetStatusText_DismissedAlwaysReturnsIgnorado()
    {
        // Mesmo com 999 dias restantes, Dismissed prevalece.
        var text = CertificateStatusHelpers.GetStatusText(999, CertificateNotificationState.Dismissed, Default());
        Assert.Equal("Ignorado", text);
    }

    [Fact]
    public void GetStatusText_NegativeDaysReturnsVencido()
    {
        var text = CertificateStatusHelpers.GetStatusText(-1, null, Default());
        Assert.Equal("Vencido", text);
    }

    [Fact]
    public void GetStatusText_AtLevel30BoundaryReturnsProximo()
    {
        var text = CertificateStatusHelpers.GetStatusText(30, null, Default());
        Assert.Equal("Próximo do vencimento", text);
    }

    [Fact]
    public void GetStatusText_JustAboveLevel30ReturnsValido()
    {
        var text = CertificateStatusHelpers.GetStatusText(31, null, Default());
        Assert.Equal("Válido", text);
    }

    [Fact]
    public void GetStatusText_RespectsCustomLevel30()
    {
        // Threshold customizado: Level30 = 45.
        var thresholds = new ExpiryThresholds { Level1 = 1, Level7 = 5, Level15 = 15, Level30 = 45 }.Normalized();

        Assert.Equal("Próximo do vencimento", CertificateStatusHelpers.GetStatusText(45, null, thresholds));
        Assert.Equal("Válido",                CertificateStatusHelpers.GetStatusText(46, null, thresholds));
    }

    [Fact]
    public void GetStatusText_NotifiedStatesDoNotOverrideRange()
    {
        // Estados Notified* sao apenas controle de progressao; o texto e
        // computado a partir de daysRemaining, nao do estado.
        var text = CertificateStatusHelpers.GetStatusText(10, CertificateNotificationState.NotifiedMedium, Default());
        Assert.Equal("Próximo do vencimento", text);
    }

    // -------------------------------------------------------------------------
    // GetStatusCategory
    // -------------------------------------------------------------------------

    [Fact]
    public void GetStatusCategory_DismissedAlwaysReturnsDismissed()
    {
        var category = CertificateStatusHelpers.GetStatusCategory(5, CertificateNotificationState.Dismissed, Default());
        Assert.Equal("Dismissed", category);
    }

    [Fact]
    public void GetStatusCategory_NegativeDaysReturnsExpired()
    {
        Assert.Equal("Expired", CertificateStatusHelpers.GetStatusCategory(-1,   null, Default()));
        Assert.Equal("Expired", CertificateStatusHelpers.GetStatusCategory(-100, null, Default()));
    }

    [Theory]
    [InlineData(0, "Critical")]
    [InlineData(1, "Critical")]
    [InlineData(7, "Critical")]
    public void GetStatusCategory_DaysAtOrBelowLevel7IsCritical(int days, string expected)
    {
        Assert.Equal(expected, CertificateStatusHelpers.GetStatusCategory(days, null, Default()));
    }

    [Theory]
    [InlineData(8,  "Warning")]
    [InlineData(15, "Warning")]
    [InlineData(30, "Warning")]
    public void GetStatusCategory_DaysBetweenLevel7AndLevel30IsWarning(int days, string expected)
    {
        Assert.Equal(expected, CertificateStatusHelpers.GetStatusCategory(days, null, Default()));
    }

    [Theory]
    [InlineData(31)]
    [InlineData(60)]
    [InlineData(365)]
    public void GetStatusCategory_DaysAboveLevel30IsValid(int days)
    {
        Assert.Equal("Valid", CertificateStatusHelpers.GetStatusCategory(days, null, Default()));
    }

    [Fact]
    public void GetStatusCategory_RespectsCustomLevel7()
    {
        // Bug historico (corrigido): com Level7=5, um cert a 6 dias era classificado
        // como Critical pelo hardcode '<= 7'. Agora deve ser Warning.
        var thresholds = new ExpiryThresholds { Level1 = 1, Level7 = 5, Level15 = 15, Level30 = 45 }.Normalized();

        Assert.Equal("Critical", CertificateStatusHelpers.GetStatusCategory(5, null, thresholds));
        Assert.Equal("Warning",  CertificateStatusHelpers.GetStatusCategory(6, null, thresholds));
        Assert.Equal("Warning",  CertificateStatusHelpers.GetStatusCategory(7, null, thresholds));
    }

    [Fact]
    public void GetStatusCategory_RespectsCustomLevel30()
    {
        // Bug historico (corrigido): com Level30=45, um cert a 40 dias era classificado
        // como Valid pelo hardcode '<= 30'. Agora deve ser Warning.
        var thresholds = new ExpiryThresholds { Level1 = 1, Level7 = 5, Level15 = 15, Level30 = 45 }.Normalized();

        Assert.Equal("Warning", CertificateStatusHelpers.GetStatusCategory(40, null, thresholds));
        Assert.Equal("Warning", CertificateStatusHelpers.GetStatusCategory(45, null, thresholds));
        Assert.Equal("Valid",   CertificateStatusHelpers.GetStatusCategory(46, null, thresholds));
    }

    [Fact]
    public void GetStatusCategory_NotifiedStatesDoNotOverrideRange()
    {
        // Mesmo cert ja notificado em bucket Days15 ainda recebe categoria
        // computada por daysRemaining (que pode ter mudado, no caso de cert renovado
        // com mesmo thumbprint — cenario raro mas possivel).
        var category = CertificateStatusHelpers.GetStatusCategory(10, CertificateNotificationState.NotifiedMedium, Default());
        Assert.Equal("Warning", category);
    }

    [Fact]
    public void GetStatusCategory_AtZeroDaysIsCritical()
    {
        // Edge case: cert vence hoje (daysRemaining = 0). Nao e Expired ainda
        // (daysRemaining < 0 sera amanha), mas e Critical (<= Level7).
        Assert.Equal("Critical", CertificateStatusHelpers.GetStatusCategory(0, null, Default()));
    }
}
