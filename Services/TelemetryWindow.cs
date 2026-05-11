using System.Drawing;
using System.Windows.Forms;

namespace CertExpiryMonitor.Services;

/// <summary>
/// Janela simples que mostra os contadores de telemetria local.
/// Permite ao usuario inspecionar/limpar e desativar a coleta.
/// </summary>
public sealed class TelemetryWindow : Form
{
    public TelemetryWindow(TelemetryService telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        Text            = "Estatísticas de uso (locais)";
        StartPosition   = FormStartPosition.CenterParent;
        ClientSize      = new Size(500, 460);
        MinimumSize     = new Size(440, 400);
        Icon            = AppIcon.Current;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox     = false;
        MinimizeBox     = false;

        var titleLabel = new Label
        {
            Text      = "📊  Estatísticas locais de uso",
            Font      = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? SystemFonts.DefaultFont.FontFamily, 12F, FontStyle.Bold),
            ForeColor = Color.FromArgb(15, 76, 129),
            AutoSize  = true,
            Location  = new Point(16, 16)
        };

        var disclaimer = new Label
        {
            Text     = telemetry.Enabled
                ? "Coleta ativa. Apenas contadores agregados — sem rede, sem thumbprints, sem dados pessoais."
                : "Coleta desativada. Para começar a coletar, ative em Configurações > Avançado.",
            AutoSize  = false,
            Location  = new Point(16, 50),
            Size      = new Size(468, 36),
            ForeColor = Color.FromArgb(80, 80, 80)
        };

        var env = telemetry.Load();
        var grid = new TableLayoutPanel
        {
            Location    = new Point(16, 92),
            Size        = new Size(468, 270),
            ColumnCount = 2,
            RowCount    = 11,
            CellBorderStyle = TableLayoutPanelCellBorderStyle.Single
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 70));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 30));

        AddRow(grid, "Verificações totais",                 env.TotalChecks);
        AddRow(grid, "  com plano de notificação",          env.ChecksWithPlan);
        AddRow(grid, "  puladas (hash/data igual)",         env.ChecksSkipped);
        AddRow(grid, "  manuais (botão / menu)",            env.ManualChecks);
        AddRow(grid, "Toasts exibidos",                     env.NotificationsShown);
        AddRow(grid, "Falhas de notificação (toast bloqueado)", env.NotificationFailures);
        AddRow(grid, "Cliques: \"Não lembrar este\"",       env.DismissOne);
        AddRow(grid, "Cliques: \"Não lembrar nenhum\"",     env.DismissAll);
        AddRow(grid, "Cliques: \"Voltar a lembrar\"",       env.Restore);
        AddRow(grid, "Faixas alteradas (saves)",            env.ThresholdsChanged);
        AddRow(grid, "Horário alterado (saves)",            env.ScheduleChanged);

        var createdAtLabel = new Label
        {
            Text      = $"Coletando desde: {env.CreatedAt.ToLocalTime():dd/MM/yyyy HH:mm}",
            AutoSize  = true,
            Location  = new Point(16, 370),
            ForeColor = Color.FromArgb(120, 120, 120)
        };

        var resetBtn = new Button
        {
            Text     = "Limpar estatísticas",
            Location = new Point(16, 400),
            Size     = new Size(150, 32)
        };
        resetBtn.Click += (_, _) =>
        {
            var confirm = MessageBox.Show(
                "Apagar todos os contadores de telemetria local? (Esta ação não pode ser desfeita.)",
                "Limpar estatísticas",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning,
                MessageBoxDefaultButton.Button2);

            if (confirm == DialogResult.Yes)
            {
                telemetry.Reset();
                Close();
            }
        };

        var closeBtn = new Button
        {
            Text     = "Fechar",
            Location = new Point(396, 400),
            Size     = new Size(90, 32),
            DialogResult = DialogResult.Cancel
        };

        CancelButton = closeBtn;

        Controls.AddRange([titleLabel, disclaimer, grid, createdAtLabel, resetBtn, closeBtn]);
    }

    private static void AddRow(TableLayoutPanel grid, string label, long value)
    {
        var lbl = new Label
        {
            Text      = label,
            Dock      = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding   = new Padding(8, 0, 0, 0)
        };
        var val = new Label
        {
            Text      = value.ToString("N0"),
            Dock      = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight,
            Padding   = new Padding(0, 0, 8, 0),
            Font      = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? SystemFonts.DefaultFont.FontFamily, 9F, FontStyle.Bold)
        };
        grid.Controls.Add(lbl);
        grid.Controls.Add(val);
    }
}
