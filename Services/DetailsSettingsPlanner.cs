using CertExpiryMonitor.Models;

namespace CertExpiryMonitor.Services;

internal sealed record DetailsSettingsPlan(
    AppSettings Settings,
    TimeSpan SelectedMinute,
    bool ScheduleChanged,
    bool ThresholdsChanged,
    bool SoundChanged,
    bool AdvancedChanged,
    bool ShouldRunAgainToday,
    bool ForceNextScheduledNotification);

internal static class DetailsSettingsPlanner
{
    public static DetailsSettingsPlan Build(AppSettings currentSettings, DetailsSettingsUpdate update, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(currentSettings);
        ArgumentNullException.ThrowIfNull(update);

        var currentMinute = new TimeSpan(now.Hour, now.Minute, 0);
        var selectedMinute = new TimeSpan(update.NotificationTime.Hours, update.NotificationTime.Minutes, 0);
        var previousMinute = new TimeSpan(currentSettings.DailyCheckTime.Hours, currentSettings.DailyCheckTime.Minutes, 0);
        var previousThresholds = (currentSettings.Thresholds ?? new ExpiryThresholds()).Normalized();
        var newThresholds = (update.Thresholds ?? new ExpiryThresholds()).Normalized();

        var scheduleChanged = selectedMinute != previousMinute;
        var thresholdsChanged = !ThresholdsEqual(previousThresholds, newThresholds);
        var soundChanged = currentSettings.NotificationSoundEnabled != update.NotificationSoundEnabled;
        var advancedChanged = currentSettings.LogFormat != update.LogFormat ||
                              currentSettings.EventLogEnabled != update.EventLogEnabled ||
                              currentSettings.TelemetryEnabled != update.TelemetryEnabled;
        var shouldRunAgainToday = scheduleChanged && selectedMinute >= currentMinute;
        var forceNextScheduledNotification = currentSettings.ForceNextNotificationReminder ||
                                             thresholdsChanged ||
                                             shouldRunAgainToday;

        var newSettings = Clone(currentSettings);
        newSettings.DailyCheckTime = selectedMinute;
        newSettings.Thresholds = newThresholds;
        newSettings.NotificationSoundEnabled = update.NotificationSoundEnabled;
        newSettings.LogFormat = update.LogFormat;
        newSettings.EventLogEnabled = update.EventLogEnabled;
        newSettings.TelemetryEnabled = update.TelemetryEnabled;

        if (forceNextScheduledNotification)
        {
            newSettings.LastCheckDate = null;
            newSettings.LastCertificateSnapshotHash = string.Empty;
            newSettings.ForceNextNotificationReminder = true;
        }

        return new DetailsSettingsPlan(
            newSettings,
            selectedMinute,
            scheduleChanged,
            thresholdsChanged,
            soundChanged,
            advancedChanged,
            shouldRunAgainToday,
            forceNextScheduledNotification);
    }

    internal static bool ThresholdsEqual(ExpiryThresholds left, ExpiryThresholds right)
    {
        var normalizedLeft = left.Normalized();
        var normalizedRight = right.Normalized();
        return normalizedLeft.Level1 == normalizedRight.Level1 &&
               normalizedLeft.Level7 == normalizedRight.Level7 &&
               normalizedLeft.Level15 == normalizedRight.Level15 &&
               normalizedLeft.Level30 == normalizedRight.Level30;
    }

    private static AppSettings Clone(AppSettings settings) => new()
    {
        DailyCheckTime = settings.DailyCheckTime,
        LastCheckDate = settings.LastCheckDate,
        InitialDelayMinutes = settings.InitialDelayMinutes,
        StartupEnabled = settings.StartupEnabled,
        NotificationSoundEnabled = settings.NotificationSoundEnabled,
        LastCertificateSnapshotHash = settings.LastCertificateSnapshotHash,
        Thresholds = settings.Thresholds,
        ForceNextNotificationReminder = settings.ForceNextNotificationReminder,
        LogFormat = settings.LogFormat,
        EventLogEnabled = settings.EventLogEnabled,
        TelemetryEnabled = settings.TelemetryEnabled
    };
}
