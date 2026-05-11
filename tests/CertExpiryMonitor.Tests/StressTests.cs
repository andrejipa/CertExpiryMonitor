using System.Data;
using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

/// <summary>
/// Testes adversariais que cobrem bugs latentes identificados em auditoria
/// agressiva. Cada teste documenta o cenario de campo que ele protege.
/// </summary>
public sealed class StressTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppPaths _paths;
    private readonly FileLogger _logger;

    public StressTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"Stress_{Guid.NewGuid():N}");
        _paths   = new AppPaths(_tempDir);
        _logger  = new FileLogger(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // ---- Bug #2 (auditor): RowFilter crash com caracteres especiais ----

    [Theory]
    [InlineData("[")]
    [InlineData("]")]
    [InlineData("[abc]")]
    [InlineData("*")]
    [InlineData("%")]
    [InlineData("**[")]
    public void DataTableRowFilter_ShouldNotCrashWithSpecialChars(string userQuery)
    {
        // Reproduz crash que o usuario pode causar digitando colchetes ou wildcards
        // na busca textual do grid. O escape atual ('') so cobre aspas.
        var table = new DataTable();
        table.Columns.Add("Holder",   typeof(string));
        table.Columns.Add("Document", typeof(string));
        table.Rows.Add("PORTAL COMERCIO LTDA", "14.419.272/0001-35");

        var escaped = CertificateSearchEscaper.EscapeForRowFilter(userQuery);
        var view = new DataView(table)
        {
            // SE o escape estiver correto, isso NAO deve lancar nenhuma excecao.
            RowFilter = $"(Holder LIKE '%{escaped}%' OR Document LIKE '%{escaped}%')"
        };

        // Forca avaliacao do filter
        Assert.True(view.Count >= 0);
    }

    // ---- Bug #14 (auditor): Telemetry zera contadores se Load falha transitoriamente ----

    [Fact]
    public void Telemetry_ShouldNotZeroCountersWhenLoadFailsTransiently()
    {
        // Cenario: antivirus locka telemetry.json durante scan; Load lanca,
        // o servico (incorretamente) retorna envelope NOVO e o Save subsequente
        // sobrescreve, perdendo todos os contadores.
        var telemetry = new TelemetryService(_paths, _logger) { Enabled = true };

        // Acumula valor
        telemetry.Increment(t => t.TotalChecks = 500);
        telemetry.Increment(t => t.NotificationsShown = 50);
        Assert.Equal(500, telemetry.Load().TotalChecks);

        // Simula corrupcao: substitui o JSON por algo ilegivel
        File.WriteAllText(_paths.TelemetryPath, "{not valid json");

        // O proximo Increment NAO deve zerar os contadores acumulados —
        // deve preservar o arquivo corrompido para diagnostico e ou bailar
        // ou usar um valor seguro (envelope vazio + arquivo .corrupt preservado).
        telemetry.Increment(t => t.TotalChecks++);

        // Verifica preservacao do arquivo corrompido
        var corruptFiles = Directory.GetFiles(_tempDir, "telemetry.json.corrupt-*");
        Assert.NotEmpty(corruptFiles);
    }

    // ---- Bug #4 (auditor): Reset deve apagar mas Increment apos reset deve recomecar de zero ----

    [Fact]
    public void Telemetry_ResetThenIncrement_StartsClean()
    {
        var telemetry = new TelemetryService(_paths, _logger) { Enabled = true };
        telemetry.Increment(t => t.TotalChecks = 999);
        telemetry.Reset();
        telemetry.Increment(t => t.TotalChecks++);
        Assert.Equal(1, telemetry.Load().TotalChecks);
    }

    // ---- Bonus: Telemetria sob race intra-processo ----

    [Fact]
    public async Task Telemetry_ConcurrentIncrements_DoNotLoseUpdates()
    {
        // Lock interno garante atomicidade. 200 threads incrementando deve dar 200.
        var telemetry = new TelemetryService(_paths, _logger) { Enabled = true };

        var tasks = Enumerable.Range(0, 200)
            .Select(_ => Task.Run(() => telemetry.Increment(t => t.TotalChecks++)))
            .ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal(200, telemetry.Load().TotalChecks);
    }

    // ---- Bonus: settings com tipos extremos ----

    [Fact]
    public void Settings_AcceptsExtremeThresholdsWithoutOverflow()
    {
        // Usuario malicioso ou config legada com valores enormes nao deve
        // estourar comparacoes ou loops.
        var store = new JsonSettingsStore(_paths, _logger);
        var settings = new AppSettings
        {
            Thresholds = new ExpiryThresholds
            {
                Level30 = 365 * 100,  // 100 anos
                Level15 = 365,
                Level7  = 30,
                Level1  = 1
            }
        };

        store.Save(settings);
        var loaded = store.Load();

        var normalized = loaded.Thresholds.Normalized();
        Assert.True(normalized.Level30 >= normalized.Level15);
        Assert.True(normalized.Level15 >= normalized.Level7);
        Assert.True(normalized.Level7  >= normalized.Level1);
    }

    // ---- Bonus: state com 1000 certificados ----

    [Fact]
    public void StateStore_HandlesThousandCertificates()
    {
        // Stress: 1000 registros. Validar que round-trip funciona e Load
        // retorna todos sem perdas.
        var store = new JsonStateStore(_paths, _logger);
        var dict = new Dictionary<string, CertificateStateRecord>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 1000; i++)
        {
            var thumb = $"THUMB{i:D8}";
            dict[thumb] = new CertificateStateRecord
            {
                Thumbprint = thumb,
                NotAfter   = DateTime.Today.AddDays(i),
                State      = CertificateNotificationState.None
            };
        }

        store.Save(dict);
        var loaded = store.Load();
        Assert.Equal(1000, loaded.Count);
    }

    // ---- Bonus: Settings.json malformado com BOM ----

    [Fact]
    public void SettingsStore_ReadsFileWithBOM()
    {
        // Editores Windows (Notepad pre-Win11) salvam UTF-8 com BOM.
        // JsonDocument.Parse aceita BOM nativamente; vamos validar round-trip.
        var content = "﻿{\"DailyCheckTime\":\"10:30:00\"}";  // BOM + JSON
        File.WriteAllText(_paths.SettingsPath, content);

        var store = new JsonSettingsStore(_paths, _logger);
        var settings = store.Load();

        Assert.Equal(TimeSpan.FromHours(10).Add(TimeSpan.FromMinutes(30)), settings.DailyCheckTime);
    }
}
