using System.Drawing;
using System.Reflection;

namespace CertExpiryMonitor.Services;

/// <summary>
/// Carrega o icone do app (embedded resource) uma unica vez e cacheia.
/// Usado por <see cref="System.Windows.Forms.NotifyIcon"/> e por <c>Form.Icon</c>.
/// Cair em <see cref="SystemIcons.Information"/> e ultimo recurso caso o resource
/// nao seja encontrado (ambiente de testes que so referencia o assembly).
/// </summary>
internal static class AppIcon
{
    private const string ResourceName = "CertExpiryMonitor.assets.CertExpiryMonitor.ico";

    private static Icon? _cached;

    public static Icon Current
    {
        get
        {
            if (_cached is not null) return _cached;

            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using var stream = assembly.GetManifestResourceStream(ResourceName);
                if (stream is not null)
                {
                    _cached = new Icon(stream);
                    return _cached;
                }
            }
            catch
            {
                // fall through para fallback
            }

            _cached = SystemIcons.Information;
            return _cached;
        }
    }
}
