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
        FileLogger logger)
    {
        _settingsStore    = settingsStore;
        _stateStore       = stateStore;
        _certificateReader = certificateReader;
        _expiryEvaluator  = expiryEvaluator;
        _logger           = logger;
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
    public (bool Ran, NotificationPlan? Plan) RunCheck(
        bool ignoreConfiguredTime,
        bool ignoreLastCheckDate,
        bool forceReminder,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // CompareExchange retorna o valor ORIGINAL; se for 1, outra thread esta executando.
        if (Interlocked.CompareExchange(ref _isChecking, 1, 0) == 1) return (false, null);
        try
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var now   = DateTime.Now;

            if (!ignoreConfiguredTime && now.TimeOfDay < settings.DailyCheckTime)
            {
                _logger.Info("Certificate check skipped. Configured time not reached.");
                return (false, null);
            }

            var state        = _stateStore.Load();
            var certificates = _certificateReader.ReadCurrentUserPersonalCertificates();
            var snapshotHash = ComputeSnapshotHash(certificates);

            if (!ignoreLastCheckDate &&
                settings.LastCheckDate == today &&
                string.Equals(settings.LastCertificateSnapshotHash, snapshotHash, StringComparison.Ordinal))
            {
                _logger.Info("Certificate check skipped. Already checked today with same certificates.");
                return (false, null);
            }

            var thresholds = settings.Thresholds.Normalized();
            var plan = forceReminder
                ? _expiryEvaluator.BuildReminderPlan(certificates, state, today, thresholds)
                : _expiryEvaluator.BuildPlan(certificates, state, today, thresholds);

            LastPlan = plan;

            // Persiste novos registros criados por GetOrCreateRecord durante BuildPlan.
            _stateStore.Save(state);

            // Atualiza data e hash no objeto settings; o chamador persiste as settings.
            settings.LastCheckDate                = today;
            settings.LastCertificateSnapshotHash  = snapshotHash;

            _logger.Info($"Certificate check completed. Certificates={certificates.Count}, Due={plan.DueCertificates.Count}");
            return (true, plan.HasItems ? plan : null);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Certificate check failed");
            return (false, null);
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
    public void MarkNotified(NotificationPlan plan, ExpiryThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(thresholds);

        try
        {
            var state = _stateStore.Load();
            _expiryEvaluator.MarkNotified(plan, state, DateTimeOffset.Now);
            _stateStore.Save(state);

            _logger.Notification(
                $"Notification dispatched. " +
                $"Due={plan.DueCertificates.Count}, " +
                $"Bucket{thresholds.Level30}={plan.Count(ExpiryBucket.Days30)}, " +
                $"Bucket{thresholds.Level15}={plan.Count(ExpiryBucket.Days15)}, " +
                $"Bucket{thresholds.Level7}={plan.Count(ExpiryBucket.Days7)}, " +
                $"Bucket{thresholds.Level1}={plan.Count(ExpiryBucket.Days1)}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to mark certificates as notified");
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
                .OrderBy(c => c.Thumbprint, StringComparer.OrdinalIgnoreCase)
                .Select(c => $"{JsonStateStore.NormalizeThumbprint(c.Thumbprint)}:{c.NotAfter:O}"));

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes);
    }
}
