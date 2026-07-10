using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class DetailsSettingsPlannerTests
{
    [Fact]
    public void Build_WhenNotificationTimeIsUnchanged_DoesNotForceNextReminder()
    {
        var current = Settings(dailyCheckTime: new TimeSpan(9, 30, 15));
        var update = Update(notificationTime: new TimeSpan(9, 30, 0));

        var plan = DetailsSettingsPlanner.Build(current, update, new DateTime(2026, 6, 3, 8, 0, 0));

        Assert.False(plan.ScheduleChanged);
        Assert.False(plan.ShouldRunAgainToday);
        Assert.False(plan.ForceNextScheduledNotification);
        Assert.Equal(DateOnly.FromDateTime(new DateTime(2026, 6, 3)), plan.Settings.LastCheckDate);
        Assert.Equal("snapshot", plan.Settings.LastCertificateSnapshotHash);
        Assert.False(plan.Settings.ForceNextNotificationReminder);
    }

    [Fact]
    public void Build_WhenNotificationTimeMovesToFutureToday_ForcesReminderAndClearsDailySkip()
    {
        var current = Settings(dailyCheckTime: new TimeSpan(9, 0, 0));
        var update = Update(notificationTime: new TimeSpan(10, 0, 0));

        var plan = DetailsSettingsPlanner.Build(current, update, new DateTime(2026, 6, 3, 9, 30, 0));

        Assert.True(plan.ScheduleChanged);
        Assert.True(plan.ShouldRunAgainToday);
        Assert.True(plan.ForceNextScheduledNotification);
        Assert.Null(plan.Settings.LastCheckDate);
        Assert.Empty(plan.Settings.LastCertificateSnapshotHash);
        Assert.True(plan.Settings.ForceNextNotificationReminder);
    }

    [Fact]
    public void Build_WhenNotificationTimeMovesToPastToday_DoesNotForceReminder()
    {
        var current = Settings(dailyCheckTime: new TimeSpan(9, 0, 0));
        var update = Update(notificationTime: new TimeSpan(8, 0, 0));

        var plan = DetailsSettingsPlanner.Build(current, update, new DateTime(2026, 6, 3, 9, 30, 0));

        Assert.True(plan.ScheduleChanged);
        Assert.False(plan.ShouldRunAgainToday);
        Assert.False(plan.ForceNextScheduledNotification);
        Assert.Equal(DateOnly.FromDateTime(new DateTime(2026, 6, 3)), plan.Settings.LastCheckDate);
        Assert.Equal("snapshot", plan.Settings.LastCertificateSnapshotHash);
    }

    [Fact]
    public void Build_WhenThresholdsChange_ForcesReminderAndClearsDailySkip()
    {
        var current = Settings(thresholds: new ExpiryThresholds { Level1 = 1, Level7 = 7, Level15 = 15, Level30 = 30 });
        var update = Update(thresholds: new ExpiryThresholds { Level1 = 2, Level7 = 8, Level15 = 16, Level30 = 31 });

        var plan = DetailsSettingsPlanner.Build(current, update, new DateTime(2026, 6, 3, 20, 0, 0));

        Assert.True(plan.ThresholdsChanged);
        Assert.True(plan.ForceNextScheduledNotification);
        Assert.Null(plan.Settings.LastCheckDate);
        Assert.Empty(plan.Settings.LastCertificateSnapshotHash);
        Assert.True(plan.Settings.ForceNextNotificationReminder);
    }

    [Fact]
    public void Build_WhenUpdateThresholdsAreNull_UsesDefaults()
    {
        var current = Settings(thresholds: new ExpiryThresholds { Level1 = 2, Level7 = 8, Level15 = 16, Level30 = 31 });
        var update = new DetailsSettingsUpdate(
            new TimeSpan(9, 0, 0),
            true,
            null!,
            LogFormat.Text,
            false,
            false);

        var plan = DetailsSettingsPlanner.Build(current, update, new DateTime(2026, 6, 3, 20, 0, 0));

        Assert.True(plan.ThresholdsChanged);
        Assert.Equal(1, plan.Settings.Thresholds.Level1);
        Assert.Equal(7, plan.Settings.Thresholds.Level7);
        Assert.Equal(15, plan.Settings.Thresholds.Level15);
        Assert.Equal(30, plan.Settings.Thresholds.Level30);
    }

    [Fact]
    public void Build_WhenOnlySoundOrAdvancedSettingsChange_DoesNotClearDailySkip()
    {
        var current = Settings(notificationSoundEnabled: true, logFormat: LogFormat.Text, eventLogEnabled: false, telemetryEnabled: false);
        var update = Update(notificationSoundEnabled: false, logFormat: LogFormat.Json, eventLogEnabled: true, telemetryEnabled: true);

        var plan = DetailsSettingsPlanner.Build(current, update, new DateTime(2026, 6, 3, 20, 0, 0));

        Assert.True(plan.SoundChanged);
        Assert.True(plan.AdvancedChanged);
        Assert.False(plan.ForceNextScheduledNotification);
        Assert.Equal(DateOnly.FromDateTime(new DateTime(2026, 6, 3)), plan.Settings.LastCheckDate);
        Assert.Equal("snapshot", plan.Settings.LastCertificateSnapshotHash);
        Assert.False(plan.Settings.ForceNextNotificationReminder);
    }

    [Fact]
    public void Build_WhenForcedReminderAlreadyPending_PreservesForcedReminder()
    {
        var current = Settings(forceNextNotificationReminder: true);
        var update = Update();

        var plan = DetailsSettingsPlanner.Build(current, update, new DateTime(2026, 6, 3, 20, 0, 0));

        Assert.True(plan.ForceNextScheduledNotification);
        Assert.Null(plan.Settings.LastCheckDate);
        Assert.Empty(plan.Settings.LastCertificateSnapshotHash);
        Assert.True(plan.Settings.ForceNextNotificationReminder);
    }

    [Fact]
    public void Build_DoesNotMutateCurrentSettings()
    {
        var thresholds = new ExpiryThresholds { Level1 = 1, Level7 = 7, Level15 = 15, Level30 = 30 };
        var current = Settings(
            dailyCheckTime: new TimeSpan(9, 0, 15),
            thresholds: thresholds,
            notificationSoundEnabled: true,
            logFormat: LogFormat.Text,
            eventLogEnabled: false,
            telemetryEnabled: false);
        var update = Update(
            notificationTime: new TimeSpan(10, 0, 0),
            notificationSoundEnabled: false,
            thresholds: new ExpiryThresholds { Level1 = 2, Level7 = 8, Level15 = 16, Level30 = 31 },
            logFormat: LogFormat.Json,
            eventLogEnabled: true,
            telemetryEnabled: true);

        var plan = DetailsSettingsPlanner.Build(current, update, new DateTime(2026, 6, 3, 9, 30, 0));

        Assert.NotSame(current, plan.Settings);
        Assert.Equal(new TimeSpan(9, 0, 15), current.DailyCheckTime);
        Assert.Equal(DateOnly.FromDateTime(new DateTime(2026, 6, 3)), current.LastCheckDate);
        Assert.Equal("snapshot", current.LastCertificateSnapshotHash);
        Assert.False(current.ForceNextNotificationReminder);
        Assert.True(current.NotificationSoundEnabled);
        Assert.Equal(LogFormat.Text, current.LogFormat);
        Assert.False(current.EventLogEnabled);
        Assert.False(current.TelemetryEnabled);
        Assert.Same(thresholds, current.Thresholds);
        Assert.Equal(30, current.Thresholds.Level30);
    }

    private static AppSettings Settings(
        TimeSpan? dailyCheckTime = null,
        ExpiryThresholds? thresholds = null,
        bool notificationSoundEnabled = true,
        LogFormat logFormat = LogFormat.Text,
        bool eventLogEnabled = false,
        bool telemetryEnabled = false,
        bool forceNextNotificationReminder = false)
        => new()
        {
            DailyCheckTime = dailyCheckTime ?? new TimeSpan(9, 0, 0),
            LastCheckDate = DateOnly.FromDateTime(new DateTime(2026, 6, 3)),
            LastCertificateSnapshotHash = "snapshot",
            NotificationSoundEnabled = notificationSoundEnabled,
            Thresholds = thresholds ?? new ExpiryThresholds(),
            ForceNextNotificationReminder = forceNextNotificationReminder,
            LogFormat = logFormat,
            EventLogEnabled = eventLogEnabled,
            TelemetryEnabled = telemetryEnabled
        };

    private static DetailsSettingsUpdate Update(
        TimeSpan? notificationTime = null,
        bool notificationSoundEnabled = true,
        ExpiryThresholds? thresholds = null,
        LogFormat logFormat = LogFormat.Text,
        bool eventLogEnabled = false,
        bool telemetryEnabled = false)
        => new(
            notificationTime ?? new TimeSpan(9, 0, 0),
            notificationSoundEnabled,
            thresholds ?? new ExpiryThresholds(),
            logFormat,
            eventLogEnabled,
            telemetryEnabled);
}
