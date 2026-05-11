using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

/// <summary>
/// Testa persistencia, migracao de formato legado e robustez do JsonStateStore.
/// Cada teste usa um diretorio temporario proprio para isolamento total.
/// </summary>
public sealed class JsonStateStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppPaths _paths;
    private readonly FileLogger _logger;
    private readonly JsonStateStore _store;

    public JsonStateStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CertExpiryMonitorTests_{Guid.NewGuid():N}");
        _paths   = new AppPaths(_tempDir);
        _logger  = new FileLogger(_paths);
        _store   = new JsonStateStore(_paths, _logger);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // -------------------------------------------------------------------------
    // Round-trip
    // -------------------------------------------------------------------------

    [Fact]
    public void LoadReturnsEmptyDictionaryWhenFileDoesNotExist()
    {
        var state = _store.Load();

        Assert.Empty(state);
    }

    [Fact]
    public void SavedStateCanBeReloaded()
    {
        var record = new CertificateStateRecord
        {
            Thumbprint = "AABBCC",
            NotAfter   = new DateTime(2026, 6, 30),
            State      = CertificateNotificationState.Notified30
        };
        var state = new Dictionary<string, CertificateStateRecord>(StringComparer.OrdinalIgnoreCase)
        {
            ["AABBCC"] = record
        };

        _store.Save(state);
        var loaded = _store.Load();

        var entry = Assert.Single(loaded);
        Assert.Equal("AABBCC",                                  entry.Key);
        Assert.Equal(CertificateNotificationState.Notified30,   entry.Value.State);
        Assert.Equal(new DateTime(2026, 6, 30),                 entry.Value.NotAfter);
    }

    [Fact]
    public void MultipleSaveLoadCyclesPreserveAllRecords()
    {
        var state1 = MakeState(("AA", CertificateNotificationState.Notified7),
                               ("BB", CertificateNotificationState.Dismissed));

        _store.Save(state1);

        var state2 = _store.Load();
        state2["CC"] = new CertificateStateRecord
        {
            Thumbprint = "CC",
            NotAfter   = new DateTime(2026, 12, 31),
            State      = CertificateNotificationState.None
        };
        _store.Save(state2);

        var final = _store.Load();
        Assert.Equal(3, final.Count);
        Assert.Equal(CertificateNotificationState.Notified7,  final["AA"].State);
        Assert.Equal(CertificateNotificationState.Dismissed,  final["BB"].State);
        Assert.Equal(CertificateNotificationState.None,       final["CC"].State);
    }

    // -------------------------------------------------------------------------
    // Formato legado (array na raiz)
    // -------------------------------------------------------------------------

    [Fact]
    public void LegacyArrayFormatIsLoadedCorrectly()
    {
        var legacyJson = """
            [
              {
                "Thumbprint": "AABB",
                "NotAfter": "2026-06-30T00:00:00",
                "State": 30,
                "LastNotifiedAt": null
              }
            ]
            """;

        File.WriteAllText(_paths.StatePath, legacyJson);

        var state = _store.Load();

        var entry = Assert.Single(state);
        Assert.Equal("AABB",                                    entry.Key);
        Assert.Equal(CertificateNotificationState.Notified30,   entry.Value.State);
    }

    [Fact]
    public void LegacyFormatIsMigratedToEnvelopeOnNextSave()
    {
        var legacyJson = """
            [
              { "Thumbprint": "AABB", "NotAfter": "2026-06-30T00:00:00", "State": 0, "LastNotifiedAt": null }
            ]
            """;

        File.WriteAllText(_paths.StatePath, legacyJson);

        var state = _store.Load();
        _store.Save(state);

        var savedJson = File.ReadAllText(_paths.StatePath);
        Assert.Contains("\"version\"", savedJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"records\"", savedJson, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Normalizacao de thumbprint
    // -------------------------------------------------------------------------

    [Fact]
    public void ThumbprintLookupIsCaseInsensitive()
    {
        var state = MakeState(("aabbccdd", CertificateNotificationState.Notified30));

        _store.Save(state);
        var loaded = _store.Load();

        Assert.True(loaded.ContainsKey("AABBCCDD"));
        Assert.True(loaded.ContainsKey("aabbccdd"));
    }

    [Fact]
    public void ThumbprintSpacesAreStrippedOnLoad()
    {
        var legacyJson = """
            [
              { "Thumbprint": "AA BB CC", "NotAfter": "2026-06-30T00:00:00", "State": 0, "LastNotifiedAt": null }
            ]
            """;

        File.WriteAllText(_paths.StatePath, legacyJson);

        var loaded = _store.Load();

        Assert.True(loaded.ContainsKey("AABBCC"), "Chave normalizada deve existir sem espacos");
    }

    // -------------------------------------------------------------------------
    // Robustez ante corrupção
    // -------------------------------------------------------------------------

    [Fact]
    public void CorruptJsonReturnsEmptyStateAndPreservesCorruptFile()
    {
        File.WriteAllText(_paths.StatePath, "{ this is not valid json !!!");

        var state = _store.Load();

        Assert.Empty(state);
        // O arquivo original nao deve mais existir (foi renomeado para .corrupt-*)
        Assert.False(File.Exists(_paths.StatePath));
        var corruptFiles = Directory.GetFiles(_tempDir, "*.corrupt-*");
        Assert.NotEmpty(corruptFiles);
    }

    [Fact]
    public void EmptyJsonObjectReturnsEmptyState()
    {
        File.WriteAllText(_paths.StatePath, "{}");

        var state = _store.Load();

        Assert.Empty(state);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static Dictionary<string, CertificateStateRecord> MakeState(
        params (string Thumbprint, CertificateNotificationState State)[] entries)
    {
        var dict = new Dictionary<string, CertificateStateRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tp, s) in entries)
        {
            dict[tp] = new CertificateStateRecord
            {
                Thumbprint = tp,
                NotAfter   = DateTime.Today.AddDays(30),
                State      = s
            };
        }
        return dict;
    }
}
