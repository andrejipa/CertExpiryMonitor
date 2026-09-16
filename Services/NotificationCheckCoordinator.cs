using CertExpiryMonitor.Models;

namespace CertExpiryMonitor.Services;

public sealed record CheckCycleRequest(
    bool IgnoreConfiguredTime,
    bool IgnoreLastCheckDate);

public enum CheckCycleStatus
{
    Skipped = 0,
    CompletedNoDue = 1,
    NotificationShown = 2,
    NotificationFailed = 3,
    ReadFailed = 4,
    StatePersistFailed = 5,
    SettingsPersistFailed = 6
}

public sealed record CheckCycleResult(
    CheckCycleStatus Status,
    AppSettings? Settings = null,
    NotificationPlan? Plan = null)
{
    internal string? ManualFeedback => Status switch
    {
        CheckCycleStatus.CompletedNoDue => "Nenhum novo aviso pendente. Consulte os certificados monitorados.",
        CheckCycleStatus.ReadFailed => "A verificação não foi concluída porque as configurações, o histórico ou os certificados não puderam ser lidos. Nenhum dado foi alterado.",
        _ => null
    };

    public bool Ran => Status is CheckCycleStatus.CompletedNoDue or
        CheckCycleStatus.NotificationShown or
        CheckCycleStatus.NotificationFailed or
        CheckCycleStatus.StatePersistFailed or
        CheckCycleStatus.SettingsPersistFailed;

    public bool ShouldRetry => Status is CheckCycleStatus.ReadFailed or
        CheckCycleStatus.NotificationFailed or
        CheckCycleStatus.StatePersistFailed or
        CheckCycleStatus.SettingsPersistFailed;
}

/// <summary>
/// Coordena o ciclo transacional do monitor sem depender de controles WinForms.
/// </summary>
public sealed class NotificationCheckCoordinator
{
    private readonly JsonSettingsStore _settingsStore;
    private readonly CertificateCheckService _checkService;
    private readonly FileLogger _logger;
    private readonly DiagnosticEventStore? _diagnosticEvents;

    public NotificationCheckCoordinator(
        JsonSettingsStore settingsStore,
        CertificateCheckService checkService,
        FileLogger logger,
        DiagnosticEventStore? diagnosticEvents = null)
    {
        _settingsStore = settingsStore;
        _checkService = checkService;
        _logger = logger;
        _diagnosticEvents = diagnosticEvents;
    }

    public CheckCycleResult Run(CheckCycleRequest request, Func<NotificationPlan, bool> showNotification)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(showNotification);

        if (!_settingsStore.TryLoad(out var settings))
        {
            return new CheckCycleResult(CheckCycleStatus.ReadFailed);
        }

        settings = NormalizeSettings(settings);
        var forceReminder = settings.ForceNextNotificationReminder;
        var check = _checkService.RunCheck(
            request.IgnoreConfiguredTime,
            request.IgnoreLastCheckDate,
            forceReminder,
            settings);

        if (check.Status == CertificateCheckStatus.Skipped)
        {
            return new CheckCycleResult(CheckCycleStatus.Skipped, settings);
        }

        if (check.Status == CertificateCheckStatus.ReadFailed)
        {
            return new CheckCycleResult(CheckCycleStatus.ReadFailed, settings);
        }

        if (check.Status is CertificateCheckStatus.StatePersistFailed or CertificateCheckStatus.Failed)
        {
            return new CheckCycleResult(CheckCycleStatus.StatePersistFailed, settings);
        }

        var completedDate = settings.LastCheckDate;
        var completedHash = settings.LastCertificateSnapshotHash;
        var status = CheckCycleStatus.CompletedNoDue;

        if (check.Plan is { } plan)
        {
            if (!showNotification(plan))
            {
                settings.LastCheckDate = null;
                settings.LastCertificateSnapshotHash = string.Empty;
                settings.ForceNextNotificationReminder = forceReminder;
                status = CheckCycleStatus.NotificationFailed;
            }
            else if (!_checkService.MarkNotified(plan, settings.Thresholds.Normalized()))
            {
                settings.LastCheckDate = null;
                settings.LastCertificateSnapshotHash = string.Empty;
                settings.ForceNextNotificationReminder = true;
                status = CheckCycleStatus.StatePersistFailed;
            }
            else
            {
                settings.LastCheckDate = completedDate;
                settings.LastCertificateSnapshotHash = completedHash;
                settings.ForceNextNotificationReminder = false;
                status = CheckCycleStatus.NotificationShown;
            }
        }
        else
        {
            settings.ForceNextNotificationReminder = false;
        }

        if (!_settingsStore.Save(settings))
        {
            _logger.Error(new IOException("settings.json save failed"), "Failed to persist check cycle settings");
            _diagnosticEvents?.RecordError(
                new IOException("settings.json save failed"),
                "check.settings_persist_failed",
                "NotificationCheckCoordinator",
                "Falha ao persistir o resultado do ciclo de verificacao.");
            return new CheckCycleResult(CheckCycleStatus.SettingsPersistFailed, settings, check.Plan);
        }

        return new CheckCycleResult(status, settings, check.Plan);
    }

    internal static AppSettings NormalizeSettings(AppSettings settings)
    {
        if (settings.DailyCheckTime < TimeSpan.Zero || settings.DailyCheckTime >= TimeSpan.FromDays(1))
        {
            settings.DailyCheckTime = TimeSpan.FromHours(9);
        }

        if (settings.InitialDelayMinutes <= 0)
        {
            settings.InitialDelayMinutes = 5;
        }

        settings.Thresholds = (settings.Thresholds ?? new ExpiryThresholds()).Normalized();
        return settings;
    }
}
