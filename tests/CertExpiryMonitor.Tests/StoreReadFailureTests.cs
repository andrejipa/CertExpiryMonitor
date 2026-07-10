using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class StoreReadFailureTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"CertExpiryMonitor-ReadFailure-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void SettingsMissingIsSuccessfulAndReturnsDefaults()
    {
        var paths = new AppPaths(_tempDir);
        var store = new JsonSettingsStore(paths, new FileLogger(paths));

        Assert.True(store.TryLoad(out var settings));
        Assert.Equal(TimeSpan.FromHours(9), settings.DailyCheckTime);
    }

    [Fact]
    public void SettingsLockTimeoutFailsWithoutChangingExistingFile()
    {
        var paths = new AppPaths(_tempDir);
        const string original = "{\"DailyCheckTime\":\"06:00:00\"}";
        File.WriteAllText(paths.SettingsPath, original);
        var store = new JsonSettingsStore(paths, new FileLogger(paths), new JsonStoreReadHooks
        {
            AcquireLock = _ => false
        });

        Assert.False(store.TryLoad(out var settings));
        Assert.Equal(TimeSpan.FromHours(9), settings.DailyCheckTime);
        Assert.Equal(original, File.ReadAllText(paths.SettingsPath));
    }

    [Fact]
    public void SettingsTransientIoFailureDoesNotPreserveOrOverwriteExistingFile()
    {
        var paths = new AppPaths(_tempDir);
        const string original = "{\"DailyCheckTime\":\"06:00:00\"}";
        File.WriteAllText(paths.SettingsPath, original);
        var store = new JsonSettingsStore(paths, new FileLogger(paths), new JsonStoreReadHooks
        {
            AcquireLock = _ => true,
            ReleaseLock = () => { },
            ReadAllText = _ => throw new IOException("locked")
        });

        Assert.False(store.TryLoad(out _));
        Assert.Equal(original, File.ReadAllText(paths.SettingsPath));
        Assert.Empty(Directory.GetFiles(_tempDir, "settings.json.corrupt-*"));
    }

    [Fact]
    public void StateLockTimeoutFailsWithoutChangingExistingFile()
    {
        var paths = new AppPaths(_tempDir);
        const string original = "[]";
        File.WriteAllText(paths.StatePath, original);
        var store = new JsonStateStore(paths, new FileLogger(paths), new JsonStoreReadHooks
        {
            AcquireLock = _ => false
        });

        Assert.False(store.TryLoad(out var state));
        Assert.Empty(state);
        Assert.Equal(original, File.ReadAllText(paths.StatePath));
    }

    [Fact]
    public void StateTransientIoFailureDoesNotPreserveOrOverwriteExistingFile()
    {
        var paths = new AppPaths(_tempDir);
        const string original = "[]";
        File.WriteAllText(paths.StatePath, original);
        var store = new JsonStateStore(paths, new FileLogger(paths), new JsonStoreReadHooks
        {
            AcquireLock = _ => true,
            ReleaseLock = () => { },
            ReadAllText = _ => throw new IOException("locked")
        });

        Assert.False(store.TryLoad(out _));
        Assert.Equal(original, File.ReadAllText(paths.StatePath));
        Assert.Empty(Directory.GetFiles(_tempDir, "certificate-state.json.corrupt-*"));
    }
}
