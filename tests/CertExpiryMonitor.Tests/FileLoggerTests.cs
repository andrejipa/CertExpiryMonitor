using System.Text.Json;
using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class FileLoggerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AppPaths _paths;
    private readonly FileLogger _logger;

    public FileLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"FileLoggerTests_{Guid.NewGuid():N}");
        _paths = new AppPaths(_tempDir);
        _logger = new FileLogger(_paths);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void JsonFormatWritesValidJsonLineWithExceptionDetails()
    {
        _logger.ApplySettings(new AppSettings { LogFormat = LogFormat.Json });

        try
        {
            ThrowForStackTrace();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Falha simulada");
        }

        var line = Assert.Single(File.ReadAllLines(_paths.LogPath));
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;

        Assert.Equal("ERROR", root.GetProperty("level").GetString());
        Assert.Equal("Falha simulada", root.GetProperty("message").GetString());
        Assert.Equal(typeof(InvalidOperationException).FullName, root.GetProperty("exceptionType").GetString());
        Assert.Contains(nameof(ThrowForStackTrace), root.GetProperty("stackTrace").GetString(), StringComparison.Ordinal);
        Assert.Contains("inner de teste", root.GetProperty("exception").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void LogOutputRedactsSensitiveTextBeforePersisting()
    {
        _logger.ApplySettings(new AppSettings { LogFormat = LogFormat.Json });

        var sensitiveMessage =
            "Failed to dismiss certificate AABBCCDD thumb=AABBCCDDEEFF0011223344556677889900AABBCC senha=segredo pasta=C:\\Users\\Fulano\\AppData\\Local\\CertExpiryMonitor pfx=C:\\Users\\Fulano\\certificado.pfx";
        var exception = new InvalidOperationException("cpf=12345678901 caminho=C:\\Users\\Fulano\\AppData\\Local\\CertExpiryMonitor\\logs");

        _logger.Error(exception, sensitiveMessage);

        var line = Assert.Single(File.ReadAllLines(_paths.LogPath));
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var text =
            $"{root.GetProperty("message").GetString()}\n" +
            $"{root.GetProperty("exceptionMessage").GetString()}\n" +
            $"{root.GetProperty("exception").GetString()}";

        Assert.Contains("certificate (redacted-thumbprint-prefix)", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("thumb=(redacted-thumbprint)", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("senha=(redacted)", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("document_last4:8901", text, StringComparison.Ordinal);
        Assert.Contains("C:\\Users\\(redacted)", text, StringComparison.Ordinal);
        Assert.Contains("pfx=(redacted)", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AABBCCDD", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AABBCCDDEEFF0011223344556677889900AABBCC", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("segredo", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Fulano", text, StringComparison.Ordinal);
        Assert.DoesNotContain("12345678901", text, StringComparison.Ordinal);
        Assert.DoesNotContain("certificado.pfx", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RotationPreservesOldFileAndWritesNewLine()
    {
        File.WriteAllText(_paths.LogPath, new string('A', 1_048_577));

        _logger.Info("linha apos rotacao");

        Assert.True(File.Exists(Path.Combine(_paths.RootDirectory, "monitor.log.1")));
        Assert.Contains("linha apos rotacao", File.ReadAllText(_paths.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public void InfoRecreatesMissingDataDirectory()
    {
        Directory.Delete(_paths.RootDirectory, recursive: true);

        _logger.Info("linha apos pasta removida");

        Assert.True(File.Exists(_paths.LogPath));
        Assert.Contains("linha apos pasta removida", File.ReadAllText(_paths.LogPath), StringComparison.Ordinal);
    }

    [Fact]
    public void ApplySettingsUpdatesRuntimeFormat()
    {
        _logger.ApplySettings(new AppSettings { LogFormat = LogFormat.Json, EventLogEnabled = true });

        Assert.Equal(LogFormat.Json, _logger.Format);
        Assert.True(_logger.EventLogEnabled);
    }

    private static void ThrowForStackTrace()
    {
        throw new InvalidOperationException("erro de teste", new ApplicationException("inner de teste"));
    }
}
