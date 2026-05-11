using CertExpiryMonitor.Models;

namespace CertExpiryMonitor.Services;

public sealed class ExpiryEvaluator
{
    public NotificationPlan BuildPlan(
        IReadOnlyList<CertificateSnapshot> certificates,
        Dictionary<string, CertificateStateRecord> state,
        DateOnly today,
        ExpiryThresholds? thresholds = null)
    {
        return BuildPlan(certificates, state, today, thresholds ?? new ExpiryThresholds(), includeAlreadyNotified: false);
    }

    public NotificationPlan BuildReminderPlan(
        IReadOnlyList<CertificateSnapshot> certificates,
        Dictionary<string, CertificateStateRecord> state,
        DateOnly today,
        ExpiryThresholds? thresholds = null)
    {
        return BuildPlan(certificates, state, today, thresholds ?? new ExpiryThresholds(), includeAlreadyNotified: true);
    }

    private NotificationPlan BuildPlan(
        IReadOnlyList<CertificateSnapshot> certificates,
        Dictionary<string, CertificateStateRecord> state,
        DateOnly today,
        ExpiryThresholds thresholds,
        bool includeAlreadyNotified)
    {
        var due = new List<CertificateDueNotification>();
        var processedThumbprints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var certificate in certificates)
        {
            var thumbprint = JsonStateStore.NormalizeThumbprint(certificate.Thumbprint);
            if (!processedThumbprints.Add(thumbprint))
            {
                continue;
            }

            var record = GetOrCreateRecord(state, certificate, thumbprint);
            RefreshRecord(record, certificate);

            if (record.State == CertificateNotificationState.Dismissed)
            {
                continue;
            }

            var expiryDate = DateOnly.FromDateTime(certificate.NotAfter.Date);
            var daysRemaining = expiryDate.DayNumber - today.DayNumber;

            if (daysRemaining < 0)
            {
                continue;
            }

            var bucket = SelectBucket(daysRemaining, thresholds);
            if (bucket is null || (!includeAlreadyNotified && AlreadyNotified(record.State, bucket.Value)))
            {
                continue;
            }

            due.Add(new CertificateDueNotification(certificate, bucket.Value, daysRemaining));
        }

        return new NotificationPlan { DueCertificates = due };
    }

    public void MarkNotified(NotificationPlan plan, Dictionary<string, CertificateStateRecord> state, DateTimeOffset now)
    {
        foreach (var item in plan.DueCertificates)
        {
            var thumbprint = JsonStateStore.NormalizeThumbprint(item.Certificate.Thumbprint);
            var record = GetOrCreateRecord(state, item.Certificate, thumbprint);
            if (record.State == CertificateNotificationState.Dismissed)
            {
                continue;
            }

            record.State = ToState(item.Bucket);
            record.LastNotifiedAt = now;
            RefreshRecord(record, item.Certificate);
        }
    }

    public void DismissCertificate(string thumbprint, Dictionary<string, CertificateStateRecord> state)
    {
        var normalized = JsonStateStore.NormalizeThumbprint(thumbprint);
        if (!state.TryGetValue(normalized, out var record))
        {
            record = new CertificateStateRecord { Thumbprint = normalized };
            state[normalized] = record;
        }

        record.State = CertificateNotificationState.Dismissed;
    }

    public void DismissCertificates(IEnumerable<string> thumbprints, Dictionary<string, CertificateStateRecord> state)
    {
        foreach (var thumbprint in thumbprints)
        {
            DismissCertificate(thumbprint, state);
        }
    }

    public void RestoreCertificate(string thumbprint, Dictionary<string, CertificateStateRecord> state)
    {
        var normalized = JsonStateStore.NormalizeThumbprint(thumbprint);
        if (state.TryGetValue(normalized, out var record) &&
            record.State == CertificateNotificationState.Dismissed)
        {
            record.State = CertificateNotificationState.None;
        }
    }

    private static CertificateStateRecord GetOrCreateRecord(
        Dictionary<string, CertificateStateRecord> state,
        CertificateSnapshot certificate,
        string thumbprint)
    {
        if (state.TryGetValue(thumbprint, out var existing))
        {
            return existing;
        }

        var created = new CertificateStateRecord { Thumbprint = thumbprint };
        RefreshRecord(created, certificate);
        state[thumbprint] = created;
        return created;
    }

    private static void RefreshRecord(CertificateStateRecord record, CertificateSnapshot certificate)
    {
        record.Thumbprint = JsonStateStore.NormalizeThumbprint(certificate.Thumbprint);
        record.NotAfter = certificate.NotAfter;
    }

    /// <summary>
    /// Seleciona a faixa mais urgente que se aplica ao numero de dias restantes,
    /// usando os limites configurados em <paramref name="thresholds"/>.
    /// </summary>
    private static ExpiryBucket? SelectBucket(int daysRemaining, ExpiryThresholds thresholds)
    {
        if (daysRemaining <= thresholds.Level1)  return ExpiryBucket.Days1;
        if (daysRemaining <= thresholds.Level7)  return ExpiryBucket.Days7;
        if (daysRemaining <= thresholds.Level15) return ExpiryBucket.Days15;
        if (daysRemaining <= thresholds.Level30) return ExpiryBucket.Days30;
        return null;
    }

    private static bool AlreadyNotified(CertificateNotificationState current, ExpiryBucket candidate)
    {
        return current switch
        {
            CertificateNotificationState.NotifiedUrgent  => true,
            CertificateNotificationState.NotifiedShort  => candidate is ExpiryBucket.Days7 or ExpiryBucket.Days15 or ExpiryBucket.Days30,
            CertificateNotificationState.NotifiedMedium => candidate is ExpiryBucket.Days15 or ExpiryBucket.Days30,
            CertificateNotificationState.NotifiedLong => candidate is ExpiryBucket.Days30,
            CertificateNotificationState.Dismissed  => true,
            _                                       => false
        };
    }

    private static CertificateNotificationState ToState(ExpiryBucket bucket)
    {
        return bucket switch
        {
            ExpiryBucket.Days30 => CertificateNotificationState.NotifiedLong,
            ExpiryBucket.Days15 => CertificateNotificationState.NotifiedMedium,
            ExpiryBucket.Days7  => CertificateNotificationState.NotifiedShort,
            ExpiryBucket.Days1  => CertificateNotificationState.NotifiedUrgent,
            _                   => CertificateNotificationState.None
        };
    }
}
