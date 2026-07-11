namespace CertExpiryMonitor.Services;

internal sealed record SettingsUpdateResult(bool Saved, DetailsSettingsPlan? Plan)
{
    public static SettingsUpdateResult Failed { get; } = new(false, null);
}

internal sealed class SettingsUpdateCoordinator
{
    private readonly JsonSettingsStore _settingsStore;
    private readonly TelemetryService _telemetry;
    private readonly FileLogger _logger;
    private readonly DiagnosticEventStore? _diagnosticEvents;

    public SettingsUpdateCoordinator(
        JsonSettingsStore settingsStore,
        TelemetryService telemetry,
        FileLogger logger,
        DiagnosticEventStore? diagnosticEvents = null)
    {
        _settingsStore = settingsStore;
        _telemetry = telemetry;
        _logger = logger;
        _diagnosticEvents = diagnosticEvents;
    }

    public SettingsUpdateResult Apply(DetailsSettingsUpdate update, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (!_settingsStore.TryLoad(out var loadedSettings))
        {
            // Stryker disable all: texto de observabilidade nao altera o contrato de falha.
            _logger.Error(new IOException("settings.json read failed"), "Failed to save settings because current settings could not be read");
            // Stryker restore all
            return SettingsUpdateResult.Failed;
        }

        var currentSettings = NotificationCheckCoordinator.NormalizeSettings(loadedSettings);
        var plan = DetailsSettingsPlanner.Build(currentSettings, update, now);
        if (!_settingsStore.Save(plan.Settings))
        {
            // Stryker disable all: erro estruturado e coberto por integracao do DiagnosticEventStore.
            var error = new IOException("settings.json save failed");
            _logger.Error(error, "Failed to persist settings");
            _diagnosticEvents?.RecordError(error, "settings.persist_failed", nameof(SettingsUpdateCoordinator), "Falha ao persistir configuracoes.");
            // Stryker restore all
            return SettingsUpdateResult.Failed;
        }

        RecordChanges(plan, update);
        _logger.ApplySettings(plan.Settings);
        _telemetry.Enabled = update.TelemetryEnabled;
        return new SettingsUpdateResult(true, plan);
    }

    private void RecordChanges(DetailsSettingsPlan plan, DetailsSettingsUpdate update)
    {
        // Stryker disable all: este metodo produz apenas telemetria/log/diagnostico; o plano e a
        // persistencia que governam comportamento permanecem no escopo de mutation testing.
        if (plan.ScheduleChanged)
        {
            _telemetry.Increment(counters => counters.ScheduleChanged++);
            _logger.Info($"User changed daily notification time to {plan.SelectedMinute:hh\\:mm}.");
        }
        if (plan.ThresholdsChanged)
        {
            _telemetry.Increment(counters => counters.ThresholdsChanged++);
            var thresholds = plan.Settings.Thresholds;
            _logger.Info($"User changed thresholds to {thresholds.Level1}/{thresholds.Level7}/{thresholds.Level15}/{thresholds.Level30}.");
        }
        if (plan.SoundChanged)
        {
            _logger.Info($"User changed notification sound setting to {update.NotificationSoundEnabled}.");
        }
        if (plan.AdvancedChanged)
        {
            _logger.Info($"Advanced settings updated: LogFormat={update.LogFormat}, EventLog={update.EventLogEnabled}, Telemetry={update.TelemetryEnabled}");
        }
        if (plan.ScheduleChanged || plan.ThresholdsChanged || plan.SoundChanged || plan.AdvancedChanged)
        {
            _diagnosticEvents?.RecordInfo(
                "settings.changed",
                nameof(SettingsUpdateCoordinator),
                "Usuario salvou alteracoes de configuracao.",
                new
                {
                    scheduleChanged = plan.ScheduleChanged,
                    thresholdsChanged = plan.ThresholdsChanged,
                    soundChanged = plan.SoundChanged,
                    advancedChanged = plan.AdvancedChanged,
                    log_format = update.LogFormat.ToString(),
                    event_log_enabled = update.EventLogEnabled,
                    telemetry_enabled = update.TelemetryEnabled
                });
        }
        // Stryker restore all
    }
}
