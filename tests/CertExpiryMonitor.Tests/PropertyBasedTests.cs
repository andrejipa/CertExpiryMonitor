using System.Xml.Linq;
using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using FsCheck.Xunit;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class PropertyBasedTests
{
    [Property(MaxTest = 100)]
    public void ParseArgumentsNeverThrowsForArbitraryInput(string arguments)
    {
        var exception = Record.Exception(() => TrayApplicationContext.ParseArguments(arguments));

        Assert.Null(exception);
    }

    [Property(MaxTest = 100)]
    public void ParseArgumentsKeepsLastDuplicateValueAndIgnoresEmptyKey(string keySeed, string firstSeed, string lastSeed)
    {
        var key = MakeKey(keySeed);
        var first = MakeValue(firstSeed);
        var last = MakeValue(lastSeed);
        var arguments =
            $"{key}={Uri.EscapeDataString(first)}&ignored&=empty&{key}={Uri.EscapeDataString(last)}";

        var parsed = TrayApplicationContext.ParseArguments(arguments);

        Assert.Equal(last, parsed[key]);
        Assert.False(parsed.ContainsKey(string.Empty));
    }

    [Property(MaxTest = 100)]
    public void NormalizedThresholdsAreAlwaysStrictlyOrdered(int level1, int level7, int level15, int level30)
    {
        var normalized = new ExpiryThresholds
        {
            Level1 = level1,
            Level7 = level7,
            Level15 = level15,
            Level30 = level30
        }.Normalized();

        Assert.InRange(normalized.Level1, 1, ExpiryThresholds.MaximumDays - 3);
        Assert.True(normalized.Level7 > normalized.Level1);
        Assert.True(normalized.Level15 > normalized.Level7);
        Assert.True(normalized.Level30 > normalized.Level15);
        Assert.InRange(normalized.Level30, 4, ExpiryThresholds.MaximumDays);
    }

    [Property(MaxTest = 50)]
    public void JsonSettingsStoreRoundTripsSafeArbitrarySettings(
        int minutesSeed,
        int initialDelaySeed,
        int level1,
        int level7,
        int level15,
        int level30,
        bool startupEnabled,
        bool notificationSoundEnabled,
        bool eventLogEnabled,
        bool telemetryEnabled)
    {
        using var fixture = new StoreFixture("SettingsProperty");
        var thresholds = new ExpiryThresholds
        {
            Level1 = level1,
            Level7 = level7,
            Level15 = level15,
            Level30 = level30
        }.Normalized();
        var settings = new AppSettings
        {
            DailyCheckTime = TimeSpan.FromMinutes(Mod(minutesSeed, 24 * 60)),
            InitialDelayMinutes = Mod(initialDelaySeed, 180),
            StartupEnabled = startupEnabled,
            NotificationSoundEnabled = notificationSoundEnabled,
            EventLogEnabled = eventLogEnabled,
            TelemetryEnabled = telemetryEnabled,
            LogFormat = eventLogEnabled ? LogFormat.Json : LogFormat.Text,
            Thresholds = thresholds
        };

        fixture.SettingsStore.Save(settings);
        var loaded = fixture.SettingsStore.Load();

        Assert.Equal(settings.DailyCheckTime, loaded.DailyCheckTime);
        Assert.Equal(settings.InitialDelayMinutes, loaded.InitialDelayMinutes);
        Assert.Equal(settings.StartupEnabled, loaded.StartupEnabled);
        Assert.Equal(settings.NotificationSoundEnabled, loaded.NotificationSoundEnabled);
        Assert.Equal(settings.EventLogEnabled, loaded.EventLogEnabled);
        Assert.Equal(settings.TelemetryEnabled, loaded.TelemetryEnabled);
        Assert.Equal(settings.LogFormat, loaded.LogFormat);
        Assert.Equal(thresholds.Level1, loaded.Thresholds.Level1);
        Assert.Equal(thresholds.Level7, loaded.Thresholds.Level7);
        Assert.Equal(thresholds.Level15, loaded.Thresholds.Level15);
        Assert.Equal(thresholds.Level30, loaded.Thresholds.Level30);
    }

    [Property(MaxTest = 50)]
    public void JsonStateStoreRoundTripsSafeArbitraryRecords(string thumbprintSeed, int daysSeed, int stateSeed)
    {
        using var fixture = new StoreFixture("StateProperty");
        var thumbprint = MakeThumbprint(thumbprintSeed);
        var state = PickState(stateSeed);
        var notAfter = new DateTime(2026, 1, 1).AddDays(Mod(daysSeed, 3650));
        var records = new Dictionary<string, CertificateStateRecord>(StringComparer.OrdinalIgnoreCase)
        {
            [thumbprint] = new CertificateStateRecord
            {
                Thumbprint = thumbprint,
                NotAfter = notAfter,
                State = state,
                LastNotifiedAt = DateTimeOffset.UtcNow.AddMinutes(-Mod(daysSeed, 1440))
            }
        };

        fixture.StateStore.Save(records);
        var loaded = fixture.StateStore.Load();

        var normalized = CertificateIdentity.NormalizeThumbprint(thumbprint);
        Assert.True(loaded.ContainsKey(normalized));
        Assert.Equal(notAfter, loaded[normalized].NotAfter);
        Assert.Equal(state, loaded[normalized].State);
    }

    [Property(MaxTest = 100)]
    public void CertificateDocumentHelpersNeverThrowForBmpInput(string commonNameSeed, string documentSeed)
    {
        var commonName = MakeBmpString(commonNameSeed);
        var document = MakeBmpString(documentSeed);

        var exception = Record.Exception(() =>
        {
            _ = CertificateDocumentHelpers.GetCommonNameFallback(commonName);
            _ = CertificateDocumentHelpers.ParseHolder(commonName);
            _ = CertificateDocumentHelpers.FormatDocument(document);
        });

        Assert.Null(exception);
    }

    [Property(MaxTest = 100)]
    public void ToastXmlIsParseableAndKeepsActionLimit(string thumbprintSeed, int level1, int level7, int level15, int level30)
    {
        var thumbprint = MakeValue(thumbprintSeed);
        var thresholds = new ExpiryThresholds
        {
            Level1 = level1,
            Level7 = level7,
            Level15 = level15,
            Level30 = level30
        }.Normalized();
        var plan = new NotificationPlan
        {
            DueCertificates =
            [
                new CertificateDueNotification(
                    new CertificateSnapshot(thumbprint, "CN=Codex", "CN=Issuer", DateTime.Today.AddDays(5), "01"),
                    ExpiryBucket.Days7,
                    5)
            ]
        };

        var xml = ToastNotifierService.BuildToastXml(plan, thresholds);
        var document = XDocument.Parse(xml);
        var actions = document.Descendants("action").ToArray();

        Assert.Equal("toast", document.Root?.Name.LocalName);
        Assert.InRange(actions.Length, 0, 5);
        Assert.Equal(ToastNotifierService.DetailsProtocolUri, document.Root?.Attribute("launch")?.Value);
        Assert.Equal("protocol", document.Root?.Attribute("activationType")?.Value);
    }

    private static string MakeKey(string? seed)
    {
        var chars = (seed ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Take(24)
            .ToArray();

        return chars.Length == 0 ? "key" : new string(chars);
    }

    private static string MakeValue(string? seed)
    {
        var value = MakeBmpString(seed);
        return value.Length == 0 ? "value" : value;
    }

    private static string MakeThumbprint(string? seed)
    {
        var hex = new string((seed ?? string.Empty)
            .Where(Uri.IsHexDigit)
            .Take(40)
            .ToArray());

        return hex.Length == 0 ? "AABBCC" : hex;
    }

    private static string MakeBmpString(string? seed)
    {
        var chars = (seed ?? string.Empty)
            .Where(c => !char.IsSurrogate(c) && c <= '\uFFFF')
            .Take(128)
            .ToArray();

        return new string(chars);
    }

    private static int Mod(int value, int modulo)
    {
        var result = value % modulo;
        return result < 0 ? result + modulo : result;
    }

    private static CertificateNotificationState PickState(int seed)
    {
        var values = Enum.GetValues<CertificateNotificationState>();
        return values[Mod(seed, values.Length)];
    }

    private sealed class StoreFixture : IDisposable
    {
        private readonly string _tempDir;

        public StoreFixture(string prefix)
        {
            _tempDir = Path.Combine(Path.GetTempPath(), $"{prefix}_{Guid.NewGuid():N}");
            Paths = new AppPaths(_tempDir);
            Logger = new FileLogger(Paths);
            SettingsStore = new JsonSettingsStore(Paths, Logger);
            StateStore = new JsonStateStore(Paths, Logger);
        }

        public AppPaths Paths { get; }
        public FileLogger Logger { get; }
        public JsonSettingsStore SettingsStore { get; }
        public JsonStateStore StateStore { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_tempDir, recursive: true);
            }
            catch
            {
                // best-effort cleanup em diretorio temporario de teste
            }
        }
    }
}
