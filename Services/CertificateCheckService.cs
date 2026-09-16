using System.Security.Cryptography;
using System.Text;
using CertExpiryMonitor.Models;

namespace CertExpiryMonitor.Services;

/// <summary>
/// Encapsula a logica de verificacao de certificados, separada das preocupacoes de UI
/// do <see cref="TrayApplicationContext"/>.
/// </summary>
public sealed class CertificateCheckService
{
    private readonly JsonSettingsStore _settingsStore;
    private readonly JsonStateStore _stateStore;
    private readonly CertificateReader _certificateReader;
    private readonly ExpiryEvaluator _expiryEvaluator;
    private readonly FileLogger _logger;
    private readonly DiagnosticEventStore? _diagnosticEvents;
    private readonly Func<DateTime> _now;
    // Guard atomico entre timer thread (verificacao diaria) e UI thread (botoes de menu).
    // 0 = livre, 1 = em execucao.
    private int _isChecking;

    /// <summary>Ultimo plano produzido, seja em verificacao automatica ou manual.</summary>
    public NotificationPlan? LastPlan { get; private set; }

    public CertificateCheckService(
        JsonSettingsStore settingsStore,
        JsonStateStore stateStore,
        CertificateReader certificateReader,
        ExpiryEvaluator expiryEvaluator,
        FileLogger logger,
        DiagnosticEventStore? diagnosticEvents = null)
        : this(
            settingsStore,
            stateStore,
            certificateReader,
            expiryEvaluator,
            logger,
            diagnosticEvents,
            () => DateTime.Now)
    {
    }

    internal CertificateCheckService(
        JsonSettingsStore settingsStore,
        JsonStateStore stateStore,
        CertificateReader certificateReader,
        ExpiryEvaluator expiryEvaluator,
        FileLogger logger,
        DiagnosticEventStore? diagnosticEvents,
        Func<DateTime> now)
    {
        _settingsStore    = settingsStore;
        _stateStore       = stateStore;
        _certificateReader = certificateReader;
        _expiryEvaluator  = expiryEvaluator;
        _logger           = logger;
        _diagnosticEvents = diagnosticEvents;
        _now              = now ?? throw new ArgumentNullException(nameof(now));
    }

    /// <summary>
    /// Executa a verificacao de certificados.
    /// </summary>
    /// <param name="ignoreConfiguredTime">
    ///   Se verdadeiro, ignora o horario configurado e executa imediatamente.
    /// </param>
    /// <param name="ignoreLastCheckDate">
    ///   Se verdadeiro, executa mesmo que a verificacao ja tenha ocorrido hoje.
    /// </param>
    /// <param name="forceReminder">
    ///   Se verdadeiro, usa BuildReminderPlan (inclui certificados ja notificados).
    /// </param>
    /// <param name="settings">Settings carregados e normalizados pelo chamador.</param>
    /// <returns>
    ///   (Ran=true, Plan) se a verificacao executou; (Ran=false, null) se foi pulada.
    ///   Plan sera nulo mesmo quando Ran=true se nao houver certificados pendentes.
    /// </returns>
    public CertificateCheckResult RunCheck(
        bool ignoreConfiguredTime,
        bool ignoreLastCheckDate,
        bool forceReminder,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // CompareExchange retorna o valor ORIGINAL; se for 1, outra thread esta executando.
        if (Interlocked.CompareExchange(ref _isChecking, 1, 0) == 1)
        {
            _diagnosticEvents?.RecordInfo(
                "check.skipped",
                "CertificateCheckService",
                "Verificacao ignorada porque outra verificacao ja estava em execucao.",
                new { reason = "concurrent" });
            return new CertificateCheckResult(CertificateCheckStatus.Skipped);
        }
        try
        {
            LastPlan = null;
            _diagnosticEvents?.RecordInfo(
                "check.started",
                "CertificateCheckService",
                "Verificacao de certificados iniciada.",
                new { ignoreConfiguredTime, ignoreLastCheckDate, forceReminder });

            var now = _now();
            var today = DateOnly.FromDateTime(now);

            if (!ignoreConfiguredTime && now.TimeOfDay < settings.DailyCheckTime)
            {
                _logger.Info("Certificate check skipped. Configured time not reached.");
                _diagnosticEvents?.RecordInfo(
                    "check.skipped",
                    "CertificateCheckService",
                    "Verificacao ignorada porque o horario configurado ainda nao chegou.",
                    new { reason = "configured_time_not_reached", configured_time = settings.DailyCheckTime });
                return new CertificateCheckResult(CertificateCheckStatus.Skipped);
            }

            if (!_stateStore.TryLoad(out var state))
            {
                _diagnosticEvents?.RecordWarning(
                    "check.read_failed",
                    "CertificateCheckService",
                    "Verificacao abortada porque o estado persistido nao pode ser lido.",
                    new { source = "certificate_state" });
                return new CertificateCheckResult(CertificateCheckStatus.ReadFailed);
            }

            var certificateRead = _certificateReader.ReadCurrentUserPersonalCertificates();
            if (!certificateRead.IsComplete)
            {
                _diagnosticEvents?.RecordWarning(
                    "check.read_failed",
                    "CertificateCheckService",
                    "Verificacao abortada porque a leitura de certificados foi incompleta.",
                    new
                    {
                        source = "x509_store",
                        status = certificateRead.Status.ToString(),
                        failed_count = certificateRead.FailedCertificates
                    });
                return new CertificateCheckResult(CertificateCheckStatus.ReadFailed);
            }

            var certificates = certificateRead.Certificates;
            var snapshotHash = ComputeSnapshotHash(certificates);
            var thresholds = (settings.Thresholds ?? new ExpiryThresholds()).Normalized();
            _diagnosticEvents?.RecordCertificateObservation(certificates, thresholds);

            if (!ignoreLastCheckDate &&
                settings.LastCheckDate == today &&
                string.Equals(settings.LastCertificateSnapshotHash, snapshotHash, StringComparison.Ordinal))
            {
                _logger.Info("Certificate check skipped. Already checked today with same certificates.");
                _diagnosticEvents?.RecordInfo(
                    "check.skipped",
                    "CertificateCheckService",
                    "Verificacao ignorada porque ja foi executada hoje com os mesmos certificados.",
                    new { reason = "same_day_same_snapshot", certificate_count = certificates.Count });
                return new CertificateCheckResult(CertificateCheckStatus.Skipped);
            }

            var plan = forceReminder
                ? _expiryEvaluator.BuildReminderPlan(certificates, state, today, thresholds)
                : _expiryEvaluator.BuildPlan(certificates, state, today, thresholds);

            // Persiste novos registros criados por GetOrCreateRecord durante BuildPlan.
            if (!_stateStore.Save(state))
            {
                _logger.Error(new IOException("certificate-state.json save failed"), "Certificate check aborted because state was not persisted");
                _diagnosticEvents?.RecordError(
                    new IOException("certificate-state.json save failed"),
                    "check.failed",
                    "CertificateCheckService",
                    "Verificacao abortada porque o estado nao foi persistido.");
                return new CertificateCheckResult(CertificateCheckStatus.StatePersistFailed);
            }

            LastPlan = plan.HasItems ? plan : null;

            // Atualiza data e hash no objeto settings; o chamador persiste as settings.
            settings.LastCheckDate                = today;
            settings.LastCertificateSnapshotHash  = snapshotHash;

            _logger.Info($"Certificate check completed. Certificates={certificates.Count}, Due={plan.DueCertificates.Count}");
            _diagnosticEvents?.RecordInfo(
                "check.completed",
                "CertificateCheckService",
                "Verificacao de certificados concluida.",
                new
                {
                    certificate_count = certificates.Count,
                    due_count = plan.DueCertificates.Count,
                    forceReminder
                });
            return new CertificateCheckResult(CertificateCheckStatus.Completed, plan.HasItems ? plan : null);
        }
        catch (Exception ex)
        {
            LastPlan = null;
            _logger.Error(ex, "Certificate check failed");
            _diagnosticEvents?.RecordError(ex, "check.failed", "CertificateCheckService", "Verificacao de certificados falhou.");
            return new CertificateCheckResult(CertificateCheckStatus.Failed);
        }
        finally
        {
            Interlocked.Exchange(ref _isChecking, 0);
        }
    }

