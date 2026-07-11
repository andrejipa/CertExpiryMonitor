using CertExpiryMonitor.Models;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CertExpiryMonitor.Services;

internal static class FallbackNotificationWindow
{
    public static bool Show(
        NotificationPlan plan,
        AppSettings settings,
        Action showDetails,
        FileLogger logger,
        DiagnosticEventStore? diagnosticEvents)
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
                Text = NotificationPresenter.BuildFallbackSummary(plan, settings.Thresholds),
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
            logger.Error(ex, "Failed to show fallback notification");
            diagnosticEvents?.RecordError(ex, "notification.fallback_failed", nameof(FallbackNotificationWindow), "Falha ao exibir popup proprio.");
            return false;
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
