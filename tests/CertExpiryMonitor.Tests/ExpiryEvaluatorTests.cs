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
    public void CertificateExpiringTodayNotifiesBucket1()
    {
        var plan = BuildPlan(Certificate("A", 0));

        var item = Assert.Single(plan.DueCertificates);
        Assert.Equal(ExpiryBucket.Days1, item.Bucket);
        Assert.Equal(0, item.DaysRemaining);
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
        var state = StateWith("A", CertificateNotificationState.NotifiedLong);

        var plan = BuildPlan(Certificate("A", 30), state);

        Assert.Empty(plan.DueCertificates);
    }

    [Fact]
    public void ReminderPlanIncludesAlreadyNotifiedCertificate()
    {
        var state = StateWith("A", CertificateNotificationState.NotifiedLong);

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
    public void DismissCertificateIgnoresBlankThumbprint()
    {
        var state = EmptyState();

        _evaluator.DismissCertificate(" \t\r\n", state);

        Assert.Empty(state);
    }

    [Fact]
    public void RestoreCertificateClearsDismissedState()
    {
        var state = StateWith("A", CertificateNotificationState.Dismissed);

        _evaluator.RestoreCertificate("A", state);

        Assert.Equal(CertificateNotificationState.None, state["A"].State);
    }

    [Fact]
    public void RestoreCertificateIgnoresBlankThumbprint()
    {
        var state = EmptyState();

        _evaluator.RestoreCertificate(" \t\r\n", state);

        Assert.Empty(state);
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
    public void BuildPlanIgnoresBlankThumbprint()
    {
        var state = EmptyState();

        var plan = _evaluator.BuildPlan([Certificate(" \t\r\n", 30)], state, Today);

        Assert.Empty(plan.DueCertificates);
        Assert.Empty(state);
    }

    [Fact]
    public void ProgressingToLaterBucketNotifiesAgain()
    {
        var state = StateWith("A", CertificateNotificationState.NotifiedLong);

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

    [Fact]
    public void MarkNotifiedIgnoresBlankThumbprint()
    {
        var state = EmptyState();
        var plan = new NotificationPlan
        {
            DueCertificates =
            [
                new CertificateDueNotification(Certificate(" \t\r\n", 15), ExpiryBucket.Days15, 15)
            ]
        };

        _evaluator.MarkNotified(plan, state, DateTimeOffset.Now);

        Assert.Empty(state);
    }

    [Fact]
    public void PublicMethodsThrowOnNullArguments()
    {
        var state = EmptyState();
        var plan = new NotificationPlan();

        Assert.Throws<ArgumentNullException>(() => _evaluator.BuildPlan(null!, state, Today));
        Assert.Throws<ArgumentNullException>(() => _evaluator.BuildPlan([], null!, Today));
        Assert.Throws<ArgumentNullException>(() => _evaluator.BuildReminderPlan(null!, state, Today));
        Assert.Throws<ArgumentNullException>(() => _evaluator.BuildReminderPlan([], null!, Today));
        Assert.Throws<ArgumentNullException>(() => _evaluator.MarkNotified(null!, state, DateTimeOffset.Now));
        Assert.Throws<ArgumentNullException>(() => _evaluator.MarkNotified(plan, null!, DateTimeOffset.Now));
        Assert.Throws<ArgumentNullException>(() => _evaluator.DismissCertificate(null!, state));
        Assert.Throws<ArgumentNullException>(() => _evaluator.DismissCertificate("A", null!));
        Assert.Throws<ArgumentNullException>(() => _evaluator.DismissCertificates(null!, state));
        Assert.Throws<ArgumentNullException>(() => _evaluator.DismissCertificates([], null!));
        Assert.Throws<ArgumentNullException>(() => _evaluator.RestoreCertificate(null!, state));
        Assert.Throws<ArgumentNullException>(() => _evaluator.RestoreCertificate("A", null!));
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