    /// <summary>
    /// Marca os certificados do plano como notificados e persiste o estado.
    /// Deve ser chamado apenas se a notificacao foi efetivamente exibida.
    /// </summary>
    public bool MarkNotified(NotificationPlan plan, ExpiryThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(thresholds);

        try
        {
            if (!_stateStore.TryLoad(out var state))
            {
                _logger.Error(new IOException("certificate-state.json read failed"), "Failed to load notified certificates");
                return false;
            }
            _expiryEvaluator.MarkNotified(plan, state, DateTimeOffset.Now);
            if (!_stateStore.Save(state))
            {
                _logger.Error(new IOException("certificate-state.json save failed"), "Failed to persist notified certificates");
                _diagnosticEvents?.RecordError(
                    new IOException("certificate-state.json save failed"),
                    "notification.mark_failed",
                    "CertificateCheckService",
                    "Falha ao persistir certificados notificados.");
                return false;
            }

            _logger.Notification(
                $"Notification dispatched. " +
                $"Due={plan.DueCertificates.Count}, " +
                $"Bucket{thresholds.Level30}={plan.Count(ExpiryBucket.Days30)}, " +
                $"Bucket{thresholds.Level15}={plan.Count(ExpiryBucket.Days15)}, " +
                $"Bucket{thresholds.Level7}={plan.Count(ExpiryBucket.Days7)}, " +
                $"Bucket{thresholds.Level1}={plan.Count(ExpiryBucket.Days1)}");
            _diagnosticEvents?.RecordInfo(
                "notification.marked",
                "CertificateCheckService",
                "Certificados marcados como notificados.",
                new
                {
                    due_count = plan.DueCertificates.Count,
                    bucket_long = plan.Count(ExpiryBucket.Days30),
                    bucket_medium = plan.Count(ExpiryBucket.Days15),
                    bucket_short = plan.Count(ExpiryBucket.Days7),
                    bucket_urgent = plan.Count(ExpiryBucket.Days1)
                });
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to mark certificates as notified");
            _diagnosticEvents?.RecordError(ex, "notification.mark_failed", "CertificateCheckService", "Falha ao marcar certificados como notificados.");
            return false;
        }
    }

    /// <summary>
    /// Computa um hash SHA-256 do conjunto de certificados para detectar mudancas
    /// sem re-executar a verificacao completa no mesmo dia.
    /// </summary>
    public static string ComputeSnapshotHash(IReadOnlyList<CertificateSnapshot> certificates)
    {
        ArgumentNullException.ThrowIfNull(certificates);

        var content = string.Join(
            "|",
            certificates
                .Select(c => new
                {
                    Thumbprint = CertificateIdentity.NormalizeThumbprint(c.Thumbprint),
                    c.NotAfter
                })
                .Where(c => c.Thumbprint.Length > 0)
                .GroupBy(c => c.Thumbprint, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(c => c.NotAfter)
                    .First())
                .OrderBy(c => c.Thumbprint, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.NotAfter)
                .Select(c => $"{c.Thumbprint}:{c.NotAfter:O}"));

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }
}
