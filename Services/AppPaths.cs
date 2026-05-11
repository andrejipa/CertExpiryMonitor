namespace CertExpiryMonitor.Services;

public sealed class AppPaths
{
    /// <param name="rootOverride">
    ///   Raiz alternativa para o diretorio de dados. Quando <c>null</c> usa o padrao
    ///   <c>%LOCALAPPDATA%\CertExpiryMonitor</c>. Util em testes automatizados.
    /// </param>
    public AppPaths(string? rootOverride = null)
    {
        var root = rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CertExpiryMonitor");

        Directory.CreateDirectory(root);
        RootDirectory = root;
        SettingsPath = Path.Combine(root, "settings.json");
        StatePath = Path.Combine(root, "certificate-state.json");
        LogPath = Path.Combine(root, "monitor.log");
    }

    public string RootDirectory { get; }
    public string SettingsPath { get; }
    public string StatePath { get; }
    public string LogPath { get; }
}
