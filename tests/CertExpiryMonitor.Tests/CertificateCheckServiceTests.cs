using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

/// <summary>
/// Testa as condicoes de skip do CertificateCheckService.RunCheck e a estabilidade
/// de ComputeSnapshotHash. Usa um <see cref="FakeCertificateReader"/> para evitar
/// dependencia do store X.509 real do SO (que seria nao-deterministico em CI).
/// </summary>
public sealed class CertificateCheckServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppPaths _paths;
    private readonly FileLogger _logger;
    private readonly JsonSettingsStore _settingsStore;
    private readonly JsonStateStore _stateStore;
    private readonly FakeCertificateReader _certReader;
    private readonly ExpiryEvaluator _evaluator;
    private readonly CertificateCheckService _service;

    public CertificateCheckServiceTests()
    {
        _tempDir       = Path.Combine(Path.GetTempPath(), $"CertCheckTests_{Guid.NewGuid():N}");
        _paths         = new AppPaths(_tempDir);
        _logger        = new FileLogger(_paths);
        _settingsStore = new JsonSettingsStore(_paths, _logger);
        _stateStore    = new JsonStateStore(_paths, _logger);
        _certReader    = new FakeCertificateReader(_logger);
        _evaluator     = new ExpiryEvaluator();
        _service       = new CertificateCheckService(_settingsStore, _stateStore, _certReader, _evaluator, _logger);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    // -------------------------------------------------------------------------
    // Skip por horario configurado
    // -------------------------------------------------------------------------

    [Fact]
    public void RunCheck_SkipsWhenTimeNotReachedAndFlagIsFalse()
    {
        var settings = new AppSettings
        {
            // 48h e impossivel de atingir (now.TimeOfDay < 24h sempre); evita flakiness
            // quando os testes rodam exatamente as 23:59:xx.
            DailyCheckTime = TimeSpan.FromHours(48)
        };

        var (ran, plan) = _service.RunCheck(
            ignoreConfiguredTime:  false,
            ignoreLastCheckDate:   false,
            forceReminder:         false,
            settings:              settings);

        Assert.False(ran);
        Assert.Null(plan);
    }

    [Fact]
    public void RunCheck_DoesNotSkipWhenIgnoreConfiguredTimeIsTrue()
    {
        var settings = new AppSettings
        {
            DailyCheckTime = TimeSpan.FromHours(48)
        };

        var (ran, _) = _service.RunCheck(
            ignoreConfiguredTime:  true,
            ignoreLastCheckDate:   true,
            forceReminder:         false,
            settings:              settings);

        // O check EXECUTOU (independente de haver plano ou nao)
        Assert.True(ran);
    }

    // -------------------------------------------------------------------------
    // Skip por data + hash identicos
    // -------------------------------------------------------------------------

    [Fact]
    public void RunCheck_SkipsWhenAlreadyCheckedTodayWithSameHash()
    {
        var settings = new AppSettings { DailyCheckTime = TimeSpan.Zero };
        _service.RunCheck(
            ignoreConfiguredTime: true,
            ignoreLastCheckDate:  true,
            forceReminder:        false,
            settings:             settings);
        Assert.Equal(DateOnly.FromDateTime(DateTime.Today), settings.LastCheckDate);
        Assert.NotEmpty(settings.LastCertificateSnapshotHash);

        var (ran, plan) = _service.RunCheck(
            ignoreConfiguredTime: false,
            ignoreLastCheckDate:  false,
            forceReminder:        false,
            settings:             settings);

        Assert.False(ran);
        Assert.Null(plan);
    }

    [Fact]
    public void RunCheck_DoesNotSkipWhenIgnoreLastCheckDateIsTrue()
    {
        var settings = new AppSettings
        {
            DailyCheckTime               = TimeSpan.Zero,
            LastCheckDate                = DateOnly.FromDateTime(DateTime.Today),
            LastCertificateSnapshotHash  = "QUALQUER_HASH_QUALQUER"
        };

        var (ran, _) = _service.RunCheck(
            ignoreConfiguredTime: true,
            ignoreLastCheckDate:  true,
            forceReminder:        false,
            settings:             settings);

        Assert.True(ran);
    }

    // -------------------------------------------------------------------------
    // Snapshot hash invalida skip quando store muda (cenario novo via fake reader)
    // -------------------------------------------------------------------------

    [Fact]
    public void RunCheck_DoesNotSkipWhenSnapshotHashChanged()
    {
        // Primeiro: roda com 1 certificado
        _certReader.Certificates = [Cert("AA", DateTime.Today.AddDays(60))];
        var settings = new AppSettings { DailyCheckTime = TimeSpan.Zero };
        _service.RunCheck(true, true, false, settings);
        var firstHash = settings.LastCertificateSnapshotHash;
        Assert.NotEmpty(firstHash);

        // Segundo: muda o store (adiciona outro certificado) — hash difere, nao deve pular
        _certReader.Certificates = [Cert("AA", DateTime.Today.AddDays(60)), Cert("BB", DateTime.Today.AddDays(30))];
        var (ran, _) = _service.RunCheck(true, false, false, settings);
        Assert.True(ran);
        Assert.NotEqual(firstHash, settings.LastCertificateSnapshotHash);
    }

    [Fact]
    public void RunCheck_ReturnsPlanWithCertificatesInDueRange()
    {
        // Cert vencendo em 5 dias com thresholds padrao (Level7=7) deve cair no bucket Days7.
        _certReader.Certificates = [Cert("CC", DateTime.Today.AddDays(5))];
        var settings = new AppSettings { DailyCheckTime = TimeSpan.Zero };

        var (ran, plan) = _service.RunCheck(true, true, false, settings);

        Assert.True(ran);
        Assert.NotNull(plan);
        Assert.Single(plan!.DueCertificates);
        Assert.Equal("CC", plan.DueCertificates[0].Certificate.Thumbprint);
    }

    [Fact]
    public void RunCheck_ReturnsNullPlanWhenNoCertificatesDue()
    {
        // Cert vencendo em 365 dias — fora de qualquer bucket padrao.
        _certReader.Certificates = [Cert("DD", DateTime.Today.AddDays(365))];
        var settings = new AppSettings { DailyCheckTime = TimeSpan.Zero };

        var (ran, plan) = _service.RunCheck(true, true, false, settings);

        Assert.True(ran);
        Assert.Null(plan);  // Ran=true mas Plan=null porque HasItems=false
    }

    // -------------------------------------------------------------------------
    // Atualizacao de settings apos execucao
    // -------------------------------------------------------------------------

    [Fact]
    public void RunCheck_SetsLastCheckDateToTodayAfterRunning()
    {
        var settings = new AppSettings { DailyCheckTime = TimeSpan.Zero };

        _service.RunCheck(true, true, false, settings);

        Assert.Equal(DateOnly.FromDateTime(DateTime.Today), settings.LastCheckDate);
    }

    [Fact]
    public void RunCheck_SetsSnapshotHashAfterRunning()
    {
        _certReader.Certificates = [Cert("EE", DateTime.Today.AddDays(30))];
        var settings = new AppSettings { DailyCheckTime = TimeSpan.Zero };

        _service.RunCheck(true, true, false, settings);

        Assert.NotEmpty(settings.LastCertificateSnapshotHash);
    }

    // -------------------------------------------------------------------------
    // Null guards (regressao protetiva apos ArgumentNullException.ThrowIfNull)
    // -------------------------------------------------------------------------

    [Fact]
    public void RunCheck_ThrowsOnNullSettings()
    {
        Assert.Throws<ArgumentNullException>(() =>
            _service.RunCheck(true, true, false, settings: null!));
    }

    [Fact]
    public void ComputeSnapshotHash_ThrowsOnNullList()
    {
        Assert.Throws<ArgumentNullException>(() =>
            CertificateCheckService.ComputeSnapshotHash(certificates: null!));
    }

    // -------------------------------------------------------------------------
    // Race condition no _isChecking — duas threads disparam RunCheck simultaneamente
    // -------------------------------------------------------------------------

    [Fact]
    public async Task RunCheck_IsCheckingGuardSerializesParallelCalls()
    {
        // Simula concorrencia entre timer thread e UI thread. Apenas uma deve passar
        // pelo guard; a outra retorna (false, null) imediatamente.
        _certReader.Certificates = [Cert("FF", DateTime.Today.AddDays(30))];

        // Bloqueia o fake reader ate ambos os RunCheck terem entrado, garantindo
        // que ambos disputem o guard simultaneamente.
        using var gate    = new ManualResetEventSlim(false);
        using var entered = new CountdownEvent(2);
        _certReader.OnRead = () =>
        {
            entered.Signal();
            gate.Wait(TimeSpan.FromSeconds(5));
        };

        var settings1 = new AppSettings { DailyCheckTime = TimeSpan.Zero };
        var settings2 = new AppSettings { DailyCheckTime = TimeSpan.Zero };

        var task1 = Task.Run(() => _service.RunCheck(true, true, false, settings1));
        var task2 = Task.Run(() => _service.RunCheck(true, true, false, settings2));

        // Espera ate 2s pelos dois entrarem no reader; se o guard ja bloqueou, OK.
        entered.Wait(TimeSpan.FromSeconds(2));
        gate.Set();

        var (ran1, _) = await task1;
        var (ran2, _) = await task2;

        // Exatamente um dos dois rodou; o outro foi bloqueado pelo Interlocked guard.
        Assert.True(ran1 ^ ran2, $"Esperado exatamente 1 ran=true, obtido ran1={ran1} ran2={ran2}");
    }

    // -------------------------------------------------------------------------
    // ComputeSnapshotHash — estabilidade
    // -------------------------------------------------------------------------

    [Fact]
    public void ComputeSnapshotHash_EmptyListProducesConsistentHash()
    {
        var hash1 = CertificateCheckService.ComputeSnapshotHash([]);
        var hash2 = CertificateCheckService.ComputeSnapshotHash([]);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeSnapshotHash_OrderDoesNotAffectResult()
    {
        var a = Cert("AA", DateTime.Today.AddDays(30));
        var b = Cert("BB", DateTime.Today.AddDays(60));

        var hash1 = CertificateCheckService.ComputeSnapshotHash([a, b]);
        var hash2 = CertificateCheckService.ComputeSnapshotHash([b, a]);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeSnapshotHash_DifferentCertificatesProduceDifferentHashes()
    {
        var a = Cert("AA", DateTime.Today.AddDays(30));
        var b = Cert("BB", DateTime.Today.AddDays(30));

        var hash1 = CertificateCheckService.ComputeSnapshotHash([a]);
        var hash2 = CertificateCheckService.ComputeSnapshotHash([b]);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeSnapshotHash_ThumbprintIsCaseInsensitive()
    {
        var lower = Cert("aabbcc", DateTime.Today.AddDays(30));
        var upper = Cert("AABBCC", DateTime.Today.AddDays(30));

        var hash1 = CertificateCheckService.ComputeSnapshotHash([lower]);
        var hash2 = CertificateCheckService.ComputeSnapshotHash([upper]);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeSnapshotHash_ChangeInNotAfterChangesHash()
    {
        var a = Cert("AA", DateTime.Today.AddDays(30));
        var b = Cert("AA", DateTime.Today.AddDays(31));

        var hash1 = CertificateCheckService.ComputeSnapshotHash([a]);
        var hash2 = CertificateCheckService.ComputeSnapshotHash([b]);

        Assert.NotEqual(hash1, hash2);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static CertificateSnapshot Cert(string thumbprint, DateTime notAfter) =>
        new(thumbprint, $"CN={thumbprint}", "CN=Test Issuer", notAfter, "01");

    /// <summary>
    /// Subclasse de teste que substitui leitura do store X.509 do SO por uma lista
    /// configuravel. Permite testes deterministicos do <see cref="CertificateCheckService"/>.
    /// </summary>
    private sealed class FakeCertificateReader : CertificateReader
    {
        public IReadOnlyList<CertificateSnapshot> Certificates { get; set; } = [];
        public Action? OnRead { get; set; }

        public FakeCertificateReader(FileLogger logger) : base(logger) { }

        public override IReadOnlyList<CertificateSnapshot> ReadCurrentUserPersonalCertificates()
        {
            OnRead?.Invoke();
            return Certificates;
        }
    }
}
