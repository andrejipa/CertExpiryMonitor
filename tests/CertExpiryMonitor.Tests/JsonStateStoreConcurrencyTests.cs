using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

/// <summary>
/// Valida o mutex global do <see cref="JsonStateStore"/> (Local\CertExpiryMonitor.StateJson)
/// sob acesso concorrente. Estes testes intencionalmente disparam multiplas threads
/// chamando Save/Load simultaneamente — sem o mutex, esperaria-se exceções de IO
/// ou JSON corrompido.
/// </summary>
public sealed class JsonStateStoreConcurrencyTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppPaths _paths;
    private readonly FileLogger _logger;
    private readonly JsonStateStore _store;

    public JsonStateStoreConcurrencyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"CertStoreConcurrency_{Guid.NewGuid():N}");
        _paths   = new AppPaths(_tempDir);
        _logger  = new FileLogger(_paths);
        _store   = new JsonStateStore(_paths, _logger);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void ParallelSaves_DoNotCorruptStateFile()
    {
        // 8 threads, cada uma fazendo Save() com dados ligeiramente diferentes.
        // O mutex deve serializar as escritas — o load final precisa retornar
        // dados validos (mesmo que sejam de uma unica thread).
        Parallel.For(0, 8, threadIndex =>
        {
            var state = new Dictionary<string, CertificateStateRecord>(StringComparer.OrdinalIgnoreCase)
            {
                [$"THUMBPRINT-{threadIndex:D2}"] = new CertificateStateRecord
                {
                    Thumbprint = $"THUMBPRINT-{threadIndex:D2}",
                    NotAfter   = DateTime.Today.AddDays(30 + threadIndex),
                    State      = CertificateNotificationState.Notified30
                }
            };

            // Cada thread roda Save varias vezes para aumentar a chance de race.
            for (var i = 0; i < 5; i++)
            {
                _store.Save(state);
            }
        });

        // O arquivo deve ser legivel sem excecao e conter exatamente 1 registro
        // (cada Save substitui o estado anterior — esta e a semantica do store).
        var loaded = _store.Load();
        Assert.Single(loaded);

        // O registro carregado deve ser um dos que foram salvos (thumbprint comeca com "THUMBPRINT-")
        var entry = loaded.Values.First();
        Assert.StartsWith("THUMBPRINT-", entry.Thumbprint, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ParallelSaveAndLoad_DoNotThrowExceptions()
    {
        // Uma thread saves continuamente; outra loads continuamente.
        // Sem o mutex, esperaria-se IOException ou JsonException intermitentes.
        var stopAt = DateTime.UtcNow.AddSeconds(2);
        var saveCount = 0;
        var loadCount = 0;
        Exception? savedException = null;
        Exception? loadedException = null;

        var saver = Task.Run(() =>
        {
            try
            {
                while (DateTime.UtcNow < stopAt)
                {
                    var state = new Dictionary<string, CertificateStateRecord>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["AA"] = new CertificateStateRecord
                        {
                            Thumbprint = "AA",
                            NotAfter   = DateTime.Today.AddDays(30),
                            State      = CertificateNotificationState.Notified30
                        }
                    };
                    _store.Save(state);
                    Interlocked.Increment(ref saveCount);
                }
            }
            catch (Exception ex) { savedException = ex; }
        });

        var loader = Task.Run(() =>
        {
            try
            {
                while (DateTime.UtcNow < stopAt)
                {
                    _store.Load();
                    Interlocked.Increment(ref loadCount);
                }
            }
            catch (Exception ex) { loadedException = ex; }
        });

        await Task.WhenAll(saver, loader);

        Assert.Null(savedException);
        Assert.Null(loadedException);
        Assert.True(saveCount  > 0, "Saver thread deveria ter executado ao menos 1 save");
        Assert.True(loadCount  > 0, "Loader thread deveria ter executado ao menos 1 load");
    }

    [Fact]
    public void ConcurrentSavesFromDifferentStores_OnSamePath_DoNotCorrupt()
    {
        // Cenario realista: app principal + verificacao manual no mesmo processo
        // (mesma instancia de JsonStateStore tipicamente, mas podem coexistir
        // multiplas instancias se a composicao mudar no futuro).
        var store2 = new JsonStateStore(_paths, _logger);

        Parallel.For(0, 16, i =>
        {
            var store = (i % 2 == 0) ? _store : store2;
            var state = new Dictionary<string, CertificateStateRecord>(StringComparer.OrdinalIgnoreCase)
            {
                [$"TP-{i:D2}"] = new CertificateStateRecord
                {
                    Thumbprint = $"TP-{i:D2}",
                    NotAfter   = DateTime.Today.AddDays(30),
                    State      = CertificateNotificationState.None
                }
            };
            store.Save(state);
        });

        // Apos a tempestade, qualquer Load deve voltar dados validos.
        var loaded = _store.Load();
        Assert.Single(loaded);
    }
}
