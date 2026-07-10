using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

/// <summary>
/// Testa persistencia, envelope versionado e compatibilidade com formato legado
/// do JsonSettingsStore.
/// </summary>
public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppPaths _paths;
    private readonly FileLogger _logger;
    private readonly JsonSettingsStore _store;

    public JsonSettingsStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"SettingsStoreTests_{Guid.NewGuid():N}");
        _paths   = new AppPaths(_tempDir);
        _logger  = new FileLogger(_paths);
        _store   = new JsonSettingsStore(_paths, _logger);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void LoadReturnsDefaultsWhenFileDoesNotExist()
    {
        var settings = _store.Load();

        Assert.NotNull(settings);
        // Defaults documentados em AppSettings
        Assert.NotNull(settings.Thresholds);
        Assert.False(File.Exists(_paths.LogPath));
    }

    [Fact]
    public void SavedSettingsCanBeReloaded()
    {
        var original = new AppSettings
        {
            DailyCheckTime = TimeSpan.FromHours(9),
            StartupEnabled = true,
            ForceNextNotificationReminder = true,
            Thresholds = new ExpiryThresholds { Level1 = 2, Level7 = 5, Level15 = 15, Level30 = 45 }
        };

        _store.Save(original);
        var loaded = _store.Load();

        Assert.Equal(original.DailyCheckTime, loaded.DailyCheckTime);
        Assert.Equal(original.StartupEnabled, loaded.StartupEnabled);
        Assert.True(loaded.ForceNextNotificationReminder);
        Assert.Equal(45, loaded.Thresholds.Level30);
        Assert.Equal(5,  loaded.Thresholds.Level7);
    }

    [Fact]
    public void SaveRecreatesMissingDataDirectory()
    {
        Directory.Delete(_paths.RootDirectory, recursive: true);

        _store.Save(new AppSettings { DailyCheckTime = TimeSpan.FromHours(7) });

        Assert.True(File.Exists(_paths.SettingsPath));
        Assert.Equal(TimeSpan.FromHours(7), _store.Load().DailyCheckTime);
    }

    [Fact]
    public void SecondSaveReplacesExistingSettingsFile()
    {
        _store.Save(new AppSettings { DailyCheckTime = TimeSpan.FromHours(7) });

        _store.Save(new AppSettings { DailyCheckTime = TimeSpan.FromHours(8) });

        Assert.Equal(TimeSpan.FromHours(8), _store.Load().DailyCheckTime);
        Assert.Single(Directory.GetFiles(_tempDir, "settings.json"));
    }

    [Fact]
    public void SaveProducesEnvelopeWithVersionField()
    {
        var settings = new AppSettings { DailyCheckTime = TimeSpan.FromHours(14) };
        _store.Save(settings);

        var json = File.ReadAllText(_paths.SettingsPath);

        Assert.Contains("\"version\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"settings\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LegacyFormatWithoutEnvelopeIsLoadedCorrectly()
    {
        // Simula um settings.json escrito por versao antiga (sem envelope).
        // Apenas o objeto AppSettings serializado diretamente.
        var legacyJson = """
        {
          "DailyCheckTime": "08:30:00",
          "StartupEnabled": true,
          "Thresholds": {
            "Level1": 1,
            "Level7": 7,
            "Level15": 15,
            "Level30": 30
          }
        }
        """;
        File.WriteAllText(_paths.SettingsPath, legacyJson);

        var loaded = _store.Load();

        Assert.Equal(TimeSpan.FromHours(8).Add(TimeSpan.FromMinutes(30)), loaded.DailyCheckTime);
        Assert.True(loaded.StartupEnabled);
        Assert.Equal(30, loaded.Thresholds.Level30);
    }

    [Fact]
    public void LegacyFormatIsMigratedToEnvelopeOnNextSave()
    {
        // Escreve formato legado
        var legacyJson = """{"DailyCheckTime":"10:00:00","Thresholds":{"Level1":1,"Level7":7,"Level15":15,"Level30":30}}""";
        File.WriteAllText(_paths.SettingsPath, legacyJson);

        // Load + Save deve migrar
        var loaded = _store.Load();
        _store.Save(loaded);

        var newJson = File.ReadAllText(_paths.SettingsPath);
        Assert.Contains("\"version\"", newJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"settings\"", newJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CorruptJsonReturnsDefaultsAndPreservesCorruptFile()
    {
        File.WriteAllText(_paths.SettingsPath, "{ this is not valid json");

        var succeeded = _store.TryLoad(out var loaded);

        Assert.False(succeeded);
        Assert.NotNull(loaded);
        // Defaults retornados
        Assert.NotNull(loaded.Thresholds);
        // O arquivo corrompido foi preservado (renomeado), settings.json removido.
        var corruptFiles = Directory.GetFiles(_tempDir, "settings.json.corrupt-*");
        Assert.NotEmpty(corruptFiles);
    }

    [Fact]
    public void TransientReadIoErrorReturnsDefaultsWithoutPreservingAsCorrupt()
    {
        _store.Save(new AppSettings { DailyCheckTime = TimeSpan.FromHours(6) });

        using var locked = new FileStream(_paths.SettingsPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var succeeded = _store.TryLoad(out var loaded);

        Assert.False(succeeded);
        Assert.Equal(TimeSpan.FromHours(9), loaded.DailyCheckTime);
        Assert.True(File.Exists(_paths.SettingsPath));
        Assert.Empty(Directory.GetFiles(_tempDir, "settings.json.corrupt-*"));
    }

    [Fact]
    public void RepeatedCorruptJsonPreservesEveryCorruptFile()
    {
        File.WriteAllText(_paths.SettingsPath, "{ primeiro json quebrado");
        Assert.False(_store.TryLoad(out _));

        File.WriteAllText(_paths.SettingsPath, "{ segundo json quebrado");
        Assert.False(_store.TryLoad(out _));

        var corruptFiles = Directory.GetFiles(_tempDir, "settings.json.corrupt-*");
        Assert.Equal(2, corruptFiles.Length);
    }

    [Fact]
    public void SettingsFileAtExactSizeLimitStillLoads()
    {
        File.WriteAllText(_paths.SettingsPath, CreateSettingsEnvelopeWithExactLength(1_048_576));

        var loaded = _store.Load();

        Assert.Equal(new TimeSpan(6, 30, 0), loaded.DailyCheckTime);
        Assert.True(File.Exists(_paths.SettingsPath));
        Assert.Empty(Directory.GetFiles(_tempDir, "settings.json.corrupt-*"));
    }

    [Fact]
    public void SaveThrowsOnNullSettings()
    {
        Assert.Throws<ArgumentNullException>(() => _store.Save(settings: null!));
    }

    [Fact]
    public void EnvelopeWithFutureVersionStillDeserializes()
    {
        // Forward compat minima: se um envelope com version=2 aparecer e tiver
        // o campo "settings" valido, ainda assim deve carregar os dados.
        var futureJson = """
        {
          "version": 2,
          "settings": {
            "DailyCheckTime": "07:00:00"
          },
          "extraFutureField": "ignored"
        }
        """;
        File.WriteAllText(_paths.SettingsPath, futureJson);

        var loaded = _store.Load();

        Assert.Equal(TimeSpan.FromHours(7), loaded.DailyCheckTime);
    }

    [Fact]
    public void EnvelopeWithoutVersionStillDeserializesSettingsProperty()
    {
        File.WriteAllText(_paths.SettingsPath, """{"settings":{"dailyCheckTime":"05:45:00"}}""");

        var loaded = _store.Load();

        Assert.Equal(new TimeSpan(5, 45, 0), loaded.DailyCheckTime);
    }

    [Fact]
    public void EnvelopeWithStringVersionStillDeserializesSettingsProperty()
    {
        File.WriteAllText(_paths.SettingsPath, """{"version":"1","settings":{"dailyCheckTime":"06:15:00"}}""");

        var loaded = _store.Load();

        Assert.Equal(new TimeSpan(6, 15, 0), loaded.DailyCheckTime);
    }

    [Fact]
    public void LowercaseEnvelopeSettingsPropertyIsLoaded()
    {
        var json = """
        {
          "version": 1,
          "settings": {
            "dailyCheckTime": "22:45:00",
            "startupEnabled": false,
            "thresholds": {
              "level1": 2,
              "level7": 8,
              "level15": 16,
              "level30": 31
            }
          }
        }
        """;
        File.WriteAllText(_paths.SettingsPath, json);

        var loaded = _store.Load();

        Assert.Equal(new TimeSpan(22, 45, 0), loaded.DailyCheckTime);
        Assert.False(loaded.StartupEnabled);
        Assert.Equal(31, loaded.Thresholds.Level30);
        Assert.Equal(2, loaded.Thresholds.Level1);
    }

    [Fact]
    public void NullThresholdsCanBeNormalizedByRuntime()
    {
        File.WriteAllText(_paths.SettingsPath, """{"version":1,"settings":{"dailyCheckTime":"00:00:00","thresholds":null}}""");

        var loaded = _store.Load();

        Assert.Null(loaded.Thresholds);
        var normalized = (loaded.Thresholds ?? new ExpiryThresholds()).Normalized();
        Assert.Equal(30, normalized.Level30);
        Assert.Equal(1, normalized.Level1);
    }

    [Fact]
    public void EnvelopeWithNullSettingsReturnsDefaultsWithoutPreservingAsCorrupt()
    {
        File.WriteAllText(_paths.SettingsPath, """{"version":1,"settings":null}""");

        var loaded = _store.Load();

        Assert.Equal(TimeSpan.FromHours(9), loaded.DailyCheckTime);
        Assert.NotNull(loaded.Thresholds);
        Assert.True(File.Exists(_paths.SettingsPath));
        Assert.Empty(Directory.GetFiles(_tempDir, "settings.json.corrupt-*"));
    }

    [Fact]
    public void NonObjectRootReturnsDefaultsWithoutPreservingAsCorrupt()
    {
        File.WriteAllText(_paths.SettingsPath, "[]");

        var loaded = _store.Load();

        Assert.Equal(TimeSpan.FromHours(9), loaded.DailyCheckTime);
        Assert.NotNull(loaded.Thresholds);
        Assert.True(File.Exists(_paths.SettingsPath));
        Assert.Empty(Directory.GetFiles(_tempDir, "settings.json.corrupt-*"));
    }

    private static string CreateSettingsEnvelopeWithExactLength(int length)
    {
        const string prefix = "{\"version\":1,\"settings\":{\"DailyCheckTime\":\"06:30:00\"},\"padding\":\"";
        const string suffix = "\"}";
        var paddingLength = length - prefix.Length - suffix.Length;
        Assert.True(paddingLength >= 0);
        return prefix + new string('x', paddingLength) + suffix;
    }
}
