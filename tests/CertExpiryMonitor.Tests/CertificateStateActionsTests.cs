using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class CertificateStateActionsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppPaths _paths;
    private readonly FileLogger _logger;
    private readonly JsonStateStore _stateStore;
    private readonly TelemetryService _telemetry;
    private readonly CertificateStateActions _actions;

    public CertificateStateActionsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CertificateActions_{Guid.NewGuid():N}");
        _paths = new AppPaths(_tempDir);
        _logger = new FileLogger(_paths);
        _stateStore = new JsonStateStore(_paths, _logger);
        _telemetry = new TelemetryService(_paths, _logger) { Enabled = true };
        _actions = new CertificateStateActions(
            _stateStore,
            new ExpiryEvaluator(),
            _telemetry,
            _logger);
    }

    [Fact]
    public void DismissOnePersistsStateAndTelemetry()
    {
        Assert.True(_actions.DismissOne("AA BB"));
        Assert.True(_stateStore.TryLoad(out var state));

        var record = Assert.Single(state).Value;
        Assert.Equal("AABB", record.Thumbprint);
        Assert.Equal(CertificateNotificationState.Dismissed, record.State);
        Assert.Equal(1, _telemetry.Load().DismissOne);
    }

    [Fact]
    public void DismissAllDeduplicatesThumbprintsAndCountsOneAction()
    {
        Assert.True(_actions.DismissAll(["AA", "aa", "BB", " "]));
        Assert.True(_stateStore.TryLoad(out var state));

        Assert.Equal(2, state.Count);
        Assert.All(state.Values, record => Assert.Equal(CertificateNotificationState.Dismissed, record.State));
        Assert.Equal(1, _telemetry.Load().DismissAll);
    }

    [Fact]
    public void RestoreOneClearsDismissedStateAndUpdatesTelemetry()
    {
        Assert.True(_actions.DismissOne("AA"));

        Assert.True(_actions.RestoreOne("AA"));
        Assert.True(_stateStore.TryLoad(out var state));

        Assert.Equal(CertificateNotificationState.None, state["AA"].State);
        Assert.Equal(1, _telemetry.Load().Restore);
    }

    [Fact]
    public void ReadFailureDoesNotOverwriteExistingState()
    {
        Directory.CreateDirectory(_paths.RootDirectory);
        const string corrupt = "{ estado existente invalido";
        File.WriteAllText(_paths.StatePath, corrupt);

        Assert.False(_actions.DismissOne("AA"));

        Assert.Equal(corrupt, File.ReadAllText(_paths.StatePath));
        Assert.Empty(Directory.GetFiles(_tempDir, "certificate-state.json.corrupt-*"));
        Assert.False(_actions.DismissOne("AA"));
        Assert.Equal(corrupt, File.ReadAllText(_paths.StatePath));
        Assert.Equal(0, _telemetry.Load().DismissOne);
    }

    [Fact]
    public void EmptyDismissAllIsSuccessfulNoOp()
    {
        Assert.True(_actions.DismissAll([]));

        Assert.False(File.Exists(_paths.StatePath));
        Assert.Equal(0, _telemetry.Load().DismissAll);
    }

    [Fact]
    public void BlankThumbprintsAreRejectedForSingleActions()
    {
        Assert.Throws<ArgumentException>(() => _actions.DismissOne(" "));
        Assert.Throws<ArgumentException>(() => _actions.RestoreOne(" "));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
