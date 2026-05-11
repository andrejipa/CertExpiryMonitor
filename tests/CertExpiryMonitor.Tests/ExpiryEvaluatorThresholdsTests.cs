using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

/// <summary>
/// Testa o comportamento do ExpiryEvaluator quando thresholds customizados sao fornecidos.
/// Complementa ExpiryEvaluatorTests.cs que usa thresholds padrao.
/// </summary>
public sealed class ExpiryEvaluatorThresholdsTests
{
    private static readonly DateOnly Today = new(2026, 4, 27);
    private readonly ExpiryEvaluator _evaluator = new();

    // -------------------------------------------------------------------------
    // Selecao de bucket com thresholds customizados
    // -------------------------------------------------------------------------

    [Fact]
    public void CertificateAtCustomLevel30BoundaryNotifiesBucket30()
    {
        // Threshold Level30 = 45 dias: um cert com 45 dias deve cair no bucket Days30
        var thresholds = new ExpiryThresholds { Level1 = 1, Level7 = 5, Level15 = 10, Level30 = 45 };

        var plan = _evaluator.BuildPlan([Cert("A", 45)], EmptyState(), Today, thresholds);

        var item = Assert.Single(plan.DueCertificates);
        Assert.Equal(ExpiryBucket.Days30, item.Bucket);
    }

    [Fact]
    public void CertificateJustAboveCustomLevel30DoesNotNotify()
    {
        var thresholds = new ExpiryThresholds { Level1 = 1, Level7 = 5, Level15 = 10, Level30 = 45 };

        var plan = _evaluator.BuildPlan([Cert("A", 46)], EmptyState(), Today, thresholds);

        Assert.Empty(plan.DueCertificates);
    }

    [Fact]
    public void CertificateAtCustomLevel7BoundaryNotifiesBucket7()
    {
        // Level7 = 5 dias: cert com 5 dias deve cair no bucket Days7
        var thresholds = new ExpiryThresholds { Level1 = 1, Level7 = 5, Level15 = 10, Level30 = 45 };

        var plan = _evaluator.BuildPlan([Cert("A", 5)], EmptyState(), Today, thresholds);

        var item = Assert.Single(plan.DueCertificates);
        Assert.Equal(ExpiryBucket.Days7, item.Bucket);
    }

    [Fact]
    public void CertificateAtCustomLevel1BoundaryNotifiesBucket1()
    {
        var thresholds = new ExpiryThresholds { Level1 = 3, Level7 = 7, Level15 = 15, Level30 = 30 };

        var plan = _evaluator.BuildPlan([Cert("A", 3)], EmptyState(), Today, thresholds);

        var item = Assert.Single(plan.DueCertificates);
        Assert.Equal(ExpiryBucket.Days1, item.Bucket);
    }

    // -------------------------------------------------------------------------
    // Default (null) thresholds sao equivalentes aos valores padrao
    // -------------------------------------------------------------------------

    [Fact]
    public void NullThresholdsEquivalentToDefault()
    {
        var planDefault  = _evaluator.BuildPlan([Cert("A", 30)], EmptyState(), Today);
        var planExplicit = _evaluator.BuildPlan([Cert("A", 30)], EmptyState(), Today, new ExpiryThresholds());

        Assert.Equal(planDefault.DueCertificates.Count, planExplicit.DueCertificates.Count);
        if (planDefault.DueCertificates.Count > 0)
        {
            Assert.Equal(planDefault.DueCertificates[0].Bucket, planExplicit.DueCertificates[0].Bucket);
        }
    }

    // -------------------------------------------------------------------------
    // Progresso entre buckets com thresholds customizados
    // -------------------------------------------------------------------------

    [Fact]
    public void ProgressFromCustomBucket30ToCustomBucket7NotifiesAgain()
    {
        var thresholds = new ExpiryThresholds { Level1 = 1, Level7 = 5, Level15 = 10, Level30 = 45 };

        // Cert ja foi notificado na faixa 30 (45 dias)
        var state = new Dictionary<string, CertificateStateRecord>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = new()
            {
                Thumbprint = "A",
                NotAfter   = Today.ToDateTime(TimeOnly.MinValue).AddDays(5),
                State      = CertificateNotificationState.NotifiedLong
            }
        };

        // Agora tem 5 dias (faixa 7 customizada)
        var plan = _evaluator.BuildPlan([Cert("A", 5)], state, Today, thresholds);

        var item = Assert.Single(plan.DueCertificates);
        Assert.Equal(ExpiryBucket.Days7, item.Bucket);
    }

    // -------------------------------------------------------------------------
    // ReminderPlan respeita thresholds customizados
    // -------------------------------------------------------------------------

    [Fact]
    public void ReminderPlanIncludesAlreadyNotifiedCertificateWithCustomThresholds()
    {
        var thresholds = new ExpiryThresholds { Level1 = 1, Level7 = 5, Level15 = 10, Level30 = 45 };

        var state = new Dictionary<string, CertificateStateRecord>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = new()
            {
                Thumbprint = "A",
                NotAfter   = Today.ToDateTime(TimeOnly.MinValue).AddDays(45),
                State      = CertificateNotificationState.NotifiedLong
            }
        };

        var plan = _evaluator.BuildReminderPlan([Cert("A", 45)], state, Today, thresholds);

        Assert.Single(plan.DueCertificates);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static Dictionary<string, CertificateStateRecord> EmptyState() =>
        new(StringComparer.OrdinalIgnoreCase);

    private static CertificateSnapshot Cert(string thumbprint, int daysFromToday) =>
        new(thumbprint, $"CN={thumbprint}", "CN=Issuer",
            Today.ToDateTime(TimeOnly.MinValue).AddDays(daysFromToday),
            $"SERIAL-{thumbprint}");
}
