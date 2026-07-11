using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class SettingsUpdateCoordinatorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"SettingsUpdater_{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly JsonSettingsStore _store;
    private readonly TelemetryService _telemetry;
    private readonly SettingsUpdateCoordinator _coordinator;

    public SettingsUpdateCoordinatorTests()
    {
        _paths = new AppPaths(_tempDir);
        var logger = new FileLogger(_paths);
        _store = new JsonSettingsStore(_paths, logger);
        _telemetry = new TelemetryService(_paths, logger) { Enabled = true };
        _coordinator = new SettingsUpdateCoordinator(_store, _telemetry, logger);
    }

    [Fact]
    public void ApplyPersistsPlanAndRecordsChangedCounters()
    {
        Assert.True(_store.Save(new AppSettings
        {
            DailyCheckTime = TimeSpan.FromHours(9),
            Thresholds = new ExpiryThresholds(),
            TelemetryEnabled = true
        }));
        var update = new DetailsSettingsUpdate(
            TimeSpan.FromHours(14),
            false,
            new ExpiryThresholds { Level1 = 2, Level7 = 8, Level15 = 16, Level30 = 40 },
            LogFormat.Json,
            true,
            true);

        var result = _coordinator.Apply(update, new DateTime(2026, 7, 10, 10, 0, 0));

        Assert.True(result.Saved);
        Assert.NotNull(result.Plan);
        Assert.True(result.Plan.ScheduleChanged);
        Assert.True(result.Plan.ThresholdsChanged);
        Assert.True(result.Plan.SoundChanged);
        Assert.True(result.Plan.AdvancedChanged);
        Assert.True(result.Plan.ShouldRunAgainToday);
        Assert.True(_store.TryLoad(out var persisted));
        Assert.Equal(TimeSpan.FromHours(14), persisted.DailyCheckTime);
        Assert.Equal(40, persisted.Thresholds.Level30);
        Assert.True(persisted.ForceNextNotificationReminder);
        Assert.Equal(1, _telemetry.Load().ScheduleChanged);
        Assert.Equal(1, _telemetry.Load().ThresholdsChanged);
    }

    [Fact]
    public void ApplyWithNoChangesPersistsWithoutForcingReminder()
    {
        var settings = new AppSettings
        {
            DailyCheckTime = TimeSpan.FromHours(9),
            NotificationSoundEnabled = true,
            Thresholds = new ExpiryThresholds(),
            LogFormat = LogFormat.Text
        };
        Assert.True(_store.Save(settings));
        var update = new DetailsSettingsUpdate(
            settings.DailyCheckTime,
            settings.NotificationSoundEnabled,
            settings.Thresholds,
            settings.LogFormat,
            settings.EventLogEnabled,
            settings.TelemetryEnabled);

        var result = _coordinator.Apply(update, new DateTime(2026, 7, 10, 10, 0, 0));

        Assert.True(result.Saved);
        Assert.NotNull(result.Plan);
        Assert.False(result.Plan.ScheduleChanged);
        Assert.False(result.Plan.ThresholdsChanged);
        Assert.False(result.Plan.SoundChanged);
        Assert.False(result.Plan.AdvancedChanged);
        Assert.False(result.Plan.ForceNextScheduledNotification);
    }

    [Fact]
    public void ReadFailureAbortsWithoutWritingDefaults()
    {
        Directory.CreateDirectory(_paths.RootDirectory);
        File.WriteAllText(_paths.SettingsPath, "{ settings quebrado");

        var result = _coordinator.Apply(DefaultUpdate(), DateTime.Now);

        Assert.False(result.Saved);
        Assert.Null(result.Plan);
        Assert.False(File.Exists(_paths.SettingsPath));
        Assert.Single(Directory.GetFiles(_tempDir, "settings.json.corrupt-*"));
    }

    [Fact]
    public void ApplyRejectsNullUpdate()
    {
        Assert.Throws<ArgumentNullException>(() => _coordinator.Apply(null!, DateTime.Now));
    }

    private static DetailsSettingsUpdate DefaultUpdate() => new(
        TimeSpan.FromHours(9),
        true,
        new ExpiryThresholds(),
        LogFormat.Text,
        false,
        false);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
