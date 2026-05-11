using CertExpiryMonitor.Models;
using System.Data;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CertExpiryMonitor.Services;

// ---------------------------------------------------------------------------
// Options object — elimina o construtor com 13+ parametros.
// ---------------------------------------------------------------------------

public sealed class DetailsFormOptions
{
    public required IReadOnlyList<CertificateSnapshot> Certificates { get; init; }
    public required IReadOnlyDictionary<string, CertificateStateRecord> State { get; init; }
    public required TimeSpan NotificationTime { get; init; }
    public required bool NotificationSoundEnabled { get; init; }
    public required ExpiryThresholds Thresholds { get; init; }
    public required Action<string> DismissCertificate { get; init; }
    public required Action<string> RestoreCertificate { get; init; }
    public required Func<string, bool> RemoveExpiredCertificate { get; init; }
    public required Action<TimeSpan> SaveNotificationTime { get; init; }
    public required Action<bool> SaveNotificationSound { get; init; }
    public required Action<ExpiryThresholds> SaveThresholds { get; init; }
    public required Func<ExpiryThresholds> GetThresholds { get; init; }
    public required Func<bool> TestNotificationNow { get; init; }
    public required Func<(IReadOnlyList<CertificateSnapshot>, IReadOnlyDictionary<string, CertificateStateRecord>)> ReloadCertificates { get; init; }
    public required FileLogger Logger { get; init; }
    public bool OpenSettingsTab { get; init; }

    // Configuracoes avancadas
    public required LogFormat LogFormat { get; init; }
    public required bool EventLogEnabled { get; init; }
    public required bool TelemetryEnabled { get; init; }
    public required Action<LogFormat, bool, bool> SaveAdvancedSettings { get; init; }
}

// ---------------------------------------------------------------------------
// Form
// ---------------------------------------------------------------------------

public sealed class DetailsForm : Form
{
    private readonly TabControl _tabs;
    private readonly TabPage _certificatesTab;
    private readonly TabPage _settingsTab;

    public DetailsForm(DetailsFormOptions options)
    {
        Text            = "Certificados A1 monitorados";
        StartPosition   = FormStartPosition.CenterScreen;
        ClientSize      = new Size(940, 600);  // +100 px do GroupBox "Avançado" na aba Configurações
        MinimumSize     = new Size(780, 520);
        Icon            = AppIcon.Current;     // icone proprio na barra de titulo e Alt+Tab

        var table = CreateCertificateTable();
        FillCertificateTable(table, options.Certificates, options.State, options.Thresholds);
        var view  = new DataView(table);

        var grid         = BuildGrid(view);
        var summaryPanel = BuildSummaryPanel(table, view, options.Thresholds, grid);
        var status       = BuildStatusLabel(options.Certificates.Count);
        var bottom       = BuildBottomPanel(grid, table, view, summaryPanel, status, options);

        _certificatesTab = new TabPage("Certificados");
        _certificatesTab.Controls.Add(grid);
        _certificatesTab.Controls.Add(summaryPanel);
        _certificatesTab.Controls.Add(bottom);

        _settingsTab = BuildSettingsTab(options, onThresholdsSaved: thresholds =>
        {
            // Reclassifica as linhas em memoria com os novos thresholds (sem recarregar do disco),
            // atualiza os rotulos "Ate X dias" do summary panel e refaz cores/contagens.
            RefreshRowClassifications(table, thresholds);
            UpdateSummaryThresholdLabels(summaryPanel, thresholds);
            UpdateSummaryItems(summaryPanel, table);
            // grid.Refresh() forca o DataGridView a re-puxar valores do DataView/DataTable.
            // Sem isso, ApplyRowStyles le os StatusCategory antigos do cell cache.
            grid.Refresh();
            ApplyRowStyles(grid);
        });

        _tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs.TabPages.Add(_certificatesTab);
        _tabs.TabPages.Add(_settingsTab);
        _tabs.SelectedTab = options.OpenSettingsTab ? _settingsTab : _certificatesTab;

        Controls.Add(_tabs);
    }

    public void FocusExisting(bool openSettingsTab)
    {
        _tabs.SelectedTab = openSettingsTab ? _settingsTab : _certificatesTab;

        if (WindowState == FormWindowState.Minimized)
        {
            WindowState = FormWindowState.Normal;
        }

        TopMost = true;
        Show();
        BringToFront();
        Activate();
        NativeMethods.SetForegroundWindow(Handle);
        TopMost = false;
        BringToFront();
    }

    // -------------------------------------------------------------------------
    // Construcao da aba Certificados
    // -------------------------------------------------------------------------

