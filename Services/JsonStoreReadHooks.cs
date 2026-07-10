namespace CertExpiryMonitor.Services;

/// <summary>
/// Pontos de injecao usados exclusivamente por testes para reproduzir timeout
/// de lock e falhas transitorias de leitura de forma deterministica.
/// </summary>
internal sealed class JsonStoreReadHooks
{
    public Func<int, bool>? AcquireLock { get; init; }
    public Action? ReleaseLock { get; init; }
    public Func<string, string> ReadAllText { get; init; } = File.ReadAllText;
    public Action<string, string>? WriteAtomically { get; init; }
}
