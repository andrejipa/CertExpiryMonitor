using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class DurableFileWriterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"DurableWriter_{Guid.NewGuid():N}");

    [Fact]
    public void FirstWriteCreatesUtf8FileWithoutTemporaryArtifacts()
    {
        var path = Path.Combine(_tempDir, "data.json");

        DurableFileWriter.WriteAtomic(path, "{\"texto\":\"ação\"}");

        Assert.Equal("{\"texto\":\"ação\"}", File.ReadAllText(path));
        Assert.False(File.ReadAllBytes(path).AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp"));
        Assert.False(File.Exists($"{path}.bak"));
    }

    [Fact]
    public void ReplacementKeepsPreviousContentAsBackup()
    {
        var path = Path.Combine(_tempDir, "data.json");
        DurableFileWriter.WriteAtomic(path, "primeiro");

        DurableFileWriter.WriteAtomic(path, "segundo");

        Assert.Equal("segundo", File.ReadAllText(path));
        Assert.Equal("primeiro", File.ReadAllText($"{path}.bak"));
        Assert.Empty(Directory.GetFiles(_tempDir, "*.tmp"));
    }

    [Fact]
    public void DeleteBackupRemovesBackupAfterSuccessfulReplacement()
    {
        var path = Path.Combine(_tempDir, "data.json");
        DurableFileWriter.WriteAtomic(path, "primeiro");

        DurableFileWriter.WriteAtomic(path, "segundo", deleteBackup: true);

        Assert.Equal("segundo", File.ReadAllText(path));
        Assert.False(File.Exists($"{path}.bak"));
    }

    [Fact]
    public void InvalidArgumentsAreRejectedBeforeFilesystemMutation()
    {
        Assert.Throws<ArgumentException>(() => DurableFileWriter.WriteAtomic(" ", "value"));
        Assert.Throws<ArgumentNullException>(() => DurableFileWriter.WriteAtomic(Path.Combine(_tempDir, "x"), null!));
        Assert.False(Directory.Exists(_tempDir));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