    private static DataGridView BuildGrid(DataView view)
    {
        var grid = new DataGridView
        {
            Dock                      = DockStyle.Fill,
            ReadOnly                  = true,
            AllowUserToAddRows        = false,
            AllowUserToDeleteRows     = false,
            AutoSizeColumnsMode       = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode             = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect               = false,
            AutoGenerateColumns       = false,
            BackgroundColor           = Color.White,
            BorderStyle               = BorderStyle.None,
            CellBorderStyle           = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle  = DataGridViewHeaderBorderStyle.None,
            EnableHeadersVisualStyles = false,
            RowHeadersVisible         = false,
            RowTemplate               = { Height = 30 },
            AllowUserToResizeRows     = false
        };

        grid.ColumnHeadersDefaultCellStyle.BackColor  = Color.FromArgb(245, 247, 250);
        grid.ColumnHeadersDefaultCellStyle.ForeColor  = Color.FromArgb(35, 35, 35);
        grid.ColumnHeadersDefaultCellStyle.Font       = new Font(grid.Font, FontStyle.Bold);
        grid.ColumnHeadersDefaultCellStyle.Padding    = new Padding(4, 0, 4, 0);
        grid.ColumnHeadersHeight                      = 36;
        grid.GridColor                                = Color.FromArgb(225, 229, 235);
        grid.DefaultCellStyle.Padding                 = new Padding(4, 0, 4, 0);
        grid.DefaultCellStyle.SelectionBackColor      = Color.FromArgb(226, 239, 255);
        grid.DefaultCellStyle.SelectionForeColor      = Color.Black;
        // Zebra striping mais saturada (era 250,250,250 — invisivel). Contraste com fundo branco.
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(244, 247, 250);

        // FillWeight 150 reserva mais espaço para o titular (nomes corporativos longos
        // tipicamente passam de 30 caracteres). DataGridView mostra tooltip nativo
        // automaticamente para celulas truncadas (ShowCellToolTips=true por default).
        grid.Columns.Add(new DataGridViewTextBoxColumn  { Name = "Holder",         HeaderText = "Titular",        DataPropertyName = "Holder",         FillWeight = 150, SortMode = DataGridViewColumnSortMode.Automatic });
        grid.Columns.Add(new DataGridViewTextBoxColumn  { Name = "Document",       HeaderText = "CPF/CNPJ",       DataPropertyName = "Document",       FillWeight = 70, SortMode = DataGridViewColumnSortMode.Automatic });
        // Vencimento: somente data (hora era ruido — usuario nao age sobre minutos).
        grid.Columns.Add(new DataGridViewTextBoxColumn  { Name = "NotAfter",       HeaderText = "Vencimento",     DataPropertyName = "NotAfter",       FillWeight = 75, DefaultCellStyle = new DataGridViewCellStyle { Format = "dd/MM/yyyy" }, SortMode = DataGridViewColumnSortMode.Automatic });
        // Dias restantes: alinhado a direita para comparacao numerica visual.
        grid.Columns.Add(new DataGridViewTextBoxColumn  { Name = "Days",           HeaderText = "Dias restantes", DataPropertyName = "Days",           FillWeight = 65, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight, Padding = new Padding(0, 0, 10, 0) }, SortMode = DataGridViewColumnSortMode.Automatic });
        grid.Columns.Add(new DataGridViewTextBoxColumn  { Name = "Status",         HeaderText = "Situação",       DataPropertyName = "Status",         FillWeight = 70, DefaultCellStyle = new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleCenter }, SortMode = DataGridViewColumnSortMode.Automatic });
        grid.Columns.Add(new DataGridViewTextBoxColumn  { Name = "Thumbprint",     HeaderText = "Thumbprint",     DataPropertyName = "Thumbprint",     Visible = false });
        grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "Dismissed",      HeaderText = "Dismissed",      DataPropertyName = "Dismissed",      Visible = false });
        grid.Columns.Add(new DataGridViewTextBoxColumn  { Name = "StatusCategory", HeaderText = "StatusCategory", DataPropertyName = "StatusCategory", Visible = false });

        grid.DataSource = view;
        grid.DataBindingComplete += (_, _) => ApplyRowStyles(grid);
        return grid;
    }

    private static Label BuildStatusLabel(int count)
    {
        return new Label
        {
            Text        = FormatCountLabel(count),
            Dock        = DockStyle.Fill,
            TextAlign   = ContentAlignment.MiddleLeft,
            Padding     = new Padding(12, 0, 0, 0)
        };
    }

    /// <summary>
    /// Texto do rodape com singular/plural correto e CTA quando vazio.
    /// </summary>
    private static string FormatCountLabel(int count) => count switch
    {
        0 => "Nenhum certificado A1 encontrado. Instale um certificado .pfx no Windows para começar a monitorar.",
        1 => "1 certificado encontrado no usuário atual.",
        _ => $"{count} certificados encontrados no usuário atual."
    };

    private Panel BuildBottomPanel(
        DataGridView grid,
        DataTable table,
        DataView view,
        Panel summaryPanel,
        Label status,
        DetailsFormOptions options)
    {
        var dismiss = new Button { Text = "Ignorar certificado", Anchor = AnchorStyles.Right | AnchorStyles.Top, Width = 150, Height = 32, Location = new Point(0, 6) };
        var analyze = new Button { Text = "Atualizar lista",     Anchor = AnchorStyles.Right | AnchorStyles.Top, Width = 120, Height = 32, Location = new Point(0, 6) };
        var close   = new Button { Text = "Fechar",              Anchor = AnchorStyles.Right | AnchorStyles.Top, Width = 90,  Height = 32, Location = new Point(0, 6) };

        var toolTip = new ToolTip();
        toolTip.SetToolTip(analyze, "Releia os certificados instalados e atualiza esta tela. As notificações seguem a verificação diária.");
        toolTip.SetToolTip(dismiss, "Selecione um certificado na lista para ignorá-lo (ou para voltar a lembrar dele).");
        toolTip.SetToolTip(close,   "Fechar esta janela. O monitor continua rodando em segundo plano na bandeja.");

        // Acessibilidade (UIA / leitores de tela): nome descritivo independente do Text.
        dismiss.AccessibleName        = "Ignorar ou restaurar o certificado selecionado";
        dismiss.AccessibleDescription = "Marca o certificado para nao notificar mais, ou reverte a marcacao.";
        analyze.AccessibleName        = "Atualizar lista de certificados";
        close.AccessibleName          = "Fechar janela de detalhes";
        grid.AccessibleName           = "Lista de certificados A1 do usuario atual";
        grid.AccessibleDescription    = "Tabela com titular, documento, vencimento, dias restantes e situacao de cada certificado.";

        close.Click += (_, _) => Close();

        grid.SelectionChanged += (_, _) => UpdateDismissButtonText(grid, dismiss);
        Shown += (_, _) =>
        {
            grid.ClearSelection();
            grid.CurrentCell = null;
            UpdateDismissButtonText(grid, dismiss);
        };

        WireDismissButton(dismiss, grid, table, summaryPanel, options);
        WireRemoveExpiredMenuItem(grid, table, summaryPanel, status, options);
        WireAnalyzeButton(analyze, grid, table, view, summaryPanel, status, options);

        var buttonPanel = new FlowLayoutPanel
        {
            Dock          = DockStyle.Right,
            Width         = 390,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents  = false,
            Padding       = new Padding(0),
            Margin        = new Padding(0)
        };
        // FlowDirection=RightToLeft: o PRIMEIRO Add fica mais a direita.
        // Convencao Windows: acao primaria/Fechar mais isolada a direita; demais a esquerda.
        // Resultado visual: [Atualizar lista]  [Ignorar certificado]  [Fechar]
        buttonPanel.Controls.Add(close);    // direita (rightmost)
        buttonPanel.Controls.Add(dismiss);  // meio
        buttonPanel.Controls.Add(analyze);  // esquerda

        var bottom = new Panel
        {
            Dock      = DockStyle.Bottom,
            Height    = 48,
            BackColor = Color.FromArgb(248, 249, 251),
            Padding   = new Padding(0, 6, 8, 6)
        };
        bottom.Controls.Add(status);
        bottom.Controls.Add(buttonPanel);
        return bottom;
    }

    private static void WireDismissButton(
        Button dismiss,
        DataGridView grid,
        DataTable table,
        Panel summaryPanel,
        DetailsFormOptions options)
    {
        dismiss.Click += (_, _) =>
        {
            try
            {
                if (grid.SelectedRows.Count == 0) return;

                var selectedRow  = grid.SelectedRows[0];
                var thumbprint   = Convert.ToString(selectedRow.Cells["Thumbprint"].Value);
                if (string.IsNullOrWhiteSpace(thumbprint)) return;

                var rowView    = selectedRow.DataBoundItem as DataRowView;
                var isDismissed = rowView?.Row.Field<bool>("Dismissed") == true;
                var daysRemaining = rowView?.Row.Field<int>("Days") ?? 9999;

                if (isDismissed) options.RestoreCertificate(thumbprint);
                else             options.DismissCertificate(thumbprint);

                if (rowView is not null)
                {
                    var t = options.GetThresholds().Normalized();
                    rowView.BeginEdit();
                    rowView["Status"]         = isDismissed ? GetStatusText(daysRemaining, CertificateNotificationState.None, t) : "Ignorado";
                    rowView["Dismissed"]      = !isDismissed;
                    rowView["StatusCategory"] = isDismissed ? GetStatusCategory(daysRemaining, CertificateNotificationState.None, t) : "Dismissed";
                    rowView.EndEdit();
                }

                UpdateSummaryItems(summaryPanel, table);
                grid.Refresh();
                ApplyRowStyles(grid);
                UpdateDismissButtonText(grid, dismiss);
            }
            catch (Exception ex)
            {
                options.Logger.Error(ex, "Failed to dismiss certificate from details window");
                MessageBox.Show("Não foi possível salvar a alteração. Tente novamente.", "CertExpiryMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
    }

    private void WireRemoveExpiredMenuItem(
        DataGridView grid,
        DataTable table,
        Panel summaryPanel,
        Label status,
        DetailsFormOptions options)
    {
        var contextMenu      = new ContextMenuStrip();
        var removeExpiredItem = new ToolStripMenuItem("Remover certificado vencido");
        contextMenu.Items.Add(removeExpiredItem);
        grid.ContextMenuStrip = contextMenu;

        grid.CellMouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Right) return;
            if (e.RowIndex < 0) { grid.ClearSelection(); grid.CurrentCell = null; return; }
            grid.ClearSelection();
            grid.Rows[e.RowIndex].Selected = true;
            grid.CurrentCell = grid.Rows[e.RowIndex].Cells["Holder"];
        };

        contextMenu.Opening += (_, e) =>
        {
            // Defensa contra right-click no header ou em area vazia da grid:
            // sem linha selecionada NUNCA mostrar menu de contexto. Hoje o
            // unico item depende de StatusCategory='Expired', mas qualquer
            // item futuro herda essa proteção.
            if (grid.SelectedRows.Count == 0)
            {
                e.Cancel = true;
                return;
            }

            var hasExpired = TryGetSelectedRowView(grid, out var rv) &&
                             rv.Row.Field<string>("StatusCategory") == "Expired";
            removeExpiredItem.Enabled = hasExpired;
            e.Cancel = !hasExpired;
        };

        removeExpiredItem.Click += (_, _) =>
        {
            try
            {
                if (!TryGetSelectedRowView(grid, out var rowView)) return;
                if (rowView.Row.Field<string>("StatusCategory") != "Expired") return;

                var holder     = rowView.Row.Field<string>("Holder") ?? "certificado";
                var thumbprint = rowView.Row.Field<string>("Thumbprint");
                if (string.IsNullOrWhiteSpace(thumbprint)) return;

                var confirm = MessageBox.Show(
                    $"Remover o certificado vencido de {holder} do repositório do usuário atual?",
                    "Remover certificado vencido",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (confirm != DialogResult.Yes) return;

                if (!options.RemoveExpiredCertificate(thumbprint))
                {
                    MessageBox.Show("Não foi possível remover o certificado. Ele pode já ter sido removido ou estar bloqueado.", "CertExpiryMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                table.Rows.Remove(rowView.Row);
                UpdateSummaryItems(summaryPanel, table);
                status.Text = FormatCountLabel(table.Rows.Count);
                grid.ClearSelection();
                grid.CurrentCell = null;
                ApplyRowStyles(grid);
            }
            catch (Exception ex)
            {
                options.Logger.Error(ex, "Failed to remove expired certificate from details window");
                MessageBox.Show("Não foi possível remover o certificado. Tente novamente.", "CertExpiryMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
    }

    private static void WireAnalyzeButton(
        Button analyze,
        DataGridView grid,
        DataTable table,
        DataView view,
        Panel summaryPanel,
        Label status,
        DetailsFormOptions options)
    {
        analyze.Click += async (_, _) =>
        {
            try
            {
                analyze.Enabled = false;
                status.Text     = "Atualizando lista de certificados...";

                var refreshed = await Task.Run(options.ReloadCertificates);
                FillCertificateTable(table, refreshed.Item1, refreshed.Item2, options.GetThresholds());
                UpdateSummaryItems(summaryPanel, table);
                status.Text = $"{FormatCountLabel(table.Rows.Count)} Atualizado às {DateTime.Now:HH:mm}.";
                grid.ClearSelection();
                grid.CurrentCell = null;
                ApplyRowStyles(grid);
            }
            catch (Exception ex)
            {
                options.Logger.Error(ex, "Failed to refresh certificate details");
                MessageBox.Show("Não foi possível analisar os certificados agora. Tente novamente.", "CertExpiryMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                analyze.Enabled = true;
            }
        };
    }

    // -------------------------------------------------------------------------
    // Aba Configuracoes
    // -------------------------------------------------------------------------

    private static TabPage BuildSettingsTab(DetailsFormOptions options, Action<ExpiryThresholds>? onThresholdsSaved = null)
    {
        var tab = new TabPage("Configurações") { Padding = new Padding(20), AutoScroll = true };
        var tip = new ToolTip { AutoPopDelay = 8000, InitialDelay = 400, ReshowDelay = 200 };

        // ===== GroupBox "Notificação" =====
        var groupNotification = new GroupBox
        {
            Text     = "Notificação",
            Location = new Point(20, 16),
            Size     = new Size(560, 165)
        };

        var descTime = new Label
        {
            Text     = "O aplicativo acompanha o relógio do PC e mostra o aviso neste horário, uma vez por dia, quando houver certificados próximos do vencimento.",
            AutoSize = false,
            Location = new Point(12, 26),
            Size     = new Size(536, 38)
        };

        var lblTime = new Label
        {
            Text      = "Horário do popup:",
            AutoSize  = true,
            Location  = new Point(12, 75),
            TextAlign = ContentAlignment.MiddleLeft
        };

        var timePicker = new DateTimePicker
        {
            Format         = DateTimePickerFormat.Custom,
            CustomFormat   = "HH:mm",
            ShowUpDown     = true,
            Location       = new Point(135, 72),
            Width          = 80,
            Value          = DateTime.Today.Add(options.NotificationTime),
            AccessibleName = "Horário do popup diário"
        };

        var testPopup = new Button
        {
            Text           = "Testar popup agora",
            Location       = new Point(230, 70),
            Size           = new Size(130, 28),
            AccessibleName = "Testar popup agora (sem aguardar o horário diário)"
        };
        tip.SetToolTip(testPopup, "Dispara o popup imediatamente para validar se a notificação está funcionando.");

        var sound = new CheckBox
        {
            Text     = "Tocar som ao mostrar o aviso",
            AutoSize = true,
            Checked  = options.NotificationSoundEnabled,
            Location = new Point(12, 110),
            AccessibleName = "Tocar som ao mostrar o aviso"
        };
        tip.SetToolTip(sound, "Toca um beep do sistema quando o popup aparece. Sem efeito se as notificações do Windows estiverem em modo silencioso.");

        groupNotification.Controls.AddRange([descTime, lblTime, timePicker, testPopup, sound]);

        // ===== GroupBox "Faixas de notificação" — 1 coluna sequencial decrescente =====
        var groupThresholds = new GroupBox
        {
            Text     = "Faixas de notificação (dias antes do vencimento)",
            Location = new Point(20, 195),
            Size     = new Size(560, 195)
        };

        var thresholdsDesc = new Label
        {
            Text     = "Da faixa mais distante (primeiro aviso) à mais crítica (último aviso):",
            AutoSize = false,
            Location = new Point(12, 24),
            Size     = new Size(536, 18),
            ForeColor = Color.FromArgb(80, 80, 80)
        };
        groupThresholds.Controls.Add(thresholdsDesc);

        // Layout 1-coluna decrescente: Faixa longa → média → curta → Urgente.
        var t = options.Thresholds;

        static (Label lbl, NumericUpDown spin) MakeField(string text, int value, int y, string accessibleName, string tooltip, ToolTip tip)
        {
            var lbl  = new Label  { Text = text, AutoSize = true, Location = new Point(12, y + 4), Width = 110 };
            var spin = new NumericUpDown
            {
                Minimum  = 1,
                Maximum  = 3650,
                Value    = value,
                Width    = 70,
                Location = new Point(130, y),
                AccessibleName = accessibleName
            };
            tip.SetToolTip(lbl,  tooltip);
            tip.SetToolTip(spin, tooltip);
            return (lbl, spin);
        }

        var (lblL30, spinL30) = MakeField("Faixa longa:",  t.Level30, 50,  "Faixa longa em dias",  "Primeiro aviso — quantos dias ANTES do vencimento começar a avisar. Default: 30 dias.", tip);
        var (lblL15, spinL15) = MakeField("Faixa média:",  t.Level15, 82,  "Faixa média em dias",  "Segundo aviso — faixa intermediária. Deve ser menor que a faixa longa. Default: 15 dias.", tip);
        var (lblL7,  spinL7)  = MakeField("Faixa curta:",  t.Level7,  114, "Faixa curta em dias",  "Terceiro aviso — alerta de proximidade. Deve ser menor que a faixa média. Default: 7 dias.", tip);
        var (lblL1,  spinL1)  = MakeField("Urgente:",      t.Level1,  146, "Urgente em dias",      "Último aviso (crítico) — alerta final. Default: 1 dia.", tip);

        // Etiqueta "(dias)" à direita de cada spin para reforçar unidade.
        Label DaysSuffix(int y) => new Label
        {
            Text      = "dias",
            AutoSize  = true,
            Location  = new Point(208, y + 4),
            ForeColor = Color.FromArgb(120, 120, 120)
        };

        groupThresholds.Controls.AddRange([
            lblL30, spinL30, DaysSuffix(50),
            lblL15, spinL15, DaysSuffix(82),
            lblL7,  spinL7,  DaysSuffix(114),
            lblL1,  spinL1,  DaysSuffix(146)
        ]);

        // ===== GroupBox "Avançado" =====
        var groupAdvanced = new GroupBox
        {
            Text     = "Avançado",
            Location = new Point(20, 408),
            Size     = new Size(560, 105)
        };

        var chkLogJson = new CheckBox
        {
            Text     = "Logs em formato JSON (facilita ingestão em SIEM/Splunk)",
            AutoSize = true,
            Checked  = options.LogFormat == LogFormat.Json,
            Location = new Point(12, 24),
            AccessibleName = "Gravar logs em formato JSON estruturado"
        };
        tip.SetToolTip(chkLogJson, "Formato texto humano-legível (default) vs JSON Lines (uma linha por evento, fácil de parsear). Aplicado em monitor.log.");

        var chkEventLog = new CheckBox
        {
            Text     = "Espelhar erros no Windows Event Log (Application channel)",
            AutoSize = true,
            Checked  = options.EventLogEnabled,
            Location = new Point(12, 48),
            AccessibleName = "Espelhar erros no Windows Event Log"
        };
        tip.SetToolTip(chkEventLog, "Quando ativo, eventos ERROR são duplicados no Event Log do Windows. Padrão para monitoramento corporativo via SCOM/Sentinel.");

        var chkTelemetry = new CheckBox
        {
            Text     = "Coletar estatísticas anônimas locais (ajuda a melhorar o app)",
            AutoSize = true,
            Checked  = options.TelemetryEnabled,
            Location = new Point(12, 72),
            AccessibleName = "Coletar estatísticas anônimas locais"
        };
        tip.SetToolTip(chkTelemetry, "Salva contadores agregados em telemetry.json (sem rede, sem thumbprints, sem dados pessoais). Apenas: total de checks, notificações exibidas, dismisses. Default: desligado.");

        groupAdvanced.Controls.AddRange([chkLogJson, chkEventLog, chkTelemetry]);

        // ===== Feedback label (status de save) =====
        var feedback = new Label
        {
            AutoSize  = true,
            ForeColor = Color.FromArgb(28, 115, 64),
            Location  = new Point(20, 525)
        };

        // ===== Botão "Salvar configurações" unificado =====
        var saveAll = new Button
        {
            Text           = "Salvar configurações",
            Location       = new Point(440, 520),
            Size           = new Size(140, 32),
            AccessibleName = "Salvar todas as configurações (horário, som, faixas e avançado)"
        };
        tip.SetToolTip(saveAll, "Salva horário do popup, opção de som e as 4 faixas. Faixas fora de ordem são ajustadas automaticamente.");

        saveAll.Click += (_, _) =>
        {
            try
            {
                // Coletar valores propostos
                var proposed = new ExpiryThresholds
                {
                    Level30 = (int)spinL30.Value,
                    Level15 = (int)spinL15.Value,
                    Level7  = (int)spinL7.Value,
                    Level1  = (int)spinL1.Value
                }.Normalized();

                // Refletir valores normalizados no UI
                spinL30.Value = proposed.Level30;
                spinL15.Value = proposed.Level15;
                spinL7.Value  = proposed.Level7;
                spinL1.Value  = proposed.Level1;

                // Persistir tudo em sequência (cada store ignora se já está atualizado)
                options.SaveNotificationTime(timePicker.Value.TimeOfDay);
                options.SaveNotificationSound(sound.Checked);
                options.SaveThresholds(proposed);
                options.SaveAdvancedSettings(
                    chkLogJson.Checked   ? LogFormat.Json : LogFormat.Text,
                    chkEventLog.Checked,
                    chkTelemetry.Checked);
                onThresholdsSaved?.Invoke(proposed);

                feedback.Text = $"Configurações salvas: {timePicker.Value:HH:mm}, som {(sound.Checked ? "ligado" : "desligado")}, faixas {proposed.Level1}/{proposed.Level7}/{proposed.Level15}/{proposed.Level30}.";
            }
            catch (Exception ex)
            {
                options.Logger.Error(ex, "Failed to save settings");
                MessageBox.Show("Não foi possível salvar as configurações. Tente novamente.", "CertExpiryMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        testPopup.Click += (_, _) =>
        {
            try   { feedback.Text = options.TestNotificationNow() ? "Popup de teste enviado." : "Nenhum certificado pendente para popup agora."; }
            catch (Exception ex) { options.Logger.Error(ex, "Failed to test popup"); MessageBox.Show("Não foi possível testar o popup agora. Tente novamente.", "CertExpiryMonitor", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };

        tab.Controls.AddRange([groupNotification, groupThresholds, groupAdvanced, feedback, saveAll]);
        return tab;
    }

    // -------------------------------------------------------------------------
    // Painel de resumo
    // -------------------------------------------------------------------------

    private static Panel BuildSummaryPanel(DataTable table, DataView view, ExpiryThresholds thresholds, DataGridView grid)
    {
        var panel = new Panel
        {
            Dock      = DockStyle.Top,
            Height    = 102,  // mais altura para acomodar linha de busca
            Padding   = new Padding(12, 10, 12, 8),
            BackColor = Color.White
        };

        // ===== Linha 1: cards clicaveis =====
        var summaryItems = new FlowLayoutPanel
        {
            Dock          = DockStyle.Top,
            Height        = 50,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents  = false,
            Margin        = new Padding(0)
        };

        // Icones unicode complementam a cor — daltonicos distinguem por simbolo.
        // Usados apenas caracteres do BMP (U+0000..U+FFFF) que estao na fonte Segoe UI padrao.
        summaryItems.Controls.Add(CreateSummaryItem("Expired",   "⛔  Vencidos",                Color.FromArgb(176, 32, 32)));
        summaryItems.Controls.Add(CreateSummaryItem("Critical",  $"⚠  Até {thresholds.Level7} dias",  Color.FromArgb(180, 96, 0)));
        summaryItems.Controls.Add(CreateSummaryItem("Warning",   $"⌛  Até {thresholds.Level30} dias", Color.FromArgb(140, 115, 0)));
        summaryItems.Controls.Add(CreateSummaryItem("Valid",     "✓  Válidos",                 Color.FromArgb(28, 115, 64)));
        summaryItems.Controls.Add(CreateSummaryItem("Dismissed", "⊘  Ignorados",               Color.FromArgb(90, 90, 90)));

        // ===== Linha 2: busca textual + filtro categoria =====
        var controlsRow = new Panel
        {
            Dock     = DockStyle.Top,
            Height   = 36,
            Margin   = new Padding(0, 6, 0, 0)
        };

        var lblSearch = new Label
        {
            Text      = "Buscar:",
            AutoSize  = false,
            Width     = 50,
            Height    = 28,
            Location  = new Point(0, 4),
            TextAlign = ContentAlignment.MiddleLeft
        };
        var searchBox = new TextBox
        {
            Width                   = 240,
            Height                  = 24,
            Location                = new Point(50, 6),
            PlaceholderText         = "Filtrar por titular ou CPF/CNPJ...",
            AccessibleName          = "Buscar por titular ou documento",
            AccessibleDescription   = "Filtra a lista por nome do titular ou número do documento. Combina com o filtro de categoria."
        };
        var tip = new ToolTip();
        tip.SetToolTip(searchBox, "Filtra a lista por nome do titular ou número do documento. A busca é case-insensitive e combina com o filtro de categoria.");

        var filterArea  = new Panel { Anchor = AnchorStyles.Top | AnchorStyles.Right, Width = 220, Height = 28, Location = new Point(0, 4) };
        var filterLabel = new Label { Text = "Mostrar:", Dock = DockStyle.Left, Width = 62, TextAlign = ContentAlignment.MiddleLeft };
        var filter      = new ComboBox
        {
            DropDownStyle           = ComboBoxStyle.DropDownList,
            Dock                    = DockStyle.Fill,
            AccessibleName          = "Filtro de exibição de certificados",
            AccessibleDescription   = "Mostra apenas certificados de uma categoria (Vencidos, A vencer, Válidos ou Ignorados)."
        };
        filter.Items.AddRange(["Todos", "Vencidos", "A vencer", "Válidos", "Ignorados"]);
        filter.SelectedIndex = 0;

        // Funcao centralizada que compoe categoria + busca textual em um unico RowFilter.
        // E (re)chamada por qualquer mudanca de filtro ou texto de busca.
        void ApplyFilters()
        {
            var clauses = new List<string>();

            // Filtro de categoria
            var categoryClause = filter.SelectedItem?.ToString() switch
            {
                "Vencidos"  => "StatusCategory = 'Expired'",
                "A vencer"  => "StatusCategory IN ('Critical', 'Warning')",
                "Válidos"   => "StatusCategory = 'Valid'",
                "Ignorados" => "StatusCategory = 'Dismissed'",
                _           => null
            };
            if (categoryClause is not null) clauses.Add(categoryClause);

            // Busca textual (case-insensitive via expressao LIKE no DataTable.RowFilter).
            // O parser do RowFilter trata varios chars como metacharacteres:
            //   '  *  %  [  ]
            // Sem escape completo, um usuario digitando "[" dispara
            // SyntaxErrorException nao tratada que sobe ate Application.ThreadException.
            // Ver CertificateSearchEscaper para detalhes.
            var query = searchBox.Text?.Trim() ?? string.Empty;
            if (query.Length > 0)
            {
                var escaped = CertificateSearchEscaper.EscapeForRowFilter(query);
                clauses.Add($"(Holder LIKE '%{escaped}%' OR Document LIKE '%{escaped}%')");
            }

            view.RowFilter = clauses.Count > 0 ? string.Join(" AND ", clauses) : string.Empty;

            // Item 8: esconder coluna "Situação" quando filtro de categoria especifico esta ativo
            // (a coluna ficaria 100% redundante — todas as linhas teriam o mesmo valor).
            if (grid.Columns["Status"] is { } statusCol)
            {
                statusCol.Visible = categoryClause is null;
            }
        }

        filter.SelectedIndexChanged += (_, _) => ApplyFilters();
        searchBox.TextChanged       += (_, _) => ApplyFilters();

        // Item 1: cards clicaveis mudam o filtro de categoria.
        foreach (var label in summaryItems.Controls.OfType<Label>())
        {
            label.Cursor = Cursors.Hand;
            tip.SetToolTip(label, "Clique para filtrar a lista por esta categoria.");
            var categoryName = label.Name; // captura para closure
            label.Click += (_, _) =>
            {
                filter.SelectedItem = categoryName switch
                {
                    "Expired"   => "Vencidos",
                    "Critical"  => "A vencer",  // Critical é parte de "A vencer"
                    "Warning"   => "A vencer",
                    "Valid"     => "Válidos",
                    "Dismissed" => "Ignorados",
                    _           => "Todos"
                };
            };
        }

        filterArea.Controls.Add(filter);
        filterArea.Controls.Add(filterLabel);

        // FlowLayoutPanel para alinhar searchBox a esquerda e filtro a direita
        controlsRow.Controls.Add(lblSearch);
        controlsRow.Controls.Add(searchBox);
        controlsRow.Controls.Add(filterArea);
        controlsRow.Resize += (_, _) =>
        {
            // mantem o filterArea encostado a direita ao redimensionar
            filterArea.Location = new Point(controlsRow.ClientSize.Width - filterArea.Width, 4);
        };

        panel.Controls.Add(controlsRow);
        panel.Controls.Add(summaryItems);
        UpdateSummaryItems(panel, table);
        return panel;
    }

    private static Label CreateSummaryItem(string name, string label, Color color)
    {
        return new Label
        {
            Name      = name,
            AutoSize  = false,
            Width     = 126,
            Height    = 42,
            Margin    = new Padding(0, 0, 8, 0),
            Padding   = new Padding(10, 4, 10, 4),
            BackColor = Color.FromArgb(248, 249, 251),
            ForeColor = color,
            Font      = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? SystemFonts.DefaultFont.FontFamily, 9F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Tag       = label
        };
    }

    private static void UpdateSummaryItems(Control panel, DataTable table)
    {
        foreach (var label in EnumerateControls(panel).OfType<Label>())
        {
            if (label.Tag is not string title) continue;

            var count = label.Name switch
            {
                "Expired"   => table.Select("StatusCategory = 'Expired'").Length,
                "Critical"  => table.Select("StatusCategory = 'Critical'").Length,
                "Warning"   => table.Select("StatusCategory = 'Warning'").Length,
                "Valid"     => table.Select("StatusCategory = 'Valid'").Length,
                "Dismissed" => table.Select("StatusCategory = 'Dismissed'").Length,
                _           => 0
            };

            label.Text = $"{count}\r\n{title}";
        }
    }

    private static void UpdateSummaryThresholdLabels(Control panel, ExpiryThresholds thresholds)
    {
        // Atualiza o Tag (titulo) dos labels "Critical" e "Warning" — UpdateSummaryItems
        // entao formata `count + Tag` no Text. Demais labels (Expired/Valid/Dismissed)
        // tem titulos fixos e nao dependem dos thresholds.
        var normalized = thresholds.Normalized();
        foreach (var label in EnumerateControls(panel).OfType<Label>())
        {
            label.Tag = label.Name switch
            {
                "Critical" => $"⚠  Até {normalized.Level7} dias",
                "Warning"  => $"⌛  Até {normalized.Level30} dias",
                _          => label.Tag
            };
        }
    }

    private static void RefreshRowClassifications(DataTable table, ExpiryThresholds thresholds)
    {
        // Re-aplica GetStatusText/GetStatusCategory sobre as linhas existentes.
        // Usado quando o usuario muda thresholds — os dados (Days, NotAfter, etc.) nao
        // mudam, apenas a classificacao deles.
        var normalized = thresholds.Normalized();
        foreach (DataRow row in table.Rows)
        {
            var days        = (int)row["Days"];
            var isDismissed = (bool)row["Dismissed"];
            var state       = isDismissed ? CertificateNotificationState.Dismissed : (CertificateNotificationState?)null;

            row["Status"]         = CertificateStatusHelpers.GetStatusText(days, state, normalized);
            row["StatusCategory"] = CertificateStatusHelpers.GetStatusCategory(days, state, normalized);
        }
    }

    private static IEnumerable<Control> EnumerateControls(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in EnumerateControls(child))
            {
                yield return descendant;
            }
        }
    }

    // -------------------------------------------------------------------------
    // Tabela de certificados
    // -------------------------------------------------------------------------

    private static DataTable CreateCertificateTable()
    {
        var table = new DataTable();
        table.Columns.Add("Holder",         typeof(string));
        table.Columns.Add("Document",       typeof(string));
        table.Columns.Add("NotAfter",       typeof(DateTime));
        table.Columns.Add("Days",           typeof(int));
        table.Columns.Add("Status",         typeof(string));
        table.Columns.Add("Thumbprint",     typeof(string));
        table.Columns.Add("Dismissed",      typeof(bool));
        table.Columns.Add("StatusCategory", typeof(string));
        return table;
    }

    private static void FillCertificateTable(
        DataTable table,
        IReadOnlyList<CertificateSnapshot> certificates,
        IReadOnlyDictionary<string, CertificateStateRecord> state,
        ExpiryThresholds thresholds)
    {
        table.Rows.Clear();

        var normalized = thresholds.Normalized();
        foreach (var certificate in certificates.OrderBy(c => c.NotAfter))
        {
            var thumbprint    = JsonStateStore.NormalizeThumbprint(certificate.Thumbprint);
            state.TryGetValue(thumbprint, out var record);
            var daysRemaining = DateOnly.FromDateTime(certificate.NotAfter.Date).DayNumber
                              - DateOnly.FromDateTime(DateTime.Today).DayNumber;
            var statusText    = GetStatusText(daysRemaining, record?.State, normalized);

            // Usa SimpleName (preenchido via GetNameInfo) em vez de parsear Subject manualmente.
            var holder = ParseHolder(certificate.SimpleName.Length > 0
                ? certificate.SimpleName
                : GetCommonNameFallback(certificate.Subject));

            table.Rows.Add(
                holder.Name,
                FormatDocument(holder.Document),
                certificate.NotAfter,
                daysRemaining,
                statusText,
                thumbprint,
                record?.State == CertificateNotificationState.Dismissed,
                GetStatusCategory(daysRemaining, record?.State, normalized));
        }
    }

    // -------------------------------------------------------------------------
    // Auxiliares de status / estilo
    // -------------------------------------------------------------------------

    // Wrappers para manter call sites legiveis; logica em CertificateStatusHelpers (testavel).
    private static string GetStatusText(int daysRemaining, CertificateNotificationState? state, ExpiryThresholds thresholds) =>
        CertificateStatusHelpers.GetStatusText(daysRemaining, state, thresholds);

    private static string GetStatusCategory(int daysRemaining, CertificateNotificationState? state, ExpiryThresholds thresholds) =>
        CertificateStatusHelpers.GetStatusCategory(daysRemaining, state, thresholds);

    private static void ApplyRowStyles(DataGridView grid)
    {
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.IsNewRow) continue;

            row.DefaultCellStyle.BackColor           = row.Index % 2 == 0 ? Color.White : Color.FromArgb(244, 247, 250);
            row.DefaultCellStyle.ForeColor           = grid.DefaultCellStyle.ForeColor;
            row.DefaultCellStyle.SelectionBackColor  = Color.FromArgb(226, 239, 255);
            row.DefaultCellStyle.SelectionForeColor  = Color.Black;

            // Usa StatusCategory pre-calculado em FillCertificateTable (com thresholds corretos).
            var category = row.Cells["StatusCategory"].Value as string ?? "Valid";
            ApplyRowStyle(row, category);
        }
    }

    private static void ApplyRowStyle(DataGridViewRow row, string statusCategory)
    {
        switch (statusCategory)
        {
            case "Dismissed":
                row.DefaultCellStyle.BackColor          = Color.Gainsboro;
                row.DefaultCellStyle.ForeColor          = Color.DimGray;
                row.DefaultCellStyle.SelectionBackColor = Color.FromArgb(220, 220, 220);
                row.DefaultCellStyle.SelectionForeColor = Color.Black;
                break;
            case "Expired":
                row.DefaultCellStyle.BackColor          = Color.MistyRose;
                row.DefaultCellStyle.SelectionBackColor = Color.FromArgb(244, 205, 201);
                row.DefaultCellStyle.SelectionForeColor = Color.Black;
                break;
            case "Critical":
                row.DefaultCellStyle.BackColor          = Color.Moccasin;
                row.DefaultCellStyle.SelectionBackColor = Color.FromArgb(244, 219, 176);
                row.DefaultCellStyle.SelectionForeColor = Color.Black;
                break;
            case "Warning":
                row.DefaultCellStyle.BackColor          = Color.LemonChiffon;
                row.DefaultCellStyle.SelectionBackColor = Color.FromArgb(241, 232, 168);
                row.DefaultCellStyle.SelectionForeColor = Color.Black;
                break;
            // "Valid" e demais: mantém o estilo zebrado definido em ApplyRowStyles.
        }
    }

    // -------------------------------------------------------------------------
    // Auxiliares de texto e dismiss
    // -------------------------------------------------------------------------

    private static void UpdateDismissButtonText(DataGridView grid, Button dismiss)
    {
        if (grid.SelectedRows.Count == 0)
        {
            dismiss.Text    = "Ignorar certificado";
            dismiss.Enabled = false;
            return;
        }

        dismiss.Enabled = true;
        var isDismissed = grid.SelectedRows[0].DataBoundItem is DataRowView rv &&
                          rv.Row.Field<bool>("Dismissed");
        dismiss.Text = isDismissed ? "Voltar a lembrar" : "Ignorar certificado";
    }

    private static bool TryGetSelectedRowView(DataGridView grid, out DataRowView rowView)
    {
        rowView = null!;
        if (grid.SelectedRows.Count == 0) return false;
        rowView = grid.SelectedRows[0].DataBoundItem as DataRowView ?? null!;
        return rowView is not null;
    }

    // Helpers de parsing delegados para CertificateDocumentHelpers (testavel separadamente).
    private static string GetCommonNameFallback(string dn) =>
        CertificateDocumentHelpers.GetCommonNameFallback(dn);

    private static HolderInfo ParseHolder(string commonName)
    {
        var (name, doc) = CertificateDocumentHelpers.ParseHolder(commonName);
        return new HolderInfo(name, doc);
    }

    private static string FormatDocument(string document) =>
        CertificateDocumentHelpers.FormatDocument(document);

    private sealed record HolderInfo(string Name, string Document);

    // -------------------------------------------------------------------------
    // Win32
    // -------------------------------------------------------------------------

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
