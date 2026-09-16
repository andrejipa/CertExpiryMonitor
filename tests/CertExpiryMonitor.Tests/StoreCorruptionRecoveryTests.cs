using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class StoreCorruptionRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"StoreRecovery-{Guid.NewGuid():N}");

    [Theory]
    [InlineData("{ broken")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("123")]
    [InlineData("{\"settings\":null}")]
    [InlineData("{\"settings\":[]}")]
    [InlineData("{\"version\":1}")]
    public void SettingsFailurePersistsAcrossReadsAndInstancesUntilExternalRepair(string invalid)
    {
        var paths = new AppPaths(_root);
        var logger = new FileLogger(paths);
        File.WriteAllText(paths.SettingsPath, invalid);
        var store = new JsonSettingsStore(paths, logger);
        Assert.False(store.TryLoad(out _));
        Assert.False(store.TryLoad(out _));
        var restarted = new JsonSettingsStore(paths, logger);
        Assert.False(restarted.TryLoad(out _));
        Assert.False(restarted.Save(new AppSettings()));
        Assert.Equal(invalid, File.ReadAllText(paths.SettingsPath));
        Assert.Empty(Directory.GetFiles(_root, "*.corrupt-*"));
        File.WriteAllText(paths.SettingsPath, "{\"DailyCheckTime\":\"06:45:00\"}");
        Assert.True(restarted.TryLoad(out var restored));
        Assert.Equal(new TimeSpan(6, 45, 0), restored.DailyCheckTime);
        Assert.True(restarted.Save(restored));
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("123")]
    [InlineData("[null]")]
    [InlineData("{\"records\":null}")]
    [InlineData("{\"records\":{}}")]
    [InlineData("{\"format\":\"dpapi-current-user\"}")]
    [InlineData("{\"format\":\"dpapi-current-user\",\"payload\":\"\"}")]
    [InlineData("{\"format\":\"dpapi-current-user\",\"payload\":\"  \"}")]
    [InlineData("{\"format\":\"dpapi-current-user\",\"payload\":null}")]
    [InlineData("{\"format\":\"dpapi-current-user\",\"payload\":123}")]
    [InlineData("{\"format\":\"unknown\",\"payload\":\"AAAA\",\"records\":[]}")]
    [InlineData("{\"payload\":\"AAAA\",\"records\":[]}")]
    [InlineData("{\"format\":\"dpapi-current-user\",\"payload\":\"not-base64\"}")]
    [InlineData("{\"format\":\"dpapi-current-user\",\"payload\":\"AAAA\"}")]
    public void StateFailurePersistsAcrossReadsAndInstancesUntilExternalRepair(string invalid)
    {
        var paths = new AppPaths(_root);
        var logger = new FileLogger(paths);
        File.WriteAllText(paths.StatePath, invalid);
        var store = new JsonStateStore(paths, logger);
        Assert.False(store.TryLoad(out _));
        Assert.False(store.TryLoad(out _));
        var restarted = new JsonStateStore(paths, logger);
        Assert.False(restarted.TryLoad(out _));
        Assert.False(restarted.Save([]));
        Assert.Equal(invalid, File.ReadAllText(paths.StatePath));
        Assert.Empty(Directory.GetFiles(_root, "*.corrupt-*"));
        File.WriteAllText(paths.StatePath, "[]");
        Assert.True(restarted.TryLoad(out var restored));
        Assert.True(restarted.Save(restored));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ broken")]
    [InlineData("{\"records\":null}")]
    public void InvalidDecryptedContentRemainsUnreadable(string plaintext)
    {
        var paths = new AppPaths(_root);
        File.WriteAllText(paths.StatePath, Protect(plaintext));
        Assert.False(new JsonStateStore(paths, new FileLogger(paths)).TryLoad(out _));
        Assert.True(File.Exists(paths.StatePath));
    }

    [Fact]
    public void NestedEncryptedEnvelopeIsRejected()
    {
        var paths = new AppPaths(_root);
        File.WriteAllText(paths.StatePath, Protect(Protect("[]")));
        Assert.False(new JsonStateStore(paths, new FileLogger(paths)).TryLoad(out _));
    }

    [Theory]
    [InlineData("unknown", true)]
    [InlineData(null, true)]
    [InlineData(null, false)]
    public void ValidDpapiPayloadCannotBypassMissingOrUnknownFormat(string? format, bool includeFormat)
    {
        var paths = new AppPaths(_root);
        var envelope = new Dictionary<string, object?>
        {
            ["version"] = 2,
            ["payload"] = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes("[]"), null, DataProtectionScope.CurrentUser)),
            ["records"] = Array.Empty<CertificateStateRecord>()
        };
        if (includeFormat) envelope["format"] = format;
        var original = JsonSerializer.Serialize(envelope);
        File.WriteAllText(paths.StatePath, original);
        var store = new JsonStateStore(paths, new FileLogger(paths));

        Assert.False(store.TryLoad(out _));
        Assert.False(store.TryLoad(out _));
        Assert.False(store.Save([]));
        Assert.Equal(original, File.ReadAllText(paths.StatePath));
    }

    [Theory]
    [InlineData("{\"format\":\"dpapi-current-user\",\"records\":[]}")]
    [InlineData("{\"format\":null,\"records\":[]}")]
    [InlineData("{\"format\":\"unknown\",\"records\":[]}")]
    [InlineData("{\"payload\":null,\"records\":[]}")]
    [InlineData("{\"payload\":\"\",\"records\":[]}")]
    [InlineData("{\"format\":\"dpapi-current-user\",\"payload\":\"  \",\"records\":[]}")]
    public void IncompleteEncryptedMarkersCannotFallBackToValidLegacyRecords(string original)
    {
        var paths = new AppPaths(_root);
        File.WriteAllText(paths.StatePath, original);
        var store = new JsonStateStore(paths, new FileLogger(paths));

        Assert.False(store.TryLoad(out _));
        Assert.False(store.Save([]));
        Assert.Equal(original, File.ReadAllText(paths.StatePath));
    }

    [Fact]
    public void RepeatedAccessDeniedReadsPreserveBothStoresByteForByte()
    {
        var paths = new AppPaths(_root);
        var logger = new FileLogger(paths);
        File.WriteAllText(paths.SettingsPath, "{\"DailyCheckTime\":\"06:45:00\"}");
        File.WriteAllText(paths.StatePath, "[]");
        var settingsBytes = File.ReadAllBytes(paths.SettingsPath);
        var stateBytes = File.ReadAllBytes(paths.StatePath);
        var hooks = new JsonStoreReadHooks { ReadAllText = _ => throw new UnauthorizedAccessException("denied") };
        var settings = new JsonSettingsStore(paths, logger, hooks);
        var state = new JsonStateStore(paths, logger, hooks);

        Assert.False(settings.TryLoad(out _));
        Assert.False(settings.TryLoad(out _));
        Assert.False(state.TryLoad(out _));
        Assert.False(state.TryLoad(out _));
        Assert.Equal(settingsBytes, File.ReadAllBytes(paths.SettingsPath));
        Assert.Equal(stateBytes, File.ReadAllBytes(paths.StatePath));
        Assert.Empty(Directory.GetFiles(_root, "*.corrupt-*"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("\u200B\u2060\uFEFF")]
    public void SavingEmptyNormalizedThumbprintsOmitsInvalidRecordsFromEncryptedContent(string? invalidThumbprint)
    {
        var paths = new AppPaths(_root);
        var store = new JsonStateStore(paths, new FileLogger(paths));
        Assert.True(store.Save(new Dictionary<string, CertificateStateRecord>
        {
            ["invalid"] = new() { Thumbprint = invalidThumbprint!, State = CertificateNotificationState.Dismissed },
            ["valid"] = new() { Thumbprint = "aa bb", State = CertificateNotificationState.NotifiedUrgent }
        }));

        // Inspecionar o arquivo decriptado evita que o filtro de Load mascare um Save incorreto.
        using var envelope = JsonDocument.Parse(File.ReadAllText(paths.StatePath));
        var payload = envelope.RootElement.EnumerateObject().Single(p => p.Name.Equals("payload", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
        using var content = JsonDocument.Parse(ProtectedData.Unprotect(Convert.FromBase64String(payload), null, DataProtectionScope.CurrentUser));
        var records = content.RootElement.EnumerateObject().Single(p => p.Name.Equals("records", StringComparison.OrdinalIgnoreCase)).Value;
        var record = Assert.Single(records.EnumerateArray());
        Assert.Equal("AABB", record.EnumerateObject().Single(p => p.Name.Equals("thumbprint", StringComparison.OrdinalIgnoreCase)).Value.GetString());
        Assert.Equal((int)CertificateNotificationState.NotifiedUrgent, record.EnumerateObject().Single(p => p.Name.Equals("state", StringComparison.OrdinalIgnoreCase)).Value.GetInt32());
    }

    [Fact]
    public void SaveCannotOverwriteCorruptionEvenWithoutPreviousLoad()
    {
        var paths = new AppPaths(_root);
        var logger = new FileLogger(paths);
        File.WriteAllText(paths.SettingsPath, "{ broken");
        File.WriteAllText(paths.StatePath, "{ broken");
        Assert.False(new JsonSettingsStore(paths, logger).Save(new AppSettings()));
        Assert.False(new JsonStateStore(paths, logger).Save([]));
        Assert.Equal("{ broken", File.ReadAllText(paths.SettingsPath));
        Assert.Equal("{ broken", File.ReadAllText(paths.StatePath));
    }

    private static string Protect(string text) => JsonSerializer.Serialize(new
    {
        version = 2,
        format = "dpapi-current-user",
        payload = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), null, DataProtectionScope.CurrentUser))
    });

    [Fact]
    public void SavedStateCanBeIndependentlyDecryptedWithCurrentUserDpapi()
    {
        var paths = new AppPaths(_root);
        var store = new JsonStateStore(paths, new FileLogger(paths));
        Assert.True(store.Save(new Dictionary<string, CertificateStateRecord>
        {
            ["AABBCC"] = new() { Thumbprint = "AABBCC", NotAfter = new DateTime(2027, 1, 1), State = CertificateNotificationState.Dismissed }
        }));
        var json = File.ReadAllText(paths.StatePath);
        Assert.DoesNotContain("AABBCC", json, StringComparison.OrdinalIgnoreCase);
        using var envelope = JsonDocument.Parse(json);
        var payload = envelope.RootElement.EnumerateObject().Single(p => p.Name.Equals("payload", StringComparison.OrdinalIgnoreCase)).Value.GetString()!;
        var plaintext = ProtectedData.Unprotect(Convert.FromBase64String(payload), null, DataProtectionScope.CurrentUser);
        using var content = JsonDocument.Parse(plaintext);
        var records = content.RootElement.EnumerateObject().Single(p => p.Name.Equals("records", StringComparison.OrdinalIgnoreCase)).Value;
        var record = Assert.Single(records.EnumerateArray());
        Assert.Equal("AABBCC", record.EnumerateObject().Single(p => p.Name.Equals("thumbprint", StringComparison.OrdinalIgnoreCase)).Value.GetString());
        Assert.Equal((int)CertificateNotificationState.Dismissed, record.EnumerateObject().Single(p => p.Name.Equals("state", StringComparison.OrdinalIgnoreCase)).Value.GetInt32());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"settings\":{}}")]
    public void ExplicitEmptySettingsObjectIsValid(string json)
    {
        var paths = new AppPaths(_root);
        File.WriteAllText(paths.SettingsPath, json);
        var store = new JsonSettingsStore(paths, new FileLogger(paths));
        Assert.True(store.TryLoad(out var settings));
        Assert.Equal(TimeSpan.FromHours(9), settings.DailyCheckTime);
        Assert.True(store.Save(settings));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SaveDoesNotOverwriteFileWhenReadingItFails(bool accessDenied)
    {
        var paths = new AppPaths(_root);
        var logger = new FileLogger(paths);
        File.WriteAllText(paths.SettingsPath, "{}");
        File.WriteAllText(paths.StatePath, "[]");
        var hooks = new JsonStoreReadHooks
        {
            ReadAllText = _ => throw (accessDenied ? new UnauthorizedAccessException("denied") : new IOException("locked"))
        };
        Assert.False(new JsonSettingsStore(paths, logger, hooks).Save(new AppSettings()));
        Assert.False(new JsonStateStore(paths, logger, hooks).Save([]));
        Assert.Equal("{}", File.ReadAllText(paths.SettingsPath));
        Assert.Equal("[]", File.ReadAllText(paths.StatePath));
    }

    [Fact]
    public void OversizedFilesStayUnreadableAndCannotBeOverwritten()
    {
        var paths = new AppPaths(_root);
        var logger = new FileLogger(paths);
        // JSON valido: somente o limite de tamanho deve impedir a leitura.
        File.WriteAllText(paths.SettingsPath, "{}" + new string(' ', 1_048_575));
        File.WriteAllText(paths.StatePath, "[]" + new string(' ', 16_777_215));
        var settings = new JsonSettingsStore(paths, logger);
        var state = new JsonStateStore(paths, logger);
        Assert.False(settings.TryLoad(out _));
        Assert.False(settings.TryLoad(out _));
        Assert.False(state.TryLoad(out _));
        Assert.False(state.TryLoad(out _));
        Assert.False(settings.Save(new AppSettings()));
        Assert.False(state.Save([]));
        Assert.Equal(1_048_577, new FileInfo(paths.SettingsPath).Length);
        Assert.Equal(16_777_217, new FileInfo(paths.StatePath).Length);
    }

    [Theory]
    [InlineData(10_485_760, true)]
    [InlineData(10_485_761, false)]
    public void DecryptedPlaintextSizeBoundaryIsEnforcedForValidJson(int plaintextBytes, bool expectedSuccess)
    {
        var paths = new AppPaths(_root);
        var plaintext = "[]" + new string(' ', plaintextBytes - 2);
        var encrypted = Protect(plaintext);
        File.WriteAllText(paths.StatePath, encrypted);
        Assert.True(new FileInfo(paths.StatePath).Length < 16_777_216);
        var store = new JsonStateStore(paths, new FileLogger(paths));

        Assert.Equal(expectedSuccess, store.TryLoad(out var state));
        Assert.Empty(state);
        Assert.Equal(encrypted, File.ReadAllText(paths.StatePath));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }
}
