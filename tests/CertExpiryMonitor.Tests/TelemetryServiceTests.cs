using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class TelemetryServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppPaths _paths;
    private readonly FileLogger _logger;
    private readonly TelemetryService _telemetry;

    public TelemetryServiceTests()
    {
        _tempDir   = Path.Combine(Path.GetTempPath(), $"Telemetry_{Guid.NewGuid():N}");
        _paths     = new AppPaths(_tempDir);
        _logger    = new FileLogger(_paths);
        _telemetry = new TelemetryService(_paths, _logger);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Increment_NoOpWhenDisabled()
    {
        _telemetry.Enabled = false;
        _telemetry.Increment(t => t.TotalChecks++);

        var loaded = _telemetry.Load();
        Assert.Equal(0, loaded.TotalChecks);
    }

    [Fact]
    public void Increment_PersistsWhenEnabled()
    {
        _telemetry.Enabled = true;
        _telemetry.Increment(t => t.TotalChecks++);
        _telemetry.Increment(t => t.NotificationsShown += 3);

        var loaded = _telemetry.Load();
        Assert.Equal(1, loaded.TotalChecks);
        Assert.Equal(3, loaded.NotificationsShown);
    }

    [Fact]
    public void Load_ReturnsEmptyEnvelopeWhenFileDoesNotExist()
    {
        var env = _telemetry.Load();

        Assert.NotNull(env);
        Assert.Equal(0, env.TotalChecks);
        Assert.Equal(1, env.Version);
    }

    [Fact]
    public void Reset_DeletesFileAndCountersReturnToZero()
    {
        _telemetry.Enabled = true;
        _telemetry.Increment(t => t.TotalChecks = 100);
        Assert.Equal(100, _telemetry.Load().TotalChecks);

        _telemetry.Reset();

        Assert.Equal(0, _telemetry.Load().TotalChecks);
    }

    [Fact]
    public void Increment_UpdatedAtChangesWhenIncrementing()
    {
        _telemetry.Enabled = true;
        _telemetry.Increment(t => t.TotalChecks++);
        var firstUpdate = _telemetry.Load().UpdatedAt;

        Thread.Sleep(20);  // garante diferença mensurável
        _telemetry.Increment(t => t.TotalChecks++);
        var secondUpdate = _telemetry.Load().UpdatedAt;

        Assert.True(secondUpdate > firstUpdate);
    }

    [Fact]
    public void Increment_PreservesCreatedAtAcrossUpdates()
    {
        _telemetry.Enabled = true;
        _telemetry.Increment(t => t.TotalChecks++);
        var firstCreate = _telemetry.Load().CreatedAt;

        Thread.Sleep(20);
        _telemetry.Increment(t => t.TotalChecks++);
        var secondCreate = _telemetry.Load().CreatedAt;

        // CreatedAt nao deve mudar entre incrementos (so e setado na criacao do arquivo)
        Assert.Equal(firstCreate, secondCreate);
    }
}
