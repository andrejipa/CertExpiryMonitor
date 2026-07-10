using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using System.Text.Json;
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
        var succeeded = _store.TryLoad(out var state);

        Assert.True(succeeded);
        Assert.Empty(state);
        Assert.False(File.Exists(_paths.LogPath));
    }

    [Fact]
    public void SavedStateCanBeReloaded()
    {
        var record = new CertificateStateRecord
        {
            Thumbprint = "AABBCC",
            NotAfter   = new DateTime(2026, 6, 30),
            State      = CertificateNotificationState.NotifiedLong
        };
        var state = new Dictionary<string, CertificateStateRecord>(StringComparer.OrdinalIgnoreCase)
        {
            ["AABBCC"] = record
        };

        _store.Save(state);
        var loaded = _store.Load();

        var entry = Assert.Single(loaded);
        Assert.Equal("AABBCC",                                  entry.Key);
        Assert.Equal(CertificateNotificationState.NotifiedLong,   entry.Value.State);
        Assert.Equal(new DateTime(2026, 6, 30),                 entry.Value.NotAfter);
        AssertEncryptedStateFileDoesNotExpose("AABBCC", "records", "2026-06-30");
    }

    [Fact]
    public void SaveRecreatesMissingDataDirectory()
    {
        Directory.Delete(_paths.RootDirectory, recursive: true);

        _store.Save(MakeState(("AABBCC", CertificateNotificationState.NotifiedLong)));

        Assert.True(File.Exists(_paths.StatePath));
        Assert.True(_store.Load().ContainsKey("AABBCC"));
    }

    [Fact]
    public void MultipleSaveLoadCyclesPreserveAllRecords()
    {
        var state1 = MakeState(("AA", CertificateNotificationState.NotifiedShort),
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
        Assert.Equal(CertificateNotificationState.NotifiedShort,  final["AA"].State);
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
        Assert.Equal(CertificateNotificationState.NotifiedLong,   entry.Value.State);
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
        Assert.Contains("\"format\"", savedJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"payload\"", savedJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dpapi-current-user", savedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"records\"", savedJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AABB", savedJson, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(_paths.StatePath + ".bak"), "Migracao para DPAPI nao deve deixar plaintext em .bak.");
    }

    [Fact]
    public void LowercaseEnvelopeFormatIsLoadedCorrectly()
    {
        var envelopeJson = """
            {
              "version": 1,
              "records": [
                { "thumbprint": "AABB", "notAfter": "2026-06-30T00:00:00", "state": 30, "lastNotifiedAt": null }
              ]
            }
            """;

        File.WriteAllText(_paths.StatePath, envelopeJson);

        var state = _store.Load();

        var entry = Assert.Single(state);
        Assert.Equal("AABB", entry.Key);
        Assert.Equal(CertificateNotificationState.NotifiedLong, entry.Value.State);
    }

    [Fact]
    public void EnvelopeWithoutVersionStillLoadsRecordsProperty()
    {
        File.WriteAllText(_paths.StatePath, """
            {
              "records": [
                { "thumbprint": "AABB", "notAfter": "2026-06-30T00:00:00", "state": 30, "lastNotifiedAt": null }
              ]
            }
            """);

        var state = _store.Load();

        var entry = Assert.Single(state);
        Assert.Equal("AABB", entry.Key);
        Assert.Equal(CertificateNotificationState.NotifiedLong, entry.Value.State);
        Assert.Empty(Directory.GetFiles(_tempDir, "certificate-state.json.corrupt-*"));
    }

    [Fact]
    public void EnvelopeWithStringVersionStillLoadsRecordsProperty()
    {
        File.WriteAllText(_paths.StatePath, """
            {
              "version": "1",
              "records": [
                { "thumbprint": "CCDD", "notAfter": "2026-07-31T00:00:00", "state": 7, "lastNotifiedAt": null }
              ]
            }
            """);

        var state = _store.Load();

        var entry = Assert.Single(state);
        Assert.Equal("CCDD", entry.Key);
        Assert.Equal(CertificateNotificationState.NotifiedShort, entry.Value.State);
        Assert.Empty(Directory.GetFiles(_tempDir, "certificate-state.json.corrupt-*"));
    }

    [Fact]
    public void DuplicateThumbprintsKeepLastRecordFromFile()
    {
        var envelopeJson = """
            {
              "version": 1,
              "records": [
                { "thumbprint": "AA BB", "notAfter": "2026-06-30T00:00:00", "state": 30, "lastNotifiedAt": null },
                { "thumbprint": "AABB", "notAfter": "2026-07-31T00:00:00", "state": 7, "lastNotifiedAt": null }
              ]
            }
            """;

        File.WriteAllText(_paths.StatePath, envelopeJson);

        var state = _store.Load();

        var entry = Assert.Single(state);
        Assert.Equal("AABB", entry.Key);
        Assert.Equal(new DateTime(2026, 7, 31), entry.Value.NotAfter);
        Assert.Equal(CertificateNotificationState.NotifiedShort, entry.Value.State);
    }

    [Fact]
    public void BlankThumbprintsAreIgnoredOnLoad()
    {
        var envelopeJson = """
            {
              "version": 1,
              "records": [
                { "thumbprint": "", "notAfter": "2026-06-30T00:00:00", "state": 30, "lastNotifiedAt": null },
                { "thumbprint": "   ", "notAfter": "2026-07-31T00:00:00", "state": 7, "lastNotifiedAt": null },
                { "thumbprint": "AABB", "notAfter": "2026-08-31T00:00:00", "state": 1, "lastNotifiedAt": null }
              ]
            }
            """;

        File.WriteAllText(_paths.StatePath, envelopeJson);

        var state = _store.Load();

        var entry = Assert.Single(state);
        Assert.Equal("AABB", entry.Key);
        Assert.Equal(CertificateNotificationState.NotifiedUrgent, entry.Value.State);
    }

    [Fact]
    public void ThumbprintsThatNormalizeToEmptyAreIgnoredOnLoad()
    {
        var envelopeJson = """
            {
              "version": 1,
              "records": [
                { "thumbprint": "\u200E\u200F\uFEFF\u200B", "notAfter": "2026-06-30T00:00:00", "state": 30, "lastNotifiedAt": null },
                { "thumbprint": "AABB", "notAfter": "2026-08-31T00:00:00", "state": 1, "lastNotifiedAt": null }
              ]
            }
            """;

        File.WriteAllText(_paths.StatePath, envelopeJson);

        var state = _store.Load();

        var entry = Assert.Single(state);
        Assert.Equal("AABB", entry.Key);
        Assert.False(state.ContainsKey(string.Empty));
    }

    // -------------------------------------------------------------------------
    // Normalizacao de thumbprint
    // -------------------------------------------------------------------------

    [Fact]
    public void ThumbprintLookupIsCaseInsensitive()
    {
        var state = MakeState(("aabbccdd", CertificateNotificationState.NotifiedLong));

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
              { "Thumbprint": "AA\tBB\u200ECC\r\n", "NotAfter": "2026-06-30T00:00:00", "State": 0, "LastNotifiedAt": null }
            ]
            """;

        File.WriteAllText(_paths.StatePath, legacyJson);

        var loaded = _store.Load();

        Assert.True(loaded.ContainsKey("AABBCC"), "Chave normalizada deve existir sem whitespace/invisiveis");
    }

    [Fact]
    public void NormalizeThumbprintStripsAllFormatCharacters()
    {
        var normalized = JsonStateStore.NormalizeThumbprint("AA\u200BBB\u2060CC");

        Assert.Equal("AABBCC", normalized);
    }

    [Fact]
    public void SaveOmitsBlankThumbprintsAndNormalizesSavedRecords()
    {
        var state = new Dictionary<string, CertificateStateRecord>(StringComparer.OrdinalIgnoreCase)
        {
            ["blank"] = new()
            {
                Thumbprint = "\u200E\u200F\uFEFF\u200B",
                NotAfter = new DateTime(2026, 6, 30),
                State = CertificateNotificationState.NotifiedLong
            },
            ["valid"] = new()
            {
                Thumbprint = " aa bb ",
                NotAfter = new DateTime(2026, 8, 31),
                State = CertificateNotificationState.NotifiedUrgent
            }
        };

        Assert.True(_store.Save(state));
        var json = File.ReadAllText(_paths.StatePath);
        var loaded = _store.Load();

        var entry = Assert.Single(loaded);
        Assert.Equal("AABB", entry.Key);
        Assert.Equal("AABB", entry.Value.Thumbprint);
        Assert.DoesNotContain(@"""thumbprint"":", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AABB", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NormalizeThumbprintThrowsOnNull()
    {
        Assert.Throws<ArgumentNullException>(() => JsonStateStore.NormalizeThumbprint(null!));
    }

    [Fact]
    public void SaveThrowsOnNullState()
    {
        Assert.Throws<ArgumentNullException>(() => _store.Save(null!));
    }

    // -------------------------------------------------------------------------
    // Robustez ante corrupção
    // -------------------------------------------------------------------------

    [Fact]
    public void CorruptJsonReturnsEmptyStateAndPreservesCorruptFile()
    {
        File.WriteAllText(_paths.StatePath, "{ this is not valid json !!!");

        var succeeded = _store.TryLoad(out var state);

        Assert.False(succeeded);
        Assert.Empty(state);
        // O arquivo original nao deve mais existir (foi renomeado para .corrupt-*)
        Assert.False(File.Exists(_paths.StatePath));
        var corruptFiles = Directory.GetFiles(_tempDir, "*.corrupt-*");
        Assert.NotEmpty(corruptFiles);
    }

    [Fact]
    public void TransientReadIoErrorReturnsEmptyStateWithoutPreservingAsCorrupt()
    {
        Assert.True(_store.Save(MakeState(("AABBCC", CertificateNotificationState.Dismissed))));

        using var locked = new FileStream(_paths.StatePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var succeeded = _store.TryLoad(out var state);

        Assert.False(succeeded);
        Assert.Empty(state);
        Assert.True(File.Exists(_paths.StatePath));
        Assert.Empty(Directory.GetFiles(_tempDir, "certificate-state.json.corrupt-*"));
    }

    [Fact]
    public void InvalidEncryptedStateReturnsEmptyAndPreservesCorruptFile()
    {
        File.WriteAllText(_paths.StatePath, """
            {
              "version": 2,
              "format": "dpapi-current-user",
              "payload": "not-base64"
            }
            """);

        var succeeded = _store.TryLoad(out var state);

        Assert.False(succeeded);
        Assert.Empty(state);
        Assert.False(File.Exists(_paths.StatePath));
        Assert.Single(Directory.GetFiles(_tempDir, "certificate-state.json.corrupt-*"));
    }

    [Fact]
    public void RepeatedCorruptJsonPreservesEveryCorruptFile()
    {
        File.WriteAllText(_paths.StatePath, "{ primeiro state quebrado");
        Assert.False(_store.TryLoad(out _));

        File.WriteAllText(_paths.StatePath, "{ segundo state quebrado");
        Assert.False(_store.TryLoad(out _));

        var corruptFiles = Directory.GetFiles(_tempDir, "certificate-state.json.corrupt-*");
        Assert.Equal(2, corruptFiles.Length);
    }

    [Fact]
    public void StateFileAtExactSizeLimitStillLoads()
    {
        File.WriteAllText(_paths.StatePath, CreateStateEnvelopeWithExactLength(10_485_760));

        var state = _store.Load();

        var entry = Assert.Single(state);
        Assert.Equal("EDGE", entry.Key);
        Assert.True(File.Exists(_paths.StatePath));
        Assert.Empty(Directory.GetFiles(_tempDir, "certificate-state.json.corrupt-*"));
    }

    [Fact]
    public void EmptyJsonObjectReturnsEmptyState()
    {
        File.WriteAllText(_paths.StatePath, "{}");

        var state = _store.Load();

        Assert.Empty(state);
    }

    [Fact]
    public void EnvelopeWithNullRecordsReturnsEmptyState()
    {
        File.WriteAllText(_paths.StatePath, """{"version":1,"records":null}""");

        var state = _store.Load();

        Assert.Empty(state);
        Assert.True(File.Exists(_paths.StatePath));
        Assert.Empty(Directory.GetFiles(_tempDir, "certificate-state.json.corrupt-*"));
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

    private void AssertEncryptedStateFileDoesNotExpose(params string[] sensitiveFragments)
    {
        var json = File.ReadAllText(_paths.StatePath);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
        Assert.Equal(2, GetProperty(document.RootElement, "version").GetInt32());
        Assert.Equal("dpapi-current-user", GetProperty(document.RootElement, "format").GetString());
        Assert.False(string.IsNullOrWhiteSpace(GetProperty(document.RootElement, "payload").GetString()));

        foreach (var fragment in sensitiveFragments)
        {
            Assert.DoesNotContain(fragment, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static JsonElement GetProperty(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        throw new KeyNotFoundException(name);
    }

    private static string CreateStateEnvelopeWithExactLength(int length)
    {
        const string prefix = "{\"version\":1,\"records\":[{\"thumbprint\":\"EDGE\",\"notAfter\":\"2026-06-30T00:00:00\",\"state\":30,\"lastNotifiedAt\":null}],\"padding\":\"";
        const string suffix = "\"}";
        var paddingLength = length - prefix.Length - suffix.Length;
        Assert.True(paddingLength >= 0);
        return prefix + new string('x', paddingLength) + suffix;
    }
}
