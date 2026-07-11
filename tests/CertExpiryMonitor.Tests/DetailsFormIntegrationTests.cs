using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using System.Windows.Forms;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class DetailsFormIntegrationTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"DetailsFormIntegration_{Guid.NewGuid():N}");

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
            Assert.Contains(controls, control => control.AccessibleName == "Testar popup agora (sem aguardar o horário diário)");
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

            Find<MaskedTextBox>(controls, "Horário do popup diário").Text = "18:45";
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

            Find<Button>(controls, "Testar popup agora (sem aguardar o horário diário)").PerformClick();

            Assert.Equal(1, calls);
            Assert.Contains(controls.OfType<Label>(), label => label.Text == "Popup de teste enviado.");
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
            NotificationTime = TimeSpan.FromHours(9),
            NotificationSoundEnabled = true,
            Thresholds = new ExpiryThresholds().Normalized(),
            DismissCertificate = dismiss ?? (_ => true),
            RestoreCertificate = restore ?? (_ => true),
            RemoveCertificate = _ => true,
            OpenWindowsCertificateStore = () => { },
            SaveSettings = saveSettings ?? (_ => true),
            GetThresholds = () => new ExpiryThresholds().Normalized(),
            TestNotificationNow = testNotification ?? (() => false),
            ReloadCertificates = () => (CertificateReadResult.Complete(certificates), new Dictionary<string, CertificateStateRecord>()),
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
            try { action(); }
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
