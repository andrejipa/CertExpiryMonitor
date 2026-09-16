using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class NotificationCheckCoordinatorTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"CheckCoordinator-{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly FileLogger _logger;

    public NotificationCheckCoordinatorTests()
    {
        _paths = new AppPaths(_tempDir);
        _logger = new FileLogger(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void NoDueCertificatesCompletesAndPersistsFingerprint()
    {
        var coordinator = CreateCoordinator([], out var settingsStore, out _);

        var result = coordinator.Run(new CheckCycleRequest(true, true), _ => throw new InvalidOperationException());

        Assert.Equal(CheckCycleStatus.CompletedNoDue, result.Status);
        Assert.True(settingsStore.TryLoad(out var saved));
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today), saved.LastCheckDate);
        Assert.False(string.IsNullOrWhiteSpace(saved.LastCertificateSnapshotHash));
    }

    [Fact]
    public void NoDueCertificatesClearsPersistedForcedReminder()
    {
        var coordinator = CreateCoordinator([], out var settingsStore, out _, forceReminder: true);

        var result = coordinator.Run(new CheckCycleRequest(true, true), _ => throw new InvalidOperationException());

        Assert.Equal(CheckCycleStatus.CompletedNoDue, result.Status);
        Assert.True(settingsStore.TryLoad(out var saved));
        Assert.False(saved.ForceNextNotificationReminder);
    }

    [Fact]
    public void RunRejectsNullRequestAndNotificationCallback()
    {
        var coordinator = CreateCoordinator([], out _, out _);

        Assert.Throws<ArgumentNullException>(() => coordinator.Run(null!, _ => true));
        Assert.Throws<ArgumentNullException>(() => coordinator.Run(new CheckCycleRequest(true, true), null!));
    }

    [Fact]
    public void ConfiguredTimeSkipDoesNotRewriteSettings()
    {
        var settingsStore = new JsonSettingsStore(_paths, _logger);
        Assert.True(settingsStore.Save(new AppSettings { DailyCheckTime = TimeSpan.FromHours(10) }));
        var before = File.ReadAllText(_paths.SettingsPath);
        var stateStore = new JsonStateStore(_paths, _logger);
        var check = new CertificateCheckService(
            settingsStore,
            stateStore,
            new FixedCertificateReader(_logger, []),
            new ExpiryEvaluator(),
            _logger,
            diagnosticEvents: null,
            now: () => new DateTime(2026, 7, 10, 9, 0, 0));
        var coordinator = new NotificationCheckCoordinator(settingsStore, check, _logger);

        var result = coordinator.Run(new CheckCycleRequest(false, false), _ => true);

        Assert.Equal(CheckCycleStatus.Skipped, result.Status);
        Assert.Equal(before, File.ReadAllText(_paths.SettingsPath));
    }

    [Fact]
    public void ConfiguredTimeReachedRunsCheckDeterministically()
    {
        var settingsStore = new JsonSettingsStore(_paths, _logger);
        Assert.True(settingsStore.Save(new AppSettings { DailyCheckTime = TimeSpan.FromHours(9) }));
        var stateStore = new JsonStateStore(_paths, _logger);
        var check = new CertificateCheckService(
            settingsStore,
            stateStore,
            new FixedCertificateReader(_logger, []),
            new ExpiryEvaluator(),
            _logger,
            diagnosticEvents: null,
            now: () => new DateTime(2026, 7, 10, 10, 0, 0));
        var coordinator = new NotificationCheckCoordinator(settingsStore, check, _logger);

        var result = coordinator.Run(new CheckCycleRequest(false, false), _ => true);

        Assert.Equal(CheckCycleStatus.CompletedNoDue, result.Status);
    }

    [Fact]
    public void ConfiguredTimeExactBoundaryRunsCheckDeterministically()
    {
        var settingsStore = new JsonSettingsStore(_paths, _logger);
        Assert.True(settingsStore.Save(new AppSettings { DailyCheckTime = TimeSpan.FromHours(9) }));
        var stateStore = new JsonStateStore(_paths, _logger);
        var check = new CertificateCheckService(
            settingsStore,
            stateStore,
            new FixedCertificateReader(_logger, []),
            new ExpiryEvaluator(),
            _logger,
            diagnosticEvents: null,
            now: () => new DateTime(2026, 7, 10, 9, 0, 0));
        var coordinator = new NotificationCheckCoordinator(settingsStore, check, _logger);

        var result = coordinator.Run(new CheckCycleRequest(false, false), _ => true);

        Assert.Equal(CheckCycleStatus.CompletedNoDue, result.Status);
    }

    [Fact]
    public void AcceptedNotificationMarksStateAndClearsForcedReminder()
    {
        var coordinator = CreateCoordinator([DueCertificate()], out var settingsStore, out var stateStore, forceReminder: true);

        var result = coordinator.Run(new CheckCycleRequest(true, true), _ => true);

        Assert.Equal(CheckCycleStatus.NotificationShown, result.Status);
        Assert.True(stateStore.TryLoad(out var state));
        Assert.Equal(CertificateNotificationState.NotifiedShort, state.Values.Single().State);
        Assert.True(settingsStore.TryLoad(out var settings));
        Assert.False(settings.ForceNextNotificationReminder);
        Assert.NotNull(settings.LastCheckDate);
    }

    [Fact]
    public void RejectedNotificationClearsFingerprintAndKeepsReminderPending()
    {
        var coordinator = CreateCoordinator([DueCertificate()], out var settingsStore, out _, forceReminder: true);

        var result = coordinator.Run(new CheckCycleRequest(true, true), _ => false);

        Assert.Equal(CheckCycleStatus.NotificationFailed, result.Status);
        Assert.True(result.ShouldRetry);
        Assert.True(settingsStore.TryLoad(out var settings));
        Assert.Null(settings.LastCheckDate);
        Assert.Equal(string.Empty, settings.LastCertificateSnapshotHash);
        Assert.True(settings.ForceNextNotificationReminder);
    }

    [Fact]
    public void MarkNotifiedSaveFailureReturnsStatePersistFailedAndKeepsRetryState()
    {
        var writes = 0;
        var stateHooks = new JsonStoreReadHooks
        {
            WriteAtomically = (path, content) =>
            {
                writes++;
                if (writes == 2) throw new IOException("second state write failed");
                File.WriteAllText(path, content);
            }
        };
        var coordinator = CreateCoordinator(
            [DueCertificate()],
            out var settingsStore,
            out _,
            forceReminder: false,
            stateHooks: stateHooks);

        var result = coordinator.Run(new CheckCycleRequest(true, true), _ => true);

        Assert.Equal(CheckCycleStatus.StatePersistFailed, result.Status);
        Assert.True(settingsStore.TryLoad(out var settings));
        Assert.Null(settings.LastCheckDate);
        Assert.Equal(string.Empty, settings.LastCertificateSnapshotHash);
        Assert.True(settings.ForceNextNotificationReminder);
    }

    [Theory]
    [InlineData(CheckCycleStatus.Skipped, false)]
    [InlineData(CheckCycleStatus.CompletedNoDue, false)]
    [InlineData(CheckCycleStatus.NotificationShown, false)]
    [InlineData(CheckCycleStatus.NotificationFailed, true)]
    [InlineData(CheckCycleStatus.ReadFailed, true)]
    [InlineData(CheckCycleStatus.StatePersistFailed, true)]
    [InlineData(CheckCycleStatus.SettingsPersistFailed, true)]
    public void CheckCycleResultExposesRetryOnlyForRecoverableFailures(CheckCycleStatus status, bool expected)
    {
        Assert.Equal(expected, new CheckCycleResult(status).ShouldRetry);
    }

    [Theory]
    [InlineData(CheckCycleStatus.Skipped, false)]
    [InlineData(CheckCycleStatus.ReadFailed, false)]
    [InlineData(CheckCycleStatus.CompletedNoDue, true)]
    [InlineData(CheckCycleStatus.NotificationShown, true)]
    [InlineData(CheckCycleStatus.NotificationFailed, true)]
    [InlineData(CheckCycleStatus.StatePersistFailed, true)]
    [InlineData(CheckCycleStatus.SettingsPersistFailed, true)]
    public void ResultDistinguishesExecutedCycleFromSkippedOrUnreadableData(CheckCycleStatus status, bool expected)
    {
        Assert.Equal(expected, new CheckCycleResult(status).Ran);
    }

    [Fact]
    public void FailedReadFeedbackDoesNotClaimSuccessfulVerification()
    {
        var result = new CheckCycleResult(CheckCycleStatus.ReadFailed);
        Assert.Contains("não foi concluída", result.ManualFeedback);
        Assert.DoesNotContain("Nenhum novo aviso", result.ManualFeedback);
    }

    [Fact]
    public void SettingsSaveFailureIsReportedAfterSuccessfulCheck()
    {
        var normalSettings = new JsonSettingsStore(_paths, _logger);
        Assert.True(normalSettings.Save(new AppSettings()));
        var failingSettings = new JsonSettingsStore(_paths, _logger, new JsonStoreReadHooks
        {
            WriteAtomically = (_, _) => throw new IOException("settings write failed")
        });
        var stateStore = new JsonStateStore(_paths, _logger);
        var check = new CertificateCheckService(
            failingSettings,
            stateStore,
            new FixedCertificateReader(_logger, []),
            new ExpiryEvaluator(),
            _logger);
        var diagnostics = new DiagnosticEventStore(_paths, _logger);
        var coordinator = new NotificationCheckCoordinator(failingSettings, check, _logger, diagnostics);

        var result = coordinator.Run(new CheckCycleRequest(true, true), _ => true);

        Assert.Equal(CheckCycleStatus.SettingsPersistFailed, result.Status);
        Assert.True(result.ShouldRetry);
    }

    [Fact]
    public void InitialStateSaveFailureStopsBeforeNotification()
    {
        var settingsStore = new JsonSettingsStore(_paths, _logger);
        Assert.True(settingsStore.Save(new AppSettings()));
        var stateStore = new JsonStateStore(_paths, _logger, new JsonStoreReadHooks
        {
            WriteAtomically = (_, _) => throw new IOException("state write failed")
        });
        var check = new CertificateCheckService(
            settingsStore,
            stateStore,
            new FixedCertificateReader(_logger, [DueCertificate()]),
            new ExpiryEvaluator(),
            _logger);
        var coordinator = new NotificationCheckCoordinator(settingsStore, check, _logger);
        var notifications = 0;

        var result = coordinator.Run(new CheckCycleRequest(true, true), _ => { notifications++; return true; });

        Assert.Equal(CheckCycleStatus.StatePersistFailed, result.Status);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public void NormalizeSettingsRepairsInvalidValuesAndKeepsValidValues()
    {
        var invalid = new AppSettings
        {
            DailyCheckTime = TimeSpan.FromHours(48),
            InitialDelayMinutes = 0,
            Thresholds = null!
        };
        var valid = new AppSettings
        {
            DailyCheckTime = TimeSpan.FromHours(8),
            InitialDelayMinutes = 10,
            Thresholds = new ExpiryThresholds()
        };

        var normalizedInvalid = NotificationCheckCoordinator.NormalizeSettings(invalid);
        var normalizedValid = NotificationCheckCoordinator.NormalizeSettings(valid);

        Assert.Equal(TimeSpan.FromHours(9), normalizedInvalid.DailyCheckTime);
        Assert.Equal(5, normalizedInvalid.InitialDelayMinutes);
        Assert.NotNull(normalizedInvalid.Thresholds);
        Assert.Equal(TimeSpan.FromHours(8), normalizedValid.DailyCheckTime);
        Assert.Equal(10, normalizedValid.InitialDelayMinutes);
    }

    [Theory]
    [InlineData(-1, 9)]
    [InlineData(0, 0)]
    [InlineData(23, 23)]
    [InlineData(24, 9)]
    public void NormalizeSettingsHonorsDailyTimeBoundaries(int inputHours, int expectedHours)
    {
        var settings = new AppSettings { DailyCheckTime = TimeSpan.FromHours(inputHours) };

        var normalized = NotificationCheckCoordinator.NormalizeSettings(settings);

        Assert.Equal(TimeSpan.FromHours(expectedHours), normalized.DailyCheckTime);
    }

    [Fact]
    public void NormalizeSettingsPreservesAndNormalizesCustomThresholds()
    {
        var settings = new AppSettings
        {
            Thresholds = new ExpiryThresholds { Level30 = 40, Level15 = 20, Level7 = 10, Level1 = 2 }
        };

        var normalized = NotificationCheckCoordinator.NormalizeSettings(settings);

        Assert.Equal(40, normalized.Thresholds.Level30);
        Assert.Equal(20, normalized.Thresholds.Level15);
        Assert.Equal(10, normalized.Thresholds.Level7);
        Assert.Equal(2, normalized.Thresholds.Level1);
    }

    [Fact]
    public void SettingsReadFailureDoesNotAttemptNotificationOrWrite()
    {
        File.WriteAllText(_paths.SettingsPath, "{}");
        var writes = 0;
        var settingsStore = new JsonSettingsStore(_paths, _logger, new JsonStoreReadHooks
        {
            ReadAllText = _ => throw new IOException("read failed"),
            WriteAtomically = (_, _) => writes++
        });
        var stateStore = new JsonStateStore(_paths, _logger);
        var check = new CertificateCheckService(
            settingsStore,
            stateStore,
            new FixedCertificateReader(_logger, [DueCertificate()]),
            new ExpiryEvaluator(),
            _logger);
        var coordinator = new NotificationCheckCoordinator(settingsStore, check, _logger);
        var notifications = 0;

        var result = coordinator.Run(new CheckCycleRequest(true, true), _ => { notifications++; return true; });

        Assert.Equal(CheckCycleStatus.ReadFailed, result.Status);
        Assert.True(result.ShouldRetry);
        Assert.Equal(0, notifications);
        Assert.Equal(0, writes);
    }

    [Fact]
    public void StateReadFailureDoesNotOverwriteSettingsFingerprint()
    {
        var normalSettings = new JsonSettingsStore(_paths, _logger);
        var originalSettings = new AppSettings
        {
            LastCheckDate = DateOnly.FromDateTime(DateTime.Today.AddDays(-1)),
            LastCertificateSnapshotHash = "ORIGINAL"
        };
        Assert.True(normalSettings.Save(originalSettings));
        var originalJson = File.ReadAllText(_paths.SettingsPath);
        var stateStore = new JsonStateStore(_paths, _logger, new JsonStoreReadHooks
        {
            AcquireLock = _ => false
        });
        var check = new CertificateCheckService(
            normalSettings,
            stateStore,
            new FixedCertificateReader(_logger, [DueCertificate()]),
            new ExpiryEvaluator(),
            _logger);
        var coordinator = new NotificationCheckCoordinator(normalSettings, check, _logger);

        var result = coordinator.Run(new CheckCycleRequest(true, true), _ => true);

        Assert.Equal(CheckCycleStatus.ReadFailed, result.Status);
        Assert.True(result.ShouldRetry);
        Assert.Equal(originalJson, File.ReadAllText(_paths.SettingsPath));
    }

    private NotificationCheckCoordinator CreateCoordinator(
        IReadOnlyList<CertificateSnapshot> certificates,
        out JsonSettingsStore settingsStore,
        out JsonStateStore stateStore,
        bool forceReminder = false,
        JsonStoreReadHooks? stateHooks = null)
    {
        settingsStore = new JsonSettingsStore(_paths, _logger);
        Assert.True(settingsStore.Save(new AppSettings { ForceNextNotificationReminder = forceReminder }));
        stateStore = new JsonStateStore(_paths, _logger, stateHooks);
        var check = new CertificateCheckService(
            settingsStore,
            stateStore,
            new FixedCertificateReader(_logger, certificates),
            new ExpiryEvaluator(),
            _logger);
        return new NotificationCheckCoordinator(settingsStore, check, _logger);
    }

    [Fact]
    public void RepeatedManualCheckDoesNotDuplicateOrClaimCertificateIsOutsideExpiryWindow()
    {
        var coordinator = CreateCoordinator([DueCertificate()], out _, out _);
        var notifications = 0;
        var first = coordinator.Run(new CheckCycleRequest(true, true), _ => { notifications++; return true; });
        var second = coordinator.Run(new CheckCycleRequest(true, true), _ => { notifications++; return true; });

        Assert.Equal(CheckCycleStatus.NotificationShown, first.Status);
        Assert.Equal(CheckCycleStatus.CompletedNoDue, second.Status);
        Assert.Equal(1, notifications);
        Assert.Equal("Nenhum novo aviso pendente. Consulte os certificados monitorados.", second.ManualFeedback);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CorruptSourceRemainsBlockedAcrossCyclesAndNewCoordinator(bool corruptSettings)
    {
        var settingsStore = new JsonSettingsStore(_paths, _logger);
        var stateStore = new JsonStateStore(_paths, _logger);
        Assert.True(settingsStore.Save(new AppSettings { DailyCheckTime = new TimeSpan(12, 30, 0) }));
        Assert.True(stateStore.Save(new Dictionary<string, CertificateStateRecord>()));
        var corruptPath = corruptSettings ? _paths.SettingsPath : _paths.StatePath;
        var otherPath = corruptSettings ? _paths.StatePath : _paths.SettingsPath;
        var otherBytes = File.ReadAllBytes(otherPath);
        File.WriteAllText(corruptPath, "{invalid");
        var notifications = 0;

        for (var restart = 0; restart < 2; restart++)
        {
            var settings = new JsonSettingsStore(_paths, _logger);
            var state = new JsonStateStore(_paths, _logger);
            var service = new CertificateCheckService(settings, state,
                new FixedCertificateReader(_logger, [DueCertificate()]), new ExpiryEvaluator(), _logger);
            var coordinator = new NotificationCheckCoordinator(settings, service, _logger);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var result = coordinator.Run(new CheckCycleRequest(true, true), _ => { notifications++; return true; });
                Assert.Equal(CheckCycleStatus.ReadFailed, result.Status);
                Assert.True(result.ShouldRetry);
                Assert.NotNull(result.ManualFeedback);
            }
        }

        Assert.Equal(0, notifications);
        Assert.Equal("{invalid", File.ReadAllText(corruptPath));
        Assert.Equal(otherBytes, File.ReadAllBytes(otherPath));
    }

    private static CertificateSnapshot DueCertificate() =>
        new("AABBCCDDEEFF0011223344556677889900AABBCC", "CN=Teste", "CN=Issuer", DateTime.Today.AddDays(5), "01", "Teste");

    private sealed class FixedCertificateReader : CertificateReader
    {
        private readonly IReadOnlyList<CertificateSnapshot> _certificates;

        public FixedCertificateReader(FileLogger logger, IReadOnlyList<CertificateSnapshot> certificates) : base(logger)
        {
            _certificates = certificates;
        }

        public override CertificateReadResult ReadCurrentUserPersonalCertificates() =>
            CertificateReadResult.Complete(_certificates);
    }
}
