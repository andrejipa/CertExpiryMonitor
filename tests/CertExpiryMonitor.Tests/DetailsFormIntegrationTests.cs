using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using System.Windows.Forms;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class DetailsFormIntegrationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"DetailsFormIntegration_{Guid.NewGuid():N}");

    [Theory]
    [InlineData("00:00")]
    [InlineData("09:00")]
    [InlineData("10:30")]
    [InlineData("12:30")]
    [InlineData("18:45")]
    [InlineData("23:59")]
    public void SavingWithoutEditingPreservesTime(string text)
    {
        RunOnSta(() =>
        {
            DetailsSettingsUpdate? saved = null;
            var expected = TimeSpan.Parse(text);
            using var form = new DetailsForm(CreateOptions(openSettingsTab: true,
                notificationTime: expected, saveSettings: update => { saved = update; return true; }));
            Prepare(form);
            Find<Button>(Descendants(form), "Salvar todas as configurações (horário, som, faixas e avançado)").PerformClick();
            Assert.NotNull(saved);
            Assert.Equal(expected, saved.NotificationTime);
        });
    }

    [Theory]
    [InlineData(580, 450)]
    [InlineData(940, 600)]
    public void SettingsKeepSaveVisibleWithoutHorizontalScroll(int width, int height)
    {
        RunOnSta(() =>
        {
            using var form = new DetailsForm(CreateOptions(openSettingsTab: true));
            form.Size = new System.Drawing.Size(width, height);
            Prepare(form);
            var save = Find<Button>(Descendants(form), "Salvar todas as configurações (horário, som, faixas e avançado)");
            var bounds = form.RectangleToClient(save.RectangleToScreen(save.ClientRectangle));
            Assert.True(form.ClientRectangle.Contains(bounds), $"Salvar fora da janela: {bounds}");
            Assert.All(Descendants(form).OfType<Panel>(), panel => Assert.False(panel.HorizontalScroll.Visible));
            var folder = Environment.GetEnvironmentVariable("CERT_UI_TEST_CAPTURE");
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
                using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                bitmap.Save(Path.Combine(folder, $"settings-{width}.png"));
            }
        });
    }

    [Theory]
    [InlineData(580, 450, 2)]
    [InlineData(940, 600, 1)]
    public void CertificateCardsFitAndFollowVisualTabOrder(int width, int height, int rows)
    {
        RunOnSta(() =>
        {
            using var form = new DetailsForm(CreateOptions());
            form.Size = new System.Drawing.Size(width, height);
            Prepare(form);
            var cards = Descendants(form).OfType<Button>().Where(x => x.Name is "Expired" or "Critical" or "Warning" or "Valid" or "Dismissed").ToArray();
            Assert.Equal(5, cards.Length);
            Assert.Equal(rows, cards.Select(x => x.Top).Distinct().Count());
            Assert.Equal([0, 1, 2, 3, 4], cards.Select(x => x.TabIndex));
            Assert.All(cards, card => Assert.True(card.Parent!.ClientRectangle.Contains(card.Bounds)));
            var folder = Environment.GetEnvironmentVariable("CERT_UI_TEST_CAPTURE");
            if (!string.IsNullOrEmpty(folder))
            {
                Directory.CreateDirectory(folder);
                using var bitmap = new System.Drawing.Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                bitmap.Save(Path.Combine(folder, $"certificates-{width}.png"));
            }
        });
    }

    [Theory]
    [InlineData("12:__")]
    [InlineData("__:30")]
    [InlineData("24:00")]
    [InlineData("12:60")]
    public void SaveButtonRejectsIncompleteOrInvalidTimeAndFocusesField(string text)
    {
        RunOnSta(() =>
        {
            var calls = 0;
            using var form = new DetailsForm(CreateOptions(openSettingsTab: true,
                saveSettings: _ => { calls++; return true; }));
            Prepare(form);
            var controls = Descendants(form).ToArray();
            var time = Find<MaskedTextBox>(controls, "Horário do aviso diário");
            time.Text = text;
            Find<Button>(controls, "Salvar todas as configurações (horário, som, faixas e avançado)").PerformClick();
            Assert.Equal(0, calls);
            Assert.True(time.Focused);
            Assert.Contains(controls.OfType<Label>(), x => x.Text.Contains("Informe um horário válido"));
        });
    }

    [Theory]
    [InlineData("som")]
    [InlineData("faixas")]
    [InlineData("avancado")]
    public void EditingOtherPreferencesPreservesLoadedTime(string preference)
    {
        RunOnSta(() =>
        {
            DetailsSettingsUpdate? saved = null;
            using var form = new DetailsForm(CreateOptions(openSettingsTab: true,
                notificationTime: new TimeSpan(10, 30, 0), saveSettings: update => { saved = update; return true; }));
            Prepare(form);
            var controls = Descendants(form).ToArray();
            if (preference == "som") Find<CheckBox>(controls, "Tocar som ao mostrar o aviso").Checked = false;
            if (preference == "faixas") Find<NumericUpDown>(controls, "Faixa longa em dias").Value = 45;
            if (preference == "avancado") Find<CheckBox>(controls, "Gravar logs em formato JSON estruturado").Checked = true;
            Find<Button>(controls, "Salvar todas as configurações (horário, som, faixas e avançado)").PerformClick();
            Assert.NotNull(saved);
            Assert.Equal(new TimeSpan(10, 30, 0), saved.NotificationTime);
            if (preference == "som") Assert.False(saved.NotificationSoundEnabled);
            if (preference == "faixas") Assert.Equal(45, saved.Thresholds.Level30);
            if (preference == "avancado") Assert.Equal(LogFormat.Json, saved.LogFormat);
        });
    }

    [Fact]
    public void PartialReadBannerPersistsAfterDismissAndClearsOnlyAfterCompleteReload()
    {
        RunOnSta(() =>
        {
            var options = CreateOptions(certificateReadStatus: CertificateReadStatus.PartialFailure);
            using var form = new DetailsForm(options);
            Prepare(form);
            var controls = Descendants(form).ToArray();
            var banner = Find<Label>(controls, "Estado da leitura de certificados");
            var grid = Find<DataGridView>(controls, "Lista de certificados A1 do usuario atual");
            grid.CurrentCell = grid.Rows[0].Cells[0]; grid.Rows[0].Selected = true;
            Find<Button>(controls, "Ignorar ou restaurar o certificado selecionado").PerformClick();
            Assert.True(banner.Visible);
            Assert.Contains("Lista incompleta", banner.Text);
            var reload = Find<Button>(controls, "Atualizar lista de certificados");
            reload.PerformClick();
            var timeout = DateTime.UtcNow.AddSeconds(5);
            while (!reload.Enabled && DateTime.UtcNow < timeout)
            { Application.DoEvents(); Thread.Sleep(10); }
            Assert.True(reload.Enabled);
            Assert.Equal(CertificateReadStatus.Success, options.CertificateReadStatus);
            Assert.False(banner.Visible);
        });
    }

    [Fact]
    public void SavingCustomThresholdsUpdatesCardsAndPreservesActiveFilterAndSearch()
    {
        RunOnSta(() =>
        {
            var options = CreateOptions();
            options.Thresholds = new ExpiryThresholds { Level1 = 1, Level7 = 20, Level15 = 25, Level30 = 40 };
            using var form = new DetailsForm(options);
            Prepare(form);
            var controls = Descendants(form).ToArray();
            var search = Find<TextBox>(controls, "Buscar por titular ou documento");
            search.Text = "Portal";
            var critical = controls.OfType<Button>().Single(x => x.Name == "Critical");
            Assert.Contains("Até 20 dias", critical.Text);
            critical.PerformClick();
            var grid = Find<DataGridView>(controls, "Lista de certificados A1 do usuario atual");
            Assert.Single(grid.Rows.Cast<DataGridViewRow>());
            var tabs = controls.OfType<TabControl>().Single();
            tabs.SelectedIndex = 1;
            Find<NumericUpDown>(controls, "Faixa curta em dias").Value = 7;
            Find<Button>(controls, "Salvar todas as configurações (horário, som, faixas e avançado)").PerformClick();
            tabs.SelectedIndex = 0;
            Assert.Empty(grid.Rows.Cast<DataGridViewRow>());
            Assert.Equal("Portal", search.Text);
            Assert.Equal(3, Find<ComboBox>(controls, "Filtro de exibição de certificados").SelectedIndex);
            Assert.Contains("Até 7 dias", critical.Text);
            var warning = controls.OfType<Button>().Single(x => x.Name == "Warning");
            Assert.Contains("8 a 40 dias", warning.Text);
            warning.PerformClick();
            Assert.Single(grid.Rows.Cast<DataGridViewRow>());
        });
    }

    [Fact]
    public void RestoreAfterRecoveryUsesFreshThresholds()
    {
        RunOnSta(() =>
        {
            var options = CreateOptions(openSettingsTab: true);
            options.SettingsAvailable = false;
            var certificate = options.Certificates[0];
            options.ReloadCertificates = () => new DetailsLoadResult(CertificateReadResult.Complete([certificate]),
                new Dictionary<string, CertificateStateRecord> { [certificate.Thumbprint] = new() { Thumbprint = certificate.Thumbprint, State = CertificateNotificationState.Dismissed } },
                new AppSettings { Thresholds = new ExpiryThresholds { Level1 = 1, Level7 = 20, Level15 = 25, Level30 = 40 } }, true);
            using var form = new DetailsForm(options);
            Prepare(form);
            Find<Button>(Descendants(form), "Tentar carregar as configurações novamente").PerformClick();
            var timeout = DateTime.UtcNow.AddSeconds(5);
            while (!Descendants(form).OfType<MaskedTextBox>().Any() && DateTime.UtcNow < timeout)
            { Application.DoEvents(); Thread.Sleep(10); }
            Descendants(form).OfType<TabControl>().Single().SelectedIndex = 0;
            var grid = Find<DataGridView>(Descendants(form), "Lista de certificados A1 do usuario atual");
            grid.CurrentCell = grid.Rows[0].Cells[0]; grid.Rows[0].Selected = true;
            Find<Button>(Descendants(form), "Restaurar avisos do certificado selecionado").PerformClick();
            Assert.Equal("Critical", Convert.ToString(grid.Rows[0].Cells["StatusCategory"].Value));
        });
    }

    [Fact]
    public void RetryRestoresEditorUsingFreshSettingsAndCertificateData()
    {
        RunOnSta(() =>
        {
            var options = CreateOptions(openSettingsTab: true);
            options.SettingsAvailable = false;
            options.StateAvailable = false;
            options.ReloadCertificates = () => new DetailsLoadResult(CertificateReadResult.Complete([]),
                new Dictionary<string, CertificateStateRecord>(), new AppSettings { DailyCheckTime = new TimeSpan(12, 30, 0) }, true);
            using var form = new DetailsForm(options);
            Prepare(form);
            Find<Button>(Descendants(form), "Tentar carregar as configurações novamente").PerformClick();
            var timeout = DateTime.UtcNow.AddSeconds(5);
            while (!Descendants(form).OfType<MaskedTextBox>().Any() && DateTime.UtcNow < timeout)
            { Application.DoEvents(); Thread.Sleep(10); }
            Assert.Equal("12:30", Find<MaskedTextBox>(Descendants(form), "Horário do aviso diário").Text);
            Assert.Empty(Find<DataGridView>(Descendants(form), "Lista de certificados A1 do usuario atual").Rows.Cast<DataGridViewRow>());
            Assert.True(options.StateAvailable);
        });
    }

    [Fact]
    public void CardsSelectExactCategoryAndKeepSearch()
    {
        RunOnSta(() =>
        {
            using var form = new DetailsForm(CreateOptions());
            Prepare(form);
            var controls = Descendants(form).ToArray();
            var search = Find<TextBox>(controls, "Buscar por titular ou documento");
            search.Text = "Portal";
            controls.OfType<Button>().Single(x => x.Name == "Critical").PerformClick();
            var grid = Find<DataGridView>(controls, "Lista de certificados A1 do usuario atual");
            Assert.Empty(grid.Rows.Cast<DataGridViewRow>());
            controls.OfType<Button>().Single(x => x.Name == "Warning").PerformClick();
            Assert.Single(grid.Rows.Cast<DataGridViewRow>());
            Assert.Equal("Portal", search.Text);
        });
    }

    [Fact]
    public void UnavailableSettingsHideEditorAndUnavailableStateBlocksActions()
    {
        RunOnSta(() =>
        {
            var options = CreateOptions(openSettingsTab: true);
            options.SettingsAvailable = false;
            options.StateAvailable = false;
            using var form = new DetailsForm(options);
            Prepare(form);
            Assert.Empty(Descendants(form).OfType<MaskedTextBox>());
            Assert.Contains(Descendants(form), x => x.AccessibleName == "Tentar carregar as configurações novamente");
            var tabs = Descendants(form).OfType<TabControl>().Single();
            tabs.SelectedIndex = 0;
            var controls = Descendants(form).ToArray();
            var grid = Find<DataGridView>(controls, "Lista de certificados A1 do usuario atual");
            grid.CurrentCell = grid.Rows[0].Cells[0]; grid.Rows[0].Selected = true;
            Assert.False(Find<Button>(controls, "Remover certificados selecionados").Enabled);
            Assert.False(Find<Button>(controls, "Ignorar ou restaurar o certificado selecionado").Enabled);
        });
    }

    [Fact]
    public void FailedReadNeverClaimsNoCertificatesFound()
    {
        RunOnSta(() =>
        {
            var options = CreateOptions(certificateReadStatus: CertificateReadStatus.StoreFailure);
            options.Certificates = [];
            using var form = new DetailsForm(options);
            Prepare(form);
            Assert.DoesNotContain(Descendants(form).OfType<Label>(), x => x.Text.Contains("Nenhum certificado A1 encontrado"));
            Assert.Contains(Descendants(form).OfType<Label>(), x => x.Text.Contains("Não foi possível consultar"));
        });
    }

    [Fact]
    public void ConstructionBuildsBothTabsAndAccessiblePrimaryControls()
    {
        RunOnSta(() =>
        {
            using var form = new DetailsForm(CreateOptions(certificateReadStatus: CertificateReadStatus.PartialFailure));
            Prepare(form);
            var controls = Descendants(form).ToArray();

            var tabs = Assert.IsType<TabControl>(form.Controls.Cast<Control>().Single(control => control is TabControl));
            Assert.Equal(["Certificados", "Configurações"], tabs.TabPages.Cast<TabPage>().Select(tab => tab.Text));
            Assert.Contains(controls, control => control.AccessibleName == "Lista de certificados A1 do usuario atual");
            Assert.Contains(controls, control => control.AccessibleName == "Atualizar lista de certificados");
            Assert.Contains(controls, control => control.AccessibleName == "Testar aviso agora (sem aguardar o horário diário)");
            Assert.Contains(controls.OfType<Label>(), label => label.Text.Contains("incompleta", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact]
    public void SaveSettingsNormalizesValuesAndReportsSuccess()
    {
        RunOnSta(() =>
        {
            DetailsSettingsUpdate? saved = null;
            var options = CreateOptions(openSettingsTab: true, saveSettings: update => { saved = update; return true; });
            using var form = new DetailsForm(options);
            Prepare(form);
            var controls = Descendants(form).ToArray();

            Find<MaskedTextBox>(controls, "Horário do aviso diário").Text = "18:45";
            Find<NumericUpDown>(controls, "Faixa longa em dias").Value = 5;
            Find<NumericUpDown>(controls, "Faixa média em dias").Value = 40;
            Find<NumericUpDown>(controls, "Faixa curta em dias").Value = 20;
            Find<NumericUpDown>(controls, "Urgente em dias").Value = 10;
            Find<CheckBox>(controls, "Tocar som ao mostrar o aviso").Checked = false;
            Find<CheckBox>(controls, "Gravar logs em formato JSON estruturado").Checked = true;
            Find<CheckBox>(controls, "Espelhar erros no Windows Event Log").Checked = true;
            Find<CheckBox>(controls, "Coletar estatísticas anônimas locais").Checked = true;

            Find<Button>(controls, "Salvar todas as configurações (horário, som, faixas e avançado)").PerformClick();

            Assert.NotNull(saved);
            Assert.Equal(new TimeSpan(18, 45, 0), saved.NotificationTime);
            Assert.False(saved.NotificationSoundEnabled);
            Assert.Equal(LogFormat.Json, saved.LogFormat);
            Assert.True(saved.EventLogEnabled);
            Assert.True(saved.TelemetryEnabled);
            var normalized = saved.Thresholds.Normalized();
            Assert.Equal(normalized.Level1, saved.Thresholds.Level1);
            Assert.Equal(normalized.Level7, saved.Thresholds.Level7);
            Assert.Equal(normalized.Level15, saved.Thresholds.Level15);
            Assert.Equal(normalized.Level30, saved.Thresholds.Level30);
            Assert.Contains(controls.OfType<Label>(), label => label.Text.StartsWith("Configurações salvas:", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void TestPopupInvokesCallbackAndUpdatesFeedback()
    {
        RunOnSta(() =>
        {
            var calls = 0;
            using var form = new DetailsForm(CreateOptions(openSettingsTab: true, testNotification: () => { calls++; return true; }));
            Prepare(form);
            var controls = Descendants(form).ToArray();

            Find<Button>(controls, "Testar aviso agora (sem aguardar o horário diário)").PerformClick();

            Assert.Equal(1, calls);
            Assert.Contains(controls.OfType<Label>(), label => label.Text == "Aviso de teste enviado.");
        });
    }

    [Fact]
    public void SearchAndCategoryFiltersChangeVisibleRows()
    {
        RunOnSta(() =>
        {
            using var form = new DetailsForm(CreateOptions());
            Prepare(form);
            var controls = Descendants(form).ToArray();
            var grid = Find<DataGridView>(controls, "Lista de certificados A1 do usuario atual");
            var search = Find<TextBox>(controls, "Buscar por titular ou documento");
            var category = Find<ComboBox>(controls, "Filtro de exibição de certificados");

            Assert.Equal(2, grid.Rows.Count);
            search.Text = "Pioneira";
            Application.DoEvents();
            Assert.Single(grid.Rows.Cast<DataGridViewRow>());

            search.Text = string.Empty;
            category.SelectedItem = "A vencer";
            Application.DoEvents();
            Assert.Single(grid.Rows.Cast<DataGridViewRow>());
        });
    }

    [Fact]
    public void DismissAndRestoreButtonUsesSelectedCertificateAndUpdatesRow()
    {
        RunOnSta(() =>
        {
            var dismissed = new List<string>();
            var restored = new List<string>();
            using var form = new DetailsForm(CreateOptions(
                dismiss: thumbprint => { dismissed.Add(thumbprint); return true; },
                restore: thumbprint => { restored.Add(thumbprint); return true; }));
            Prepare(form);
            var controls = Descendants(form).ToArray();
            var grid = Find<DataGridView>(controls, "Lista de certificados A1 do usuario atual");
            var button = Find<Button>(controls, "Ignorar ou restaurar o certificado selecionado");

            grid.Rows[0].Selected = true;
            grid.CurrentCell = grid.Rows[0].Cells[0];
            button.PerformClick();
            Assert.Single(dismissed);
            Assert.Equal("Ignorado", Convert.ToString(grid.Rows[0].Cells["Status"].Value));

            grid.Rows[0].Selected = true;
            grid.CurrentCell = grid.Rows[0].Cells[0];
            button.PerformClick();
            Assert.Single(restored);
            Assert.NotEqual("Ignorado", Convert.ToString(grid.Rows[0].Cells["Status"].Value));
        });
    }

    private DetailsFormOptions CreateOptions(
        bool openSettingsTab = false,
        TimeSpan? notificationTime = null,
        CertificateReadStatus certificateReadStatus = CertificateReadStatus.Success,
        Func<DetailsSettingsUpdate, bool>? saveSettings = null,
        Func<bool>? testNotification = null,
        Func<string, bool>? dismiss = null,
        Func<string, bool>? restore = null)
    {
        var logger = new FileLogger(new AppPaths(_tempDir));
        var certificates = new[]
        {
            Certificate("AA", "CN=Portal Comercio", DateTime.Today.AddDays(13)),
            Certificate("BB", "CN=Pioneira Alimentos", DateTime.Today.AddDays(215))
        };
        return new DetailsFormOptions
        {
            Certificates = certificates,
            CertificateReadStatus = certificateReadStatus,
            State = new Dictionary<string, CertificateStateRecord>(StringComparer.OrdinalIgnoreCase),
            NotificationTime = notificationTime ?? TimeSpan.FromHours(9),
            NotificationSoundEnabled = true,
            Thresholds = new ExpiryThresholds().Normalized(),
            DismissCertificate = dismiss ?? (_ => true),
            RestoreCertificate = restore ?? (_ => true),
            RemoveCertificate = _ => true,
            OpenWindowsCertificateStore = () => { },
            SaveSettings = saveSettings ?? (_ => true),
            GetThresholds = () => new ExpiryThresholds().Normalized(),
            TestNotificationNow = testNotification ?? (() => false),
            ReloadCertificates = () => new DetailsLoadResult(CertificateReadResult.Complete(certificates), new Dictionary<string, CertificateStateRecord>(), new AppSettings(), true),
            Logger = logger,
            OpenSettingsTab = openSettingsTab,
            LogFormat = LogFormat.Text,
            EventLogEnabled = false,
            TelemetryEnabled = false
        };
    }

    private static CertificateSnapshot Certificate(string thumbprint, string subject, DateTime notAfter) =>
        new(thumbprint, subject, "CN=Issuer", notAfter, $"SERIAL-{thumbprint}", subject[3..]);

    private static void Prepare(Form form)
    {
        form.ShowInTaskbar = false;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new System.Drawing.Point(-32_000, -32_000);
        form.Show();
        Application.DoEvents();
    }

    private static T Find<T>(IEnumerable<Control> controls, string accessibleName) where T : Control =>
        Assert.IsType<T>(controls.Single(control => control.AccessibleName == accessibleName));

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        using var completed = new ManualResetEventSlim(false);
        var thread = new Thread(() =>
        {
            try { SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext()); action(); }
            catch (Exception ex) { failure = ex; }
            finally { completed.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(completed.Wait(TimeSpan.FromSeconds(20)), "Teste WinForms excedeu 20 segundos.");
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException("Falha na thread STA.", failure);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }
}
