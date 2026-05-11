using System.Drawing;
using System.Windows.Forms;

namespace CertExpiryMonitor.Services;

/// <summary>
/// Janela de diagnostico de inicializacao automatica. Mostra ao usuario:
/// - Se Task Scheduler tem a tarefa registrada;
/// - Se HKCU\Run tem a entrada;
/// - Caminho do executavel resolvido;
/// - Comando exato que sera executado no logon.
///
/// Botoes: "Tentar registrar de novo" (chama EnsureRegistered), "Fechar".
///
/// Util para troubleshooting em campo quando o app nao abre sozinho apos reboot.
/// </summary>
public sealed class StartupDiagnosticsWindow : Form
{
    private readonly StartupRegistration _startup;
    private readonly FileLogger _logger;
    private Label _statusLabel = null!;
    private TextBox _detailsBox = null!;

    public StartupDiagnosticsWindow(StartupRegistration startup, FileLogger logger)
    {
        ArgumentNullException.ThrowIfNull(startup);
        ArgumentNullException.ThrowIfNull(logger);
        _startup = startup;
        _logger  = logger;

        Text            = "Diagnóstico de inicialização automática";
        StartPosition   = FormStartPosition.CenterScreen;
        ClientSize      = new Size(620, 460);
        MinimumSize     = new Size(540, 380);
        Icon            = AppIcon.Current;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox     = false;
        MinimizeBox     = false;

        BuildUi();
        Refresh_();
    }

    private void BuildUi()
    {
        var titleLabel = new Label
        {
            Text      = "Inicialização automática com o Windows",
            Font      = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? SystemFonts.DefaultFont.FontFamily, 12F, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 76, 129),
            AutoSize  = true,
            Location  = new Point(16, 16)
        };

        var description = new Label
        {
            Text     = "Verifica se o app está configurado para abrir sozinho quando você fizer login no Windows.\r\nO registro tenta primeiro o Task Scheduler (ONLOGON, sem elevação). Se falhar, usa HKCU\\Run.",
            AutoSize = false,
            Location = new Point(16, 50),
            Size     = new Size(588, 40),
            ForeColor = Color.FromArgb(80, 80, 80)
        };

        _statusLabel = new Label
        {
            AutoSize  = false,
            Location  = new Point(16, 96),
            Size      = new Size(588, 30),
            Font      = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? SystemFonts.DefaultFont.FontFamily, 10F, FontStyle.Bold)
        };

        _detailsBox = new TextBox
        {
            Location   = new Point(16, 130),
            Size       = new Size(588, 240),
            Multiline  = true,
            ReadOnly   = true,
            ScrollBars = ScrollBars.Vertical,
            Font       = new Font(FontFamily.GenericMonospace, 9F),
            BackColor  = Color.FromArgb(248, 249, 251),
            Anchor     = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom
        };

        var refreshBtn = new Button
        {
            Text     = "Atualizar",
            Location = new Point(16, 388),
            Size     = new Size(110, 32),
            Anchor   = AnchorStyles.Bottom | AnchorStyles.Left
        };
        refreshBtn.Click += (_, _) => Refresh_();

        var registerBtn = new Button
        {
            Text     = "Tentar registrar de novo",
            Location = new Point(140, 388),
            Size     = new Size(180, 32),
            Anchor   = AnchorStyles.Bottom | AnchorStyles.Left
        };
        registerBtn.Click += (_, _) =>
        {
            // Disable durante operacao: EnsureRegistered chama schtasks sincrono
            // (WaitForExit 10s). 10 cliques seguidos = 100s travado na UI thread.
            registerBtn.Enabled  = false;
            var originalText     = registerBtn.Text;
            registerBtn.Text     = "Registrando...";
            UseWaitCursor        = true;
            try
            {
                _startup.EnsureRegistered();
                Refresh_();
                MessageBox.Show(this,
                    "Tentativa concluída. Veja o status atualizado abaixo.",
                    "CertExpiryMonitor",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "EnsureRegistered failed from diagnostics window");
                MessageBox.Show(this,
                    "Não foi possível registrar. Veja monitor.log para detalhes.",
                    "CertExpiryMonitor",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            finally
            {
                registerBtn.Text    = originalText;
                registerBtn.Enabled = true;
                UseWaitCursor       = false;
            }
        };

        var closeBtn = new Button
        {
            Text         = "Fechar",
            Location     = new Point(514, 388),
            Size         = new Size(90, 32),
            Anchor       = AnchorStyles.Bottom | AnchorStyles.Right,
            DialogResult = DialogResult.Cancel
        };
        CancelButton = closeBtn;

        Controls.AddRange([titleLabel, description, _statusLabel, _detailsBox, refreshBtn, registerBtn, closeBtn]);
    }

    private void Refresh_()
    {
        var status = _startup.QueryStatus();

        if (status.IsRegistered)
        {
            _statusLabel.Text      = "✓  Registrado: o app vai abrir sozinho no próximo logon.";
            _statusLabel.ForeColor = Color.FromArgb(28, 115, 64);
        }
        else
        {
            _statusLabel.Text      = "⚠  Não registrado: o app NÃO vai abrir sozinho no próximo logon.";
            _statusLabel.ForeColor = Color.FromArgb(176, 32, 32);
        }

        var lines = new[]
        {
            "Executável resolvido:",
            $"   {status.ResolvedExecutablePath}",
            "",
            $"Task Scheduler (CertExpiryMonitor): {(status.TaskSchedulerRegistered ? "REGISTRADO" : "ausente")}",
            status.TaskSchedulerRegistered
                ? $"   {status.TaskSchedulerCommand}"
                : "   (consulta `schtasks /query /tn \"CertExpiryMonitor\"` no Prompt do Windows para confirmar)",
            "",
            $"HKCU\\Run (CertExpiryMonitor): {(status.RegistryRegistered ? "REGISTRADO" : "ausente")}",
            status.RegistryRegistered
                ? $"   {status.RegistryCommand}"
                : "   (consulta `reg query HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run /v CertExpiryMonitor`)",
            "",
            "Se nenhum dos dois estiver registrado, clique em \"Tentar registrar de novo\".",
            "O log detalhado fica em monitor.log no menu \"Abrir pasta de logs\"."
        };
        _detailsBox.Text = string.Join(Environment.NewLine, lines);
    }
}
