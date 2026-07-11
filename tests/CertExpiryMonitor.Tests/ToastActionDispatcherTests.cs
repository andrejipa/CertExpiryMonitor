using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class ToastActionDispatcherTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ToastDispatcher_{Guid.NewGuid():N}");
    private readonly JsonStateStore _stateStore;
    private readonly ToastActionDispatcher _dispatcher;

    public ToastActionDispatcherTests()
    {
        var paths = new AppPaths(_tempDir);
        var logger = new FileLogger(paths);
        _stateStore = new JsonStateStore(paths, logger);
        var actions = new CertificateStateActions(
            _stateStore,
            new ExpiryEvaluator(),
            new TelemetryService(paths, logger),
            logger);
        _dispatcher = new ToastActionDispatcher(() => CurrentPlan(), actions, logger);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("action=view-details")]
    public void MissingOrViewActionOpensDetails(string? arguments)
    {
        var details = 0;

        _dispatcher.Dispatch(arguments, () => { }, () => details++);

        Assert.Equal(1, details);
    }

    [Fact]
    public void ConfigureActionOpensSettingsOnly()
    {
        var settings = 0;
        var details = 0;

        _dispatcher.Dispatch("action=configure-time", () => settings++, () => details++);

        Assert.Equal(1, settings);
        Assert.Equal(0, details);
    }

    [Fact]
    public void DismissOnePersistsRequestedThumbprint()
    {
        _dispatcher.Dispatch("action=dismiss-one&thumbprint=AA%20BB", () => { }, () => { });

        Assert.True(_stateStore.TryLoad(out var state));
        Assert.Equal(CertificateNotificationState.Dismissed, state["AABB"].State);
    }

    [Fact]
    public void ExplicitDismissAllPersistsEveryThumbprint()
    {
        _dispatcher.Dispatch("action=dismiss-all&thumbprints=AA%3BBB", () => { }, () => { });

        Assert.True(_stateStore.TryLoad(out var state));
        Assert.Equal(2, state.Count);
        Assert.All(state.Values, record => Assert.Equal(CertificateNotificationState.Dismissed, record.State));
    }

    [Fact]
    public void DismissAllWithoutArgumentsUsesCurrentPlan()
    {
        _dispatcher.Dispatch("action=dismiss-all", () => { }, () => { });

        Assert.True(_stateStore.TryLoad(out var state));
        Assert.Contains("CURRENT", state.Keys);
    }

    [Theory]
    [InlineData("action=remind-later")]
    [InlineData("action=unknown")]
    [InlineData("action=dismiss-one")]
    public void NonActionableArgumentsAreNoOps(string arguments)
    {
        _dispatcher.Dispatch(arguments, () => throw new InvalidOperationException(), () => throw new InvalidOperationException());

        Assert.False(File.Exists(new AppPaths(_tempDir).StatePath));
    }

    [Fact]
    public void CallbackFailureIsContained()
    {
        var exception = Record.Exception(() =>
            _dispatcher.Dispatch("action=view-details", () => { }, () => throw new InvalidOperationException("boom")));

        Assert.Null(exception);
    }

    [Fact]
    public void NullCallbacksAreRejected()
    {
        Assert.Throws<ArgumentNullException>(() => _dispatcher.Dispatch("", null!, () => { }));
        Assert.Throws<ArgumentNullException>(() => _dispatcher.Dispatch("", () => { }, null!));
    }

    private static NotificationPlan CurrentPlan() => new()
    {
        DueCertificates =
        [
            new CertificateDueNotification(
                new CertificateSnapshot("CURRENT", "CN=Current", "CN=Issuer", DateTime.Today.AddDays(10), "SERIAL"),
                ExpiryBucket.Days15,
                10)
        ]
    };

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
