namespace CertExpiryMonitor.Models;

/// <summary>Disponibilidade independente das fontes exibidas na janela de detalhes.</summary>
public sealed record DetailsLoadResult(
    CertificateReadResult CertificateRead,
    IReadOnlyDictionary<string, CertificateStateRecord> State,
    AppSettings? Settings,
    bool StateAvailable)
{
    public bool SettingsAvailable => Settings is not null;
}
