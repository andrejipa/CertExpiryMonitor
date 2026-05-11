using CertExpiryMonitor.Models;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CertExpiryMonitor.Services;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly EventWaitHandle _activationEvent;
    private readonly EventWaitHandle _configurationEvent;
    private readonly JsonSettingsStore _settingsStore;
    private readonly JsonStateStore _stateStore;
    private readonly CertificateReader _certificateReader;
    private readonly ExpiryEvaluator _expiryEvaluator;
    private readonly CertificateCheckService _checkService;
    private readonly ToastNotifierService _notifier;
    private readonly StartupRegistration _startup;
    private readonly TelemetryService _telemetry;
    private readonly FileLogger _logger;
    private readonly AppPaths _paths;
    private readonly SynchronizationContext _uiContext;
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _activationTimer;
    private AppSettings _settings;
    private DetailsForm? _detailsForm;
    private bool _forceNextScheduledNotification;

    public TrayApplicationContext(
        string[] args,
        EventWaitHandle activationEvent,
        EventWaitHandle configurationEvent,
        JsonSettingsStore settingsStore,
        JsonStateStore stateStore,
        CertificateReader certificateReader,
        ExpiryEvaluator expiryEvaluator,
        CertificateCheckService checkService,
        ToastNotifierService notifier,
        StartupRegistration startup,
        TelemetryService telemetry,
        FileLogger logger,
        AppPaths paths)
    {
        _activationEvent   = activationEvent;
        _configurationEvent = configurationEvent;
        _settingsStore     = settingsStore;
        _stateStore        = stateStore;
        _certificateReader = certificateReader;
        _expiryEvaluator   = expiryEvaluator;
        _checkService      = checkService;
        _notifier          = notifier;
        _startup           = startup;
        _telemetry         = telemetry;
        _logger            = logger;
        _paths             = paths;
        _uiContext         = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _settings          = NormalizeSettings(_settingsStore.Load());
        _telemetry.Enabled = _settings.TelemetryEnabled;

        _notifyIcon = BuildNotifyIcon();
        UpdateTrayTooltip();

        _timer = new System.Windows.Forms.Timer();
        _timer.Tick += (_, _) => OnTimerTick();

        _activationTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _activationTimer.Tick += (_, _) => HandleActivationRequests();
        _activationTimer.Start();

        _notifier.Activated += (_, eventArgs) =>
            _uiContext.Post(_ => HandleToastAction(eventArgs.Arguments), null);

        if (args.Any(arg => arg.Equals("--configure", StringComparison.OrdinalIgnoreCase)))
        {
            ShowTimeConfiguration();
        }
        else if (args.Any(arg => arg.Equals("--details", StringComparison.OrdinalIgnoreCase)))
        {
            ShowDetails();
        }

        ScheduleInitialCheck();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _activationTimer.Dispose();
            _timer.Dispose();
            _detailsForm?.Dispose();
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }

        base.Dispose(disposing);
    }

    // -------------------------------------------------------------------------
    // Tray icon e menu
    // -------------------------------------------------------------------------

    private NotifyIcon BuildNotifyIcon()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("Executar verificação agora", null,
            (_, _) => SafeExecute(
                () => RunCheck(ignoreConfiguredTime: true, ignoreLastCheckDate: true, showNoAlertFeedback: true),
                "Manual check failed"));

        menu.Items.Add("Abrir configuracoes", null,
            (_, _) => SafeExecute(() => ShowDetails(openSettingsTab: true), "Failed to open settings"));

        menu.Items.Add("Ver certificados monitorados", null,
            (_, _) => SafeExecute(() => ShowDetails(), "Failed to open certificate details"));

        menu.Items.Add(new ToolStripSeparator());

        var startupItem = new ToolStripMenuItem("Iniciar com Windows")
        {
            Checked = _settings.StartupEnabled,
            CheckOnClick = false
        };
        startupItem.Click += (sender, _) =>
            SafeExecute(() => ToggleStartup(sender), "Failed to toggle startup");
        menu.Items.Add(startupItem);

        menu.Items.Add("Abrir pasta de logs", null,
            (_, _) => SafeExecute(OpenLogsFolder, "Failed to open logs folder"));

        menu.Items.Add("Ver estatísticas de uso...", null,
            (_, _) => SafeExecute(ShowTelemetryWindow, "Failed to open telemetry window"));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => ExitThread());

        var icon = new NotifyIcon
        {
            Icon             = AppIcon.Current,
            Text             = "Monitor de Certificados A1",
            ContextMenuStrip = menu,
            Visible          = true
        };

        icon.DoubleClick += (_, _) =>
            SafeExecute(() => ShowDetails(), "Failed to open certificate details");

        return icon;
    }

    private void OpenLogsFolder()
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName        = _paths.RootDirectory,
            UseShellExecute = true
        });
    }

    private void ShowTelemetryWindow()
    {
        using var window = new TelemetryWindow(_telemetry);
        window.ShowDialog();
    }

    // -------------------------------------------------------------------------
    // Agendamento do timer
    // -------------------------------------------------------------------------

    private void ScheduleInitialCheck()
    {
        var delay = TimeSpan.FromMinutes(Math.Clamp(_settings.InitialDelayMinutes, 1, 60));
        ScheduleTimer(delay);
    }

    private void OnTimerTick()
    {
        try
        {
            _timer.Stop();
            RunCheck(ignoreConfiguredTime: false, ignoreLastCheckDate: false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Scheduled check failed");
        }
        finally
        {
            ScheduleNextDailyCheck();
        }
    }

    private void ScheduleNextDailyCheck()
    {
        ScheduleNextDailyCheck(allowImmediateToday: false);
    }

    private void ScheduleNextDailyCheck(bool allowImmediateToday)
    {
        var now  = DateTime.Now;
        var next = now.Date.Add(_settings.DailyCheckTime);

        if (allowImmediateToday && next <= now && next >= now.AddMinutes(-1))
        {
            ScheduleTimer(TimeSpan.FromSeconds(1));
            return;
        }

        if (next <= now)
        {
            next = next.AddDays(1);
        }

        ScheduleTimer(next - now);
    }

    private void ScheduleTimer(TimeSpan delay)
    {
        var milliseconds   = (int)Math.Clamp(delay.TotalMilliseconds, 1000, int.MaxValue);
        _timer.Interval    = milliseconds;
        _timer.Start();
    }

    // -------------------------------------------------------------------------
    // Logica de verificacao (delega ao CertificateCheckService)
    // -------------------------------------------------------------------------

    private void RunCheck(bool ignoreConfiguredTime, bool ignoreLastCheckDate, bool showNoAlertFeedback = false)
    {
        _settings = NormalizeSettings(_settingsStore.Load());

        var (ran, plan) = _checkService.RunCheck(
            ignoreConfiguredTime,
            ignoreLastCheckDate,
            _forceNextScheduledNotification,
            _settings);

        _forceNextScheduledNotification = false;

        // Telemetria: contagem total + skip vs executou + manual (showNoAlertFeedback=true significa botao manual)
        _telemetry.Increment(t =>
        {
            t.TotalChecks++;
            if (!ran) t.ChecksSkipped++;
            if (showNoAlertFeedback) t.ManualChecks++;
        });

        if (!ran) return;

        _settingsStore.Save(_settings);
        UpdateTrayTooltip();

        if (plan is not null)
        {
            _telemetry.Increment(t => t.ChecksWithPlan++);
            var shown = ShowNotification(plan);
            if (shown)
            {
                _telemetry.Increment(t => t.NotificationsShown++);
                _checkService.MarkNotified(plan, _settings.Thresholds.Normalized());
            }
            else
            {
                _telemetry.Increment(t => t.NotificationFailures++);
            }
        }
        else if (showNoAlertFeedback)
        {
            _notifyIcon.ShowBalloonTip(
                3000,
                "Monitor de Certificados A1",
                "Nenhum certificado próximo do vencimento.",
                ToolTipIcon.Info);
        }
    }

    // -------------------------------------------------------------------------
    // Notificacoes
    // -------------------------------------------------------------------------

    private bool ShowNotification(NotificationPlan plan)
    {
        var thresholds = _settings.Thresholds.Normalized();
        var shown = _notifier.Show(plan, thresholds);
        if (!shown)
        {
            shown = ShowFallbackWindow(plan);
        }

        return shown;
    }

    private bool ShowFallbackWindow(NotificationPlan plan)
    {
        try
        {
            _settings = NormalizeSettings(_settingsStore.Load());
            if (_settings.NotificationSoundEnabled)
            {
                System.Media.SystemSounds.Exclamation.Play();
            }

            using var form = new Form
            {
                Text             = "Certificados digitais",
                StartPosition    = FormStartPosition.CenterScreen,
                FormBorderStyle  = FormBorderStyle.FixedDialog,
                MaximizeBox      = false,
                MinimizeBox      = false,
                TopMost          = true,
                ShowInTaskbar    = true,
                ClientSize       = new Size(360, 185)
            };

            var title = new Label
            {
                Text     = "Certificados próximos do vencimento",
                Font     = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? SystemFonts.DefaultFont.FontFamily, 10F, FontStyle.Bold),
                Location = new Point(18, 16),
                Size     = new Size(320, 24)
            };

            var summary = new Label
            {
                Text     = BuildFallbackSummary(plan),
                Location = new Point(18, 48),
                Size     = new Size(320, 58)
            };

            var ignoreAll = new Button
            {
                Text         = "Ignorar agora",
                Location     = new Point(96, 132),
                Size         = new Size(116, 32),
                DialogResult = DialogResult.Cancel
            };

            var viewDetails = new Button
            {
                Text     = "Ver detalhes",
                Location = new Point(226, 132),
                Size     = new Size(116, 32)
            };

            ignoreAll.Click += (_, _) => { form.DialogResult = DialogResult.OK; form.Close(); };
            viewDetails.Click += (_, _) => { form.DialogResult = DialogResult.OK; form.Close(); ShowDetails(); };

            form.Controls.AddRange([title, summary, ignoreAll, viewDetails]);
            form.AcceptButton = viewDetails;
            form.CancelButton = ignoreAll;
            form.Shown += (_, _) =>
            {
                form.WindowState = FormWindowState.Normal;
                form.TopMost     = true;
                form.BringToFront();
                form.Activate();
                NativeMethods.SetForegroundWindow(form.Handle);
            };
            form.ShowDialog();
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to show fallback notification");
            return false;
        }
    }

    private string BuildFallbackSummary(NotificationPlan plan)
    {
        var thresholds = _settings.Thresholds.Normalized();
        var parts = new[]
        {
            FormatCount($"até {thresholds.Level30} dias", plan.Count(ExpiryBucket.Days30)),
            FormatCount($"até {thresholds.Level15} dias", plan.Count(ExpiryBucket.Days15)),
            FormatCount($"até {thresholds.Level7} dias",  plan.Count(ExpiryBucket.Days7)),
            FormatCount($"em {thresholds.Level1} dia",    plan.Count(ExpiryBucket.Days1))
        }.Where(p => p.Length > 0);

        return $"{plan.DueCertificates.Count} certificado(s) precisam de atencao.\r\n{string.Join(" | ", parts)}";
    }

    private static string FormatCount(string label, int count) =>
        count == 0 ? string.Empty : $"{count} {label}";

    // -------------------------------------------------------------------------
    // Acoes do toast
    // -------------------------------------------------------------------------

    private void HandleToastAction(string arguments)
    {
        var values = ParseArguments(arguments);
        var action = values.GetValueOrDefault("action", "view-details");

        switch (action)
        {
            case "configure-time":
                SafeExecute(ShowTimeConfiguration, "Failed to open settings from toast");
                break;
            case "dismiss-one":
                if (values.TryGetValue("thumbprint", out var thumbprint))
                {
                    SafeExecute(() => DismissOne(thumbprint), "Failed to dismiss certificate from toast");
                }
                break;
            case "dismiss-all":
                if (values.TryGetValue("thumbprints", out var thumbprints))
                {
                    SafeExecute(
                        () => DismissAll(thumbprints.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
                        "Failed to dismiss certificates from toast");
                }
                else
                {
                    SafeExecute(DismissAllCurrent, "Failed to dismiss current certificates");
                }
                break;
            case "view-details":
                SafeExecute(() => ShowDetails(), "Failed to open details from toast");
                break;
            case "remind-later":
            default:
                break;
        }
    }

    private void DismissOne(string thumbprint)
    {
        var state = _stateStore.Load();
        _expiryEvaluator.DismissCertificate(thumbprint, state);
        _stateStore.Save(state);
        _telemetry.Increment(t => t.DismissOne++);
    }

    private void DismissAllCurrent()
    {
        var lastPlan = _checkService.LastPlan;
        if (lastPlan is null) return;

        var state = _stateStore.Load();
        _expiryEvaluator.DismissCertificates(
            lastPlan.DueCertificates.Select(item => item.Certificate.Thumbprint),
            state);
        _stateStore.Save(state);
        _telemetry.Increment(t => t.DismissAll++);
    }

    private void DismissAll(IEnumerable<string> thumbprints)
    {
        var state = _stateStore.Load();
        _expiryEvaluator.DismissCertificates(thumbprints, state);
        _stateStore.Save(state);
        _telemetry.Increment(t => t.DismissAll++);
    }

    // -------------------------------------------------------------------------
    // Janela de detalhes
    // -------------------------------------------------------------------------

    private void ShowTimeConfiguration() => ShowDetails(openSettingsTab: true);

    private void ShowDetails(bool openSettingsTab = false)
    {
        if (_detailsForm is { IsDisposed: false })
        {
            _detailsForm.FocusExisting(openSettingsTab);
            return;
        }

        var state        = _stateStore.Load();
        var certificates = _certificateReader.ReadCurrentUserPersonalCertificates();

        _detailsForm = new DetailsForm(new DetailsFormOptions
        {
            Certificates            = certificates,
            State                   = state,
            NotificationTime        = _settings.DailyCheckTime,
            NotificationSoundEnabled = _settings.NotificationSoundEnabled,
            Thresholds              = _settings.Thresholds.Normalized(),
            DismissCertificate      = DismissOne,
            RestoreCertificate      = RestoreOne,
            RemoveExpiredCertificate = RemoveExpiredCertificate,
            SaveNotificationTime    = SaveNotificationTime,
            SaveNotificationSound   = SaveNotificationSound,
            SaveThresholds          = SaveThresholds,
            GetThresholds           = () => _settings.Thresholds.Normalized(),
            TestNotificationNow     = TestNotificationNow,
            ReloadCertificates      = LoadCertificateDetails,
            Logger                  = _logger,
            OpenSettingsTab         = openSettingsTab,
            LogFormat               = _settings.LogFormat,
            EventLogEnabled         = _settings.EventLogEnabled,
            TelemetryEnabled        = _settings.TelemetryEnabled,
            SaveAdvancedSettings    = SaveAdvancedSettings
        });

        _detailsForm.FormClosed += (_, _) => _detailsForm = null;
        _detailsForm.Show();
        _detailsForm.FocusExisting(openSettingsTab);
    }

    private (IReadOnlyList<CertificateSnapshot>, IReadOnlyDictionary<string, CertificateStateRecord>) LoadCertificateDetails()
    {
        return (
            _certificateReader.ReadCurrentUserPersonalCertificates(),
            _stateStore.Load());
    }

    // -------------------------------------------------------------------------
    // Acoes do menu / callbacks do DetailsForm
    // -------------------------------------------------------------------------

    private void ToggleStartup(object? sender)
    {
        _settings.StartupEnabled = !_settings.StartupEnabled;
        if (_settings.StartupEnabled) _startup.EnsureRegistered();
        else _startup.Remove();

        _settingsStore.Save(_settings);
        if (sender is ToolStripMenuItem item)
        {
            item.Checked = _settings.StartupEnabled;
        }
    }

    private void RestoreOne(string thumbprint)
    {
        var state = _stateStore.Load();
        _expiryEvaluator.RestoreCertificate(thumbprint, state);
        _stateStore.Save(state);
        _telemetry.Increment(t => t.Restore++);
    }

    private bool RemoveExpiredCertificate(string thumbprint) =>
        _certificateReader.RemoveFromCurrentUserPersonalStore(thumbprint);

    private void SaveNotificationTime(TimeSpan notificationTime)
    {
        _settings = NormalizeSettings(_settingsStore.Load());
        var currentMinute  = new TimeSpan(DateTime.Now.Hour, DateTime.Now.Minute, 0);
        var selectedMinute = new TimeSpan(notificationTime.Hours, notificationTime.Minutes, 0);
        _settings.DailyCheckTime  = selectedMinute;
        var shouldRunAgainToday   = selectedMinute >= currentMinute;
        if (shouldRunAgainToday)
        {
            _settings.LastCheckDate             = null;
            _forceNextScheduledNotification     = true;
        }

        _settingsStore.Save(_settings);
        ScheduleNextDailyCheck(allowImmediateToday: shouldRunAgainToday);
        _telemetry.Increment(t => t.ScheduleChanged++);
    }

    private void SaveNotificationSound(bool enabled)
    {
        _settings = NormalizeSettings(_settingsStore.Load());
        _settings.NotificationSoundEnabled = enabled;
        _settingsStore.Save(_settings);
    }

    private void SaveThresholds(ExpiryThresholds thresholds)
    {
        _settings = NormalizeSettings(_settingsStore.Load());
        _settings.Thresholds = thresholds.Normalized();
        _settingsStore.Save(_settings);
        _telemetry.Increment(t => t.ThresholdsChanged++);
    }

    private void SaveAdvancedSettings(LogFormat format, bool eventLog, bool telemetry)
    {
        _settings = NormalizeSettings(_settingsStore.Load());
        _settings.LogFormat       = format;
        _settings.EventLogEnabled = eventLog;
        _settings.TelemetryEnabled = telemetry;
        _settingsStore.Save(_settings);

        // Aplica imediatamente nos servicos em runtime (sem precisar reiniciar o app).
        _logger.ApplySettings(_settings);
        _telemetry.Enabled = telemetry;

        _logger.Info($"Advanced settings updated: LogFormat={format}, EventLog={eventLog}, Telemetry={telemetry}");
    }

    private bool TestNotificationNow()
    {
        var state        = _stateStore.Load();
        var certificates = _certificateReader.ReadCurrentUserPersonalCertificates();
        var thresholds   = _settings.Thresholds.Normalized();
        var plan         = _expiryEvaluator.BuildReminderPlan(certificates, state, DateOnly.FromDateTime(DateTime.Today), thresholds);

        if (!plan.HasItems)
        {
            MessageBox.Show(
                "Nenhum certificado pendente de notificação no momento.",
                "Certificados digitais",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return false;
        }

        return ShowNotification(plan);
    }

    // -------------------------------------------------------------------------
    // Auxiliares
    // -------------------------------------------------------------------------

    private void UpdateTrayTooltip()
    {
        try
        {
            var version   = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            var lastCheck = _settings.LastCheckDate?.ToString("dd/MM/yyyy") ?? "nunca";
            var raw       = $"CertExpiryMonitor v{version} | Última verificação: {lastCheck}";
            // NotifyIcon.Text tem limite de 63 caracteres no Win32.
            _notifyIcon.Text = raw.Length > 63 ? raw[..63] : raw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to update tray tooltip");
        }
    }

    private void SafeExecute(Action action, string errorMessage)
    {
        try { action(); }
        catch (Exception ex) { _logger.Error(ex, errorMessage); }
    }

    private void HandleActivationRequests()
    {
        if (_configurationEvent.WaitOne(0))
        {
            SafeExecute(() => ShowDetails(openSettingsTab: true), "Failed to activate settings window");
            return;
        }

        if (_activationEvent.WaitOne(0))
        {
            SafeExecute(() => ShowDetails(), "Failed to activate existing window");
        }
    }

    private static AppSettings NormalizeSettings(AppSettings settings)
    {
        if (settings.DailyCheckTime < TimeSpan.Zero || settings.DailyCheckTime >= TimeSpan.FromDays(1))
        {
            settings.DailyCheckTime = TimeSpan.FromHours(9);
        }

        if (settings.InitialDelayMinutes <= 0)
        {
            settings.InitialDelayMinutes = 5;
        }

        return settings;
    }

    private static Dictionary<string, string> ParseArguments(string arguments)
    {
        return arguments
            .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => part.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(
                parts => parts[0],
                parts => Uri.UnescapeDataString(parts[1]),
                StringComparer.OrdinalIgnoreCase);
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
