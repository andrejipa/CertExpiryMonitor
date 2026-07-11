using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class NotificationPresenterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"NotificationPresenter_{Guid.NewGuid():N}");
    private readonly AppPaths _paths;
    private readonly FileLogger _logger;
    private readonly JsonSettingsStore _settingsStore;

    public NotificationPresenterTests()
    {
        _paths = new AppPaths(_tempDir);
        _logger = new FileLogger(_paths);
        _settingsStore = new JsonSettingsStore(_paths, _logger);
    }

    [Fact]
    public void AcceptedToastDoesNotInvokeFallback()
    {
        var toastCalls = 0;
        var fallbackCalls = 0;
        var presenter = Presenter(
            (_, thresholds, sound) => { toastCalls++; Assert.Equal(30, thresholds.Level30); Assert.True(sound); return true; },
            (_, _, _) => { fallbackCalls++; return true; });

        var result = presenter.Show(Plan(), new AppSettings { NotificationSoundEnabled = true }, () => { });

        Assert.True(result.Shown);
        Assert.Equal(1, toastCalls);
        Assert.Equal(0, fallbackCalls);
    }

    [Fact]
    public void RejectedToastInvokesFallbackOnceAndPropagatesDetailsAction()
    {
        var fallbackCalls = 0;
        var detailsCalls = 0;
        var presenter = Presenter(
            (_, _, _) => false,
            (_, _, showDetails) => { fallbackCalls++; showDetails(); return true; });

        var result = presenter.Show(Plan(), new AppSettings(), () => detailsCalls++);

        Assert.True(result.Shown);
        Assert.Equal(1, fallbackCalls);
        Assert.Equal(1, detailsCalls);
    }

    [Fact]
    public void FallbackUsesLatestPersistedSettings()
    {
        Assert.True(_settingsStore.Save(new AppSettings
        {
            NotificationSoundEnabled = false,
            Thresholds = new ExpiryThresholds { Level1 = 2, Level7 = 8, Level15 = 16, Level30 = 31 }
        }));
        AppSettings? observed = null;
        var presenter = Presenter(
            (_, _, _) => false,
            (_, settings, _) => { observed = settings; return false; });

        var result = presenter.Show(
            Plan(),
            new AppSettings { NotificationSoundEnabled = true },
            () => { });

        Assert.False(result.Shown);
        Assert.NotNull(observed);
        Assert.False(observed.NotificationSoundEnabled);
        Assert.Equal(31, observed.Thresholds.Level30);
        Assert.Same(observed, result.Settings);
    }

    [Fact]
    public void MissingPersistedSettingsKeepsNormalizedCurrentSettings()
    {
        AppSettings? observed = null;
        var presenter = Presenter(
            (_, _, _) => false,
            (_, settings, _) => { observed = settings; return false; });
        var current = new AppSettings
        {
            InitialDelayMinutes = -1,
            Thresholds = new ExpiryThresholds { Level1 = 30, Level7 = 1, Level15 = 1, Level30 = 1 }
        };

        var result = presenter.Show(Plan(), current, () => { });

        Assert.Same(observed, result.Settings);
        Assert.Equal(5, result.Settings.InitialDelayMinutes);
        Assert.Equal(result.Settings.Thresholds.Normalized().Level30, result.Settings.Thresholds.Level30);
    }

    [Fact]
    public void SummaryIncludesOnlyNonEmptyBucketsUsingConfiguredThresholds()
    {
        var plan = Plan(
            (ExpiryBucket.Days30, 25),
            (ExpiryBucket.Days7, 4),
            (ExpiryBucket.Days7, 3));
        var thresholds = new ExpiryThresholds { Level1 = 2, Level7 = 9, Level15 = 18, Level30 = 40 };

        var summary = NotificationPresenter.BuildFallbackSummary(plan, thresholds);

        Assert.Contains("3 certificado(s)", summary, StringComparison.Ordinal);
        Assert.Contains("1 até 40 dias", summary, StringComparison.Ordinal);
        Assert.Contains("2 até 9 dias", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("18 dias", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("em 2 dia", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowRejectsNullInputs()
    {
        var presenter = Presenter((_, _, _) => true, (_, _, _) => true);

        Assert.Throws<ArgumentNullException>(() => presenter.Show(null!, new AppSettings(), () => { }));
        Assert.Throws<ArgumentNullException>(() => presenter.Show(Plan(), null!, () => { }));
        Assert.Throws<ArgumentNullException>(() => presenter.Show(Plan(), new AppSettings(), null!));
    }

    private NotificationPresenter Presenter(
        Func<NotificationPlan, ExpiryThresholds, bool, bool> toast,
        Func<NotificationPlan, AppSettings, Action, bool> fallback) =>
        new(toast, _settingsStore, _logger, showFallback: fallback);

    private static NotificationPlan Plan(params (ExpiryBucket Bucket, int Days)[] items)
    {
        if (items.Length == 0) items = [(ExpiryBucket.Days30, 20)];
        return new NotificationPlan
        {
            DueCertificates = items.Select((item, index) => new CertificateDueNotification(
                new CertificateSnapshot($"TP{index}", $"CN=Cert {index}", "CN=Issuer", DateTime.Today.AddDays(item.Days), $"SERIAL{index}"),
                item.Bucket,
                item.Days)).ToArray()
        };
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
