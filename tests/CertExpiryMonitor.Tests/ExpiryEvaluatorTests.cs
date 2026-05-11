using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class ExpiryEvaluatorTests
{
    private static readonly DateOnly Today = new(2026, 4, 27);
    private readonly ExpiryEvaluator _evaluator = new();

    [Fact]
    public void CertificateWith31DaysDoesNotNotify()
    {
        var plan = BuildPlan(Certificate("A", 31));

        Assert.Empty(plan.DueCertificates);
    }

    [Fact]
    public void CertificateWith30DaysNotifiesBucket30()
    {
        var plan = BuildPlan(Certificate("A", 30));

        var item = Assert.Single(plan.DueCertificates);
        Assert.Equal(ExpiryBucket.Days30, item.Bucket);
    }

    [Fact]
    public void CertificateWith15DaysNotifiesBucket15()
    {
        var plan = BuildPlan(Certificate("A", 15));

        var item = Assert.Single(plan.DueCertificates);
        Assert.Equal(ExpiryBucket.Days15, item.Bucket);
    }

    [Fact]
    public void CertificateWith7DaysNotifiesBucket7()
    {
        var plan = BuildPlan(Certificate("A", 7));

        var item = Assert.Single(plan.DueCertificates);
        Assert.Equal(ExpiryBucket.Days7, item.Bucket);
    }

    [Fact]
    public void CertificateWith1DayNotifiesBucket1()
    {
        var plan = BuildPlan(Certificate("A", 1));

        var item = Assert.Single(plan.DueCertificates);
        Assert.Equal(ExpiryBucket.Days1, item.Bucket);
    }

    [Fact]
    public void ExpiredCertificateDoesNotNotify()
    {
        var plan = BuildPlan(Certificate("A", -1));

        Assert.Empty(plan.DueCertificates);
    }

    [Fact]
    public void AlreadyNotifiedSameBucketDoesNotRepeat()
    {
        var state = StateWith("A", CertificateNotificationState.Notified30);

        var plan = BuildPlan(Certificate("A", 30), state);

        Assert.Empty(plan.DueCertificates);
    }

    [Fact]
    public void ReminderPlanIncludesAlreadyNotifiedCertificate()
    {
        var state = StateWith("A", CertificateNotificationState.Notified30);

        var plan = _evaluator.BuildReminderPlan([Certificate("A", 30)], state, Today);

        Assert.Single(plan.DueCertificates);
    }

    [Fact]
    public void DismissedCertificateDoesNotNotify()
    {
        var state = StateWith("A", CertificateNotificationState.Dismissed);

        var plan = BuildPlan(Certificate("A", 30), state);

        Assert.Empty(plan.DueCertificates);
    }

    [Fact]
    public void DismissCertificateCreatesDismissedStateWhenMissing()
    {
        var state = EmptyState();

        _evaluator.DismissCertificate("A", state);

        var record = Assert.Single(state.Values);
        Assert.Equal("A", record.Thumbprint);
        Assert.Equal(CertificateNotificationState.Dismissed, record.State);
    }

    [Fact]
    public void RestoreCertificateClearsDismissedState()
    {
        var state = StateWith("A", CertificateNotificationState.Dismissed);

        _evaluator.RestoreCertificate("A", state);

        Assert.Equal(CertificateNotificationState.None, state["A"].State);
    }

    [Fact]
    public void DuplicateThumbprintDoesNotCreateDuplicateAlerts()
    {
        var certificate = Certificate("A", 30);
        var duplicate = certificate with { Subject = "CN=Duplicate" };

        var plan = _evaluator.BuildPlan(
            [certificate, duplicate],
            EmptyState(),
            Today);

        Assert.Single(plan.DueCertificates);
    }

    [Fact]
    public void ProgressingToLaterBucketNotifiesAgain()
    {
        var state = StateWith("A", CertificateNotificationState.Notified30);

        var plan = BuildPlan(Certificate("A", 15), state);

        var item = Assert.Single(plan.DueCertificates);
        Assert.Equal(ExpiryBucket.Days15, item.Bucket);
    }

    [Fact]
    public void MarkNotifiedDoesNotOverwriteDismissedState()
    {
        var state = EmptyState();
        var certificate = Certificate("A", 15);
        var plan = _evaluator.BuildReminderPlan([certificate], state, Today);
        _evaluator.DismissCertificate("A", state);

        _evaluator.MarkNotified(plan, state, DateTimeOffset.Now);

        Assert.Equal(CertificateNotificationState.Dismissed, state["A"].State);
    }

    private NotificationPlan BuildPlan(
        CertificateSnapshot certificate,
        Dictionary<string, CertificateStateRecord>? state = null)
    {
        return _evaluator.BuildPlan([certificate], state ?? EmptyState(), Today);
    }

    private static Dictionary<string, CertificateStateRecord> EmptyState()
    {
        return new Dictionary<string, CertificateStateRecord>(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, CertificateStateRecord> StateWith(
        string thumbprint,
        CertificateNotificationState notificationState)
    {
        var certificate = Certificate(thumbprint, 30);
        return new Dictionary<string, CertificateStateRecord>(StringComparer.OrdinalIgnoreCase)
        {
            [thumbprint] = new()
            {
                Thumbprint = thumbprint,
                NotAfter = certificate.NotAfter,
                State = notificationState
            }
        };
    }

    private static CertificateSnapshot Certificate(string thumbprint, int daysFromToday)
    {
        return new CertificateSnapshot(
            thumbprint,
            $"CN={thumbprint}",
            "CN=Issuer",
            Today.ToDateTime(TimeOnly.MinValue).AddDays(daysFromToday),
            $"SERIAL-{thumbprint}");
    }
}
