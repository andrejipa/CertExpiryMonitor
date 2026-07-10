using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class AppPathsTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"AppPathsTests_{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void RootOverrideCreatesExpectedDataPaths()
    {
        var paths = new AppPaths(_tempDir);

        Assert.True(Directory.Exists(_tempDir));
        Assert.Equal(Path.Combine(_tempDir, "settings.json"), paths.SettingsPath);
        Assert.Equal(Path.Combine(_tempDir, "certificate-state.json"), paths.StatePath);
        Assert.Equal(Path.Combine(_tempDir, "monitor.log"), paths.LogPath);
        Assert.Equal(Path.Combine(_tempDir, "telemetry.json"), paths.TelemetryPath);
        Assert.Equal(Path.Combine(_tempDir, "diagnostics.db"), paths.DiagnosticsDbPath);
    }
}
