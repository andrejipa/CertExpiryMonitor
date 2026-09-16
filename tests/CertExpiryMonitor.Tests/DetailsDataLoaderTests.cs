using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class DetailsDataLoaderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "CertExpiryMonitor-DetailsLoad-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LoadKeepsSettingsAndStateAvailabilityIndependent(bool settingsAvailable, bool stateAvailable)
    {
        var paths = new AppPaths(_root);
        var logger = new FileLogger(paths);
        File.WriteAllText(paths.SettingsPath, "{\"DailyCheckTime\":\"12:30:00\"}");
        File.WriteAllText(paths.StatePath, "[]");
        var settingsStore = new JsonSettingsStore(paths, logger, new JsonStoreReadHooks
        {
            ReadAllText = path => settingsAvailable ? File.ReadAllText(path) : throw new IOException("indisponivel")
        });
        var stateStore = new JsonStateStore(paths, logger, new JsonStoreReadHooks
        {
            ReadAllText = path => stateAvailable ? File.ReadAllText(path) : throw new IOException("indisponivel")
        });
        var certificates = CertificateReadResult.Complete([
            new CertificateSnapshot("ABC", "CN=Teste", "CN=Teste", DateTime.Today.AddDays(5), "01", "Teste")]);
        var loader = new DetailsDataLoader(settingsStore, stateStore, new FixedReader(logger, certificates));

        var result = loader.Load();

        Assert.Equal(settingsAvailable, result.SettingsAvailable);
        Assert.Equal(stateAvailable, result.StateAvailable);
        Assert.Same(certificates, result.CertificateRead);
        Assert.Single(result.CertificateRead.Certificates);
        if (settingsAvailable) Assert.Equal(new TimeSpan(12, 30, 0), result.Settings!.DailyCheckTime);
        else Assert.Null(result.Settings);
        Assert.Equal("{\"DailyCheckTime\":\"12:30:00\"}", File.ReadAllText(paths.SettingsPath));
        Assert.Equal("[]", File.ReadAllText(paths.StatePath));
    }

    [Fact]
    public void CertificateFailureDoesNotHideReadableSettings()
    {
        var paths = new AppPaths(_root);
        var logger = new FileLogger(paths);
        var settings = new JsonSettingsStore(paths, logger);
        Assert.True(settings.Save(new AppSettings { DailyCheckTime = new TimeSpan(10, 30, 0) }));
        var failedRead = new CertificateReadResult([], CertificateReadStatus.StoreFailure, 1);
        var loader = new DetailsDataLoader(settings, new JsonStateStore(paths, logger), new FixedReader(logger, failedRead));

        var result = loader.Load();

        Assert.True(result.SettingsAvailable);
        Assert.True(result.StateAvailable);
        Assert.False(result.CertificateRead.IsComplete);
        Assert.Equal(new TimeSpan(10, 30, 0), result.Settings!.DailyCheckTime);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FixedReader(FileLogger logger, CertificateReadResult result) : CertificateReader(logger)
    {
        public override CertificateReadResult ReadCurrentUserPersonalCertificates() => result;
    }
}
