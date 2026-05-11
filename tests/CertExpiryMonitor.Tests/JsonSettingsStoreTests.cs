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
    }

    [Fact]
    public void SavedSettingsCanBeReloaded()
    {
        var original = new AppSettings
        {
            DailyCheckTime = TimeSpan.FromHours(9),
            StartupEnabled = true,
            Thresholds = new ExpiryThresholds { Level1 = 2, Level7 = 5, Level15 = 15, Level30 = 45 }
        };

        _store.Save(original);
        var loaded = _store.Load();

        Assert.Equal(original.DailyCheckTime, loaded.DailyCheckTime);
        Assert.Equal(original.StartupEnabled, loaded.StartupEnabled);
        Assert.Equal(45, loaded.Thresholds.Level30);
        Assert.Equal(5,  loaded.Thresholds.Level7);
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

        var loaded = _store.Load();

        Assert.NotNull(loaded);
        // Defaults retornados
        Assert.NotNull(loaded.Thresholds);
        // O arquivo corrompido foi preservado (renomeado), settings.json removido.
        var corruptFiles = Directory.GetFiles(_tempDir, "settings.json.corrupt-*");
        Assert.NotEmpty(corruptFiles);
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
}
