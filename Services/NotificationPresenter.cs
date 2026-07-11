using CertExpiryMonitor.Models;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CertExpiryMonitor.Services;

internal sealed record NotificationPresentationResult(bool Shown, AppSettings Settings);

internal sealed class NotificationPresenter
{
    private readonly Func<NotificationPlan, ExpiryThresholds, bool, bool> _tryShowToast;
    private readonly Func<NotificationPlan, AppSettings, Action, bool> _showFallback;
    private readonly JsonSettingsStore _settingsStore;
    private readonly FileLogger _logger;
    private readonly DiagnosticEventStore? _diagnosticEvents;

    public NotificationPresenter(
        ToastNotifierService notifier,
        JsonSettingsStore settingsStore,
        FileLogger logger,
        DiagnosticEventStore? diagnosticEvents = null)
        : this(notifier.Show, settingsStore, logger, diagnosticEvents, showFallback: null)
    {
    }

    internal NotificationPresenter(
        Func<NotificationPlan, ExpiryThresholds, bool, bool> tryShowToast,
        JsonSettingsStore settingsStore,
        FileLogger logger,
        DiagnosticEventStore? diagnosticEvents = null,
        Func<NotificationPlan, AppSettings, Action, bool>? showFallback = null)
    {
        _tryShowToast = tryShowToast;
        _settingsStore = settingsStore;
        _logger = logger;
        _diagnosticEvents = diagnosticEvents;
        _showFallback = showFallback ?? ShowFallbackWindow;
    }

    public NotificationPresentationResult Show(NotificationPlan plan, AppSettings settings, Action showDetails)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(showDetails);

        var currentSettings = NotificationCheckCoordinator.NormalizeSettings(settings);
        var shown = _tryShowToast(
            plan,
            currentSettings.Thresholds.Normalized(),
            currentSettings.NotificationSoundEnabled);
        if (shown)
        {
            _diagnosticEvents?.RecordInfo(
                "notification.shown",
                nameof(NotificationPresenter),
                "Notificacao toast do Windows exibida.",
                new { channel = "windows_toast", due_count = plan.DueCertificates.Count });
            return new NotificationPresentationResult(true, currentSettings);
        }

        _logger.Info("Toast notification was not accepted by Windows; app popup fallback was used.");
        _diagnosticEvents?.RecordWarning(
            "notification.fallback_used",
            nameof(NotificationPresenter),
            "Toast do Windows nao foi aceito; popup proprio sera usado.",
            new { due_count = plan.DueCertificates.Count });

        if (_settingsStore.TryLoad(out var latestSettings))
        {
            currentSettings = NotificationCheckCoordinator.NormalizeSettings(latestSettings);
        }

        shown = _showFallback(plan, currentSettings, showDetails);
        _diagnosticEvents?.RecordInfo(
            shown ? "notification.shown" : "notification.fallback_closed",
            nameof(NotificationPresenter),
            shown ? "Popup proprio exibido e usuario abriu detalhes." : "Popup proprio fechado sem acao efetiva.",
            new { channel = "app_popup", due_count = plan.DueCertificates.Count });
        return new NotificationPresentationResult(shown, currentSettings);
    }

    internal static string BuildFallbackSummary(NotificationPlan plan, ExpiryThresholds thresholds)
    {
        var normalized = thresholds.Normalized();
        var parts = new[]
        {
            FormatCount($"até {normalized.Level30} dias", plan.Count(ExpiryBucket.Days30)),
            FormatCount($"até {normalized.Level15} dias", plan.Count(ExpiryBucket.Days15)),
            FormatCount($"até {normalized.Level7} dias",  plan.Count(ExpiryBucket.Days7)),
            FormatCount($"em {normalized.Level1} dia",    plan.Count(ExpiryBucket.Days1))
        }.Where(part => part.Length > 0);

        return $"{plan.DueCertificates.Count} certificado(s) precisam de atencao.\r\n{string.Join(" | ", parts)}";
    }

    private bool ShowFallbackWindow(NotificationPlan plan, AppSettings settings, Action showDetails)
    {
        try
        {
            if (settings.NotificationSoundEnabled)
            {
                System.Media.SystemSounds.Exclamation.Play();
            }

            using var form = new Form
            {
                Text = "Certificados digitais",
                StartPosition = FormStartPosition.CenterScreen,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                TopMost = true,
                ShowInTaskbar = true,
                ClientSize = new Size(360, 185)
            };
            var title = new Label
            {
                Text = "Certificados próximos do vencimento",
                Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? SystemFonts.DefaultFont.FontFamily, 10F, FontStyle.Bold),
                Location = new Point(18, 16),
                Size = new Size(320, 24)
            };
            var summary = new Label
            {
                Text = BuildFallbackSummary(plan, settings.Thresholds),
                Location = new Point(18, 48),
                Size = new Size(320, 58)
            };
            var close = new Button
            {
                Text = "Fechar aviso",
                Location = new Point(96, 132),
                Size = new Size(116, 32),
                DialogResult = DialogResult.Cancel
            };
            var viewDetails = new Button
            {
                Text = "Ver detalhes",
                Location = new Point(226, 132),
                Size = new Size(116, 32)
            };

            close.Click += (_, _) => { form.DialogResult = DialogResult.Cancel; form.Close(); };
            viewDetails.Click += (_, _) => { form.DialogResult = DialogResult.OK; form.Close(); showDetails(); };
            form.Controls.AddRange([title, summary, close, viewDetails]);
            form.AcceptButton = viewDetails;
            form.CancelButton = close;
            form.Shown += (_, _) =>
            {
                form.WindowState = FormWindowState.Normal;
                form.TopMost = true;
                form.BringToFront();
                form.Activate();
                NativeMethods.SetForegroundWindow(form.Handle);
            };
            return form.ShowDialog() == DialogResult.OK;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to show fallback notification");
            _diagnosticEvents?.RecordError(ex, "notification.fallback_failed", nameof(NotificationPresenter), "Falha ao exibir popup proprio.");
            return false;
        }
    }

    private static string FormatCount(string label, int count) => count == 0 ? string.Empty : $"{count} {label}";

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
