using System.Text;

namespace CertExpiryMonitor.Services;

internal static class DurableFileWriter
{
    private static readonly UTF8Encoding Utf8WithoutBom = new(encoderShouldEmitUTF8Identifier: false);

    public static void WriteAtomic(string path, string content, bool deleteBackup = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var backupPath = $"{path}.bak";

        try
        {
            WriteThrough(tempPath, content);
            ReplaceWithRetry(tempPath, path, backupPath);
            if (deleteBackup && File.Exists(backupPath))
            {
                File.Delete(backupPath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static void WriteThrough(string path, string content)
    {
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        // Stryker disable all: leaveOpen e necessario para o flush fisico abaixo, mas seu efeito
        // ocorre apenas no dispose ao fim do escopo e nao e distinguivel por teste de mutacao.
        using var writer = new StreamWriter(stream, Utf8WithoutBom, bufferSize: 4096, leaveOpen: true);
        // Stryker restore all
        writer.Write(content);
        writer.Flush();
        // Stryker disable all: Flush(true) e um contrato de durabilidade do SO sem resultado
        // observavel em memoria; ReleaseContractTests protege o argumento exato.
        stream.Flush(flushToDisk: true);
        // Stryker restore all
    }

    private static void ReplaceWithRetry(string tempPath, string path, string backupPath)
    {
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, path);
                }
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(25 * (1 << (attempt - 1)));
            }
        }
    }
}
