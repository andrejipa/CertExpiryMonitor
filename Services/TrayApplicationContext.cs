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
    private readonly NotificationCheckCoordinator _checkCoordinator;
    private readonly ToastNotifierService _notifier;
    private readonly StartupRegistration _startup;
    private readonly TelemetryService _telemetry;
    private readonly DiagnosticsBundleService _diagnostics;
    private readonly DiagnosticEventStore _diagnosticEvents;
    private readonly FileLogger _logger;
    private readonly AppPaths _paths;
    private readonly SynchronizationContext _uiContext;
    private readonly NotifyIcon _notifyIcon;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _activationTimer;
    private AppSettings _settings;
    private DetailsForm? _detailsForm;
    private bool _forceNextScheduledNotification;
    private bool _ignoreConfiguredTimeOnNextTimer;

    public TrayApplicationContext(
        string[] args,
        EventWaitHandle activationEvent,
        EventWaitHandle configurationEvent,
        JsonSettingsStore settingsStore,
        JsonStateStore stateStore,
        CertificateReader certificateReader,
        ExpiryEvaluator expiryEvaluator,
        CertificateCheckService checkService,
        NotificationCheckCoordinator checkCoordinator,
        ToastNotifierService notifier,
        StartupRegistration startup,
        TelemetryService telemetry,
        DiagnosticsBundleService diagnostics,
        FileLogger logger,
        AppPaths paths,
        DiagnosticEventStore diagnosticEvents)
    {
        _activationEvent = activationEvent;
        _configurationEvent = configurationEvent;
        _settingsStore = settingsStore;
        _stateStore = stateStore;
        _certificateReader = certificateReader;
        _expiryEvaluator = expiryEvaluator;
        _checkService = checkService;
        _checkCoordinator = checkCoordinator;
        _notifier = notifier;
        _startup = startup;
        _telemetry = telemetry;
        _diagnostics = diagnostics;
        _diagnosticEvents = diagnosticEvents;
        _logger = logger;
        _paths = paths;
        _uiContext = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        var settingsAvailable = _settingsStore.TryLoad(out _settings);
        _settings = NotificationCheckCoordinator.NormalizeSettings(_settings);
        _telemetry.Enabled = _settings.TelemetryEnabled;
        _forceNextScheduledNotification = _settings.ForceNextNotificationReminder;

        _notifyIcon = BuildNotifyIcon();
        if (!settingsAvailable)
        {
            _notifyIcon.ShowBalloonTip(
                5000,
                "Monitor de Certificados A1",
                "Não foi possível ler as configurações. Nenhuma alteração será gravada até a leitura ser normalizada.",
                ToolTipIcon.Warning);
        }
        UpdateTrayTooltip();

        _timer = new System.Windows.Forms.Timer();
        _timer.Tick += (_, _) => OnTimerTick();

        _activationTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _activationTimer.Tick += (_, _) => HandleActivationRequests();
        _activationTimer.Start();

        _notifier.Activated += (_, eventArgs) =>
            _uiContext.Post(_ => HandleToastAction(eventArgs.Arguments), null);

        _ = Task.Run(_diagnosticEvents.RunMaintenance);

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

        menu.Items.Add("Exportar diagnóstico...", null,
            (_, _) => SafeExecute(ExportDiagnosticsBundle, "Failed to export diagnostics bundle"));

        menu.Items.Add("Diagnóstico de inicialização...", null,
            (_, _) => SafeExecute(ShowStartupDiagnosticsWindow, "Failed to open startup diagnostics"));

        menu.Items.Add("Ver estatísticas de uso...", null,
            (_, _) => SafeExecute(ShowTelemetryWindow, "Failed to open telemetry window"));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Sair", null, (_, _) => ExitThread());

        var icon = new NotifyIcon
        {
            Icon = AppIcon.Current,
            Text = "Monitor de Certificados A1",
            ContextMenuStrip = menu,
            Visible = true
        };

        icon.DoubleClick += (_, _) =>
            SafeExecute(() => ShowDetails(), "Failed to open certificate details");

        return icon;
    }

    private void OpenLogsFolder()
    {
        _logger.Info("User opened logs folder.");
        _diagnosticEvents.RecordInfo("ui.open_logs_folder", "TrayApplicationContext", "Usuario abriu a pasta de logs.");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _paths.RootDirectory,
            UseShellExecute = true
        });
    }

    private void ExportDiagnosticsBundle()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Exportar diagnóstico",
            Filter = "Arquivo ZIP (*.zip)|*.zip",
            DefaultExt = "zip",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"CertExpiryMonitor-diagnostico-{DateTime.Now:yyyyMMdd-HHmmss}.zip"
        };

        if (dialog.ShowDialog() != DialogResult.OK)
        {
            return;
        }

        try
        {
            var bundlePath = _diagnostics.CreateBundle(dialog.FileName);
            _diagnosticEvents.RecordInfo(
                "diagnostics.exported",
                "TrayApplicationContext",
                "Pacote de diagnostico exportado.",
                new { destination = bundlePath });
            MessageBox.Show(
                $"Pacote de diagnóstico gerado:\r\n{bundlePath}\r\n\r\nEle contém logs e métricas locais do app, sem chave privada, PFX ou senha.",
                "CertExpiryMonitor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to export diagnostics bundle");
            _diagnosticEvents.RecordError(ex, "diagnostics.export_failed", "TrayApplicationContext", "Falha ao exportar pacote de diagnostico.");
            MessageBox.Show(
                "Não foi possível exportar o diagnóstico agora. Feche programas que possam estar usando o arquivo de destino e tente novamente.",
                "CertExpiryMonitor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void ShowTelemetryWindow()
    {
        _logger.Info("User opened telemetry window.");
        _diagnosticEvents.RecordInfo("ui.open_telemetry", "TrayApplicationContext", "Usuario abriu a janela de estatisticas.");
        using var window = new TelemetryWindow(_telemetry);
        window.ShowDialog();
    }

    private void ShowStartupDiagnosticsWindow()
    {
        _logger.Info("User opened startup diagnostics window.");
        _diagnosticEvents.RecordInfo("ui.open_startup_diagnostics", "TrayApplicationContext", "Usuario abriu diagnostico de inicializacao.");
        using var window = new StartupDiagnosticsWindow(_startup, _logger);
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
        var retryScheduled = false;
        try
        {
            _timer.Stop();
            var ignoreConfiguredTime = _ignoreConfiguredTimeOnNextTimer;
            _ignoreConfiguredTimeOnNextTimer = false;
            var result = RunCheck(ignoreConfiguredTime: ignoreConfiguredTime, ignoreLastCheckDate: false);
            if (result.Ran)
            {
                _ = Task.Run(_diagnosticEvents.RunMaintenance);
            }
            if (result.ShouldRetry)
            {
                ScheduleTimer(TimeSpan.FromMinutes(5));
                retryScheduled = true;
                return;
            }

            if (!result.Ran && ignoreConfiguredTime && _forceNextScheduledNotification)
            {
                _ignoreConfiguredTimeOnNextTimer = true;
                ScheduleTimer(TimeSpan.FromSeconds(1));
                retryScheduled = true;
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Scheduled check failed");
            _diagnosticEvents.RecordError(ex, "check.scheduled_failed", "TrayApplicationContext", "Verificacao agendada falhou.");
        }
        finally
        {
            if (!retryScheduled)
            {
                ScheduleNextDailyCheck();
            }
        }
    }

    private void ScheduleNextDailyCheck()
    {
        ScheduleNextDailyCheck(allowImmediateToday: false);
    }

    private void ScheduleNextDailyCheck(bool allowImmediateToday)
    {
        var now = DateTime.Now;
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
        var milliseconds = (int)Math.Clamp(delay.TotalMilliseconds, 1000, int.MaxValue);
        _timer.Interval = milliseconds;
        _timer.Start();
    }

    // -------------------------------------------------------------------------
    // Logica de verificacao (delega ao coordenador testavel)
    // -------------------------------------------------------------------------

    private CheckCycleResult RunCheck(bool ignoreConfiguredTime, bool ignoreLastCheckDate, bool showNoAlertFeedback = false)
    {
        if (showNoAlertFeedback)
        {
            _diagnosticEvents.RecordInfo("check.manual_requested", "TrayApplicationContext", "Usuario solicitou verificacao manual.");
        }

        var result = _checkCoordinator.Run(
            new CheckCycleRequest(ignoreConfiguredTime, ignoreLastCheckDate),
            ShowNotification);

        if (result.Settings is { } resultSettings)
        {
            _settings = resultSettings;
            _forceNextScheduledNotification = resultSettings.ForceNextNotificationReminder;
        }

        // Telemetria: contagem total + skip vs executou + manual (showNoAlertFeedback=true significa botao manual)
        _telemetry.Increment(t =>
        {
            t.TotalChecks++;
            if (!result.Ran) t.ChecksSkipped++;
            if (showNoAlertFeedback) t.ManualChecks++;
        });

        if (result.Plan is not null)
        {
            _telemetry.Increment(t => t.ChecksWithPlan++);
        }

        if (result.Status == CheckCycleStatus.NotificationShown)
        {
            _telemetry.Increment(t => t.NotificationsShown++);
        }
        else if (result.Status is CheckCycleStatus.NotificationFailed or
                 CheckCycleStatus.StatePersistFailed or
                 CheckCycleStatus.SettingsPersistFailed)
        {
            _telemetry.Increment(t => t.NotificationFailures++);
        }

        if (showNoAlertFeedback && result.Status == CheckCycleStatus.CompletedNoDue)
        {
            _notifyIcon.ShowBalloonTip(
                3000,
                "Monitor de Certificados A1",
                "Nenhum certificado próximo do vencimento.",
                ToolTipIcon.Info);
        }
        else if (showNoAlertFeedback && result.Status == CheckCycleStatus.ReadFailed)
        {
            _notifyIcon.ShowBalloonTip(
                5000,
                "Monitor de Certificados A1",
                "A verificação não foi concluída porque settings, estado ou certificados não puderam ser lidos. Nenhum dado foi alterado.",
                ToolTipIcon.Warning);
        }

        if (result.Ran)
        {
            UpdateTrayTooltip();
        }

        return result;
    }

    // -------------------------------------------------------------------------
    // Notificacoes
    // -------------------------------------------------------------------------

    private bool ShowNotification(NotificationPlan plan)
    {
        var thresholds = _settings.Thresholds.Normalized();
        var shown = _notifier.Show(plan, thresholds, _settings.NotificationSoundEnabled);
        if (shown)
        {
            _diagnosticEvents.RecordInfo(
                "notification.shown",
                "TrayApplicationContext",
                "Notificacao toast do Windows exibida.",
                new { channel = "windows_toast", due_count = plan.DueCertificates.Count });
        }
        else
        {
            _logger.Info("Toast notification was not accepted by Windows; app popup fallback was used.");
            _diagnosticEvents.RecordWarning(
                "notification.fallback_used",
                "TrayApplicationContext",
                "Toast do Windows nao foi aceito; popup proprio sera usado.",
                new { due_count = plan.DueCertificates.Count });
            shown = ShowFallbackWindow(plan);
            _diagnosticEvents.RecordInfo(
                shown ? "notification.shown" : "notification.fallback_closed",
                "TrayApplicationContext",
                shown ? "Popup proprio exibido e usuario abriu detalhes." : "Popup proprio fechado sem acao efetiva.",
                new { channel = "app_popup", due_count = plan.DueCertificates.Count });
        }

        return shown;
    }

    private bool ShowFallbackWindow(NotificationPlan plan)
    {
        try
        {
            if (_settingsStore.TryLoad(out var latestSettings))
            {
                _settings = NormalizeSettings(latestSettings);
            }
            if (_settings.NotificationSoundEnabled)
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
                Text = BuildFallbackSummary(plan),
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
            viewDetails.Click += (_, _) => { form.DialogResult = DialogResult.OK; form.Close(); ShowDetails(); };

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
            _diagnosticEvents.RecordError(ex, "notification.fallback_failed", "TrayApplicationContext", "Falha ao exibir popup proprio.");
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
        _diagnosticEvents.RecordInfo(
            "toast.activated",
            "TrayApplicationContext",
            "Usuario ativou acao de toast.",
            new { action });

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
        _ = TryDismissOne(thumbprint);
    }

    private bool TryDismissOne(string thumbprint)
    {
        if (!_stateStore.TryLoad(out var state))
        {
            _logger.Error(new IOException("certificate-state.json read failed"), "Failed to dismiss certificate because state could not be read");
            return false;
        }
        _expiryEvaluator.DismissCertificate(thumbprint, state);
        if (!_stateStore.Save(state))
        {
            _logger.Error(new IOException("certificate-state.json save failed"), $"Failed to dismiss certificate {ShortThumbprint(thumbprint)}");
            return false;
        }

        _telemetry.Increment(t => t.DismissOne++);
        _logger.Info($"User dismissed certificate {ShortThumbprint(thumbprint)}.");
        _diagnosticEvents.RecordInfo(
            "certificate.dismiss_one",
            "TrayApplicationContext",
            "Usuario marcou certificado para nao lembrar.",
            new { thumbprint });
        return true;
    }

    private void DismissAllCurrent()
    {
        var lastPlan = _checkService.LastPlan;
        if (lastPlan is null) return;

        if (!_stateStore.TryLoad(out var state))
        {
            _logger.Error(new IOException("certificate-state.json read failed"), "Failed to dismiss current certificates because state could not be read");
            return;
        }
        _expiryEvaluator.DismissCertificates(
            lastPlan.DueCertificates.Select(item => item.Certificate.Thumbprint),
            state);
        if (!_stateStore.Save(state))
        {
            _logger.Error(new IOException("certificate-state.json save failed"), "Failed to dismiss all certificates from current notification");
            return;
        }

        _telemetry.Increment(t => t.DismissAll++);
        _logger.Info("User dismissed all certificates from current notification.");
        _diagnosticEvents.RecordInfo(
            "certificate.dismiss_all",
            "TrayApplicationContext",
            "Usuario marcou todos os certificados da notificacao atual para nao lembrar.",
            new { count = lastPlan.DueCertificates.Count });
    }

    private void DismissAll(IEnumerable<string> thumbprints)
    {
        var thumbprintList = thumbprints.ToArray();
        if (!_stateStore.TryLoad(out var state))
        {
            _logger.Error(new IOException("certificate-state.json read failed"), "Failed to dismiss toast certificates because state could not be read");
            return;
        }
        _expiryEvaluator.DismissCertificates(thumbprintList, state);
        if (!_stateStore.Save(state))
        {
            _logger.Error(new IOException("certificate-state.json save failed"), "Failed to dismiss all certificates from toast action");
            return;
        }

        _telemetry.Increment(t => t.DismissAll++);
        _logger.Info("User dismissed all certificates from toast action.");
        _diagnosticEvents.RecordInfo(
            "certificate.dismiss_all",
            "TrayApplicationContext",
            "Usuario marcou certificados do toast para nao lembrar.",
            new { count = thumbprintList.Length });
    }

    // -------------------------------------------------------------------------
    // Janela de detalhes
    // -------------------------------------------------------------------------

    private void ShowTimeConfiguration() => ShowDetails(openSettingsTab: true);

    private void ShowDetails(bool openSettingsTab = false)
    {
        _logger.Info(openSettingsTab ? "User opened settings window." : "User opened certificate details window.");
        _diagnosticEvents.RecordInfo(
            openSettingsTab ? "ui.open_settings" : "ui.open_details",
            "TrayApplicationContext",
            openSettingsTab ? "Usuario abriu configuracoes." : "Usuario abriu detalhes de certificados.");

        if (_detailsForm is { IsDisposed: false })
        {
            _detailsForm.FocusExisting(openSettingsTab);
            return;
        }

        if (!_stateStore.TryLoad(out var state))
        {
            MessageBox.Show(
                "Não foi possível ler o estado dos certificados. A janela não será aberta para evitar alterações sobre dados incompletos.",
                "CertExpiryMonitor",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return;
        }

        var certificateRead = _certificateReader.ReadCurrentUserPersonalCertificates();

        _detailsForm = new DetailsForm(new DetailsFormOptions
        {
            Certificates = certificateRead.Certificates,
            CertificateReadStatus = certificateRead.Status,
            State = state,
            NotificationTime = _settings.DailyCheckTime,
            NotificationSoundEnabled = _settings.NotificationSoundEnabled,
            Thresholds = _settings.Thresholds.Normalized(),
            DismissCertificate = TryDismissOne,
            RestoreCertificate = RestoreOne,
            RemoveCertificate = RemoveCertificate,
            OpenWindowsCertificateStore = OpenWindowsCertificateStore,
            SaveSettings = SaveSettings,
            GetThresholds = () => _settings.Thresholds.Normalized(),
            TestNotificationNow = TestNotificationNow,
            ReloadCertificates = LoadCertificateDetails,
            Logger = _logger,
            OpenSettingsTab = openSettingsTab,
            LogFormat = _settings.LogFormat,
            EventLogEnabled = _settings.EventLogEnabled,
            TelemetryEnabled = _settings.TelemetryEnabled
        });

        _detailsForm.FormClosed += (_, _) => _detailsForm = null;
        _detailsForm.Show();
        _detailsForm.FocusExisting(openSettingsTab);
    }

    private (CertificateReadResult CertificateRead, IReadOnlyDictionary<string, CertificateStateRecord> State) LoadCertificateDetails()
    {
        if (!_stateStore.TryLoad(out var state))
        {
            throw new IOException("certificate-state.json could not be read");
        }

        return (_certificateReader.ReadCurrentUserPersonalCertificates(), state);
    }

    // -------------------------------------------------------------------------
    // Acoes do menu / callbacks do DetailsForm
    // -------------------------------------------------------------------------

    private void ToggleStartup(object? sender)
    {
        if (!_settingsStore.TryLoad(out var loadedSettings))
        {
            _logger.Error(new IOException("settings.json read failed"), "Failed to toggle startup because settings could not be read");
            return;
        }

        var previousSettings = NormalizeSettings(loadedSettings);
        var previousStartupEnabled = previousSettings.StartupEnabled;
        var newSettings = NormalizeSettings(loadedSettings);
        newSettings.StartupEnabled = !newSettings.StartupEnabled;

        var startupChanged = newSettings.StartupEnabled
            ? _startup.EnsureRegistered()
            : _startup.Remove();
        if (!startupChanged)
        {
            _logger.Error(new IOException("startup registration change failed"), "Failed to apply startup setting");
            _diagnosticEvents.RecordError(
                new IOException("startup registration change failed"),
                "settings.startup_apply_failed",
                "TrayApplicationContext",
                "Falha ao aplicar configuracao de inicializacao.");
            return;
        }

        if (!_settingsStore.Save(newSettings))
        {
            _logger.Error(new IOException("settings.json save failed"), "Failed to persist startup setting");
            _diagnosticEvents.RecordError(
                new IOException("settings.json save failed"),
                "settings.startup_persist_failed",
                "TrayApplicationContext",
                "Falha ao persistir configuracao de inicializacao.");
            if (previousStartupEnabled)
            {
                if (!_startup.EnsureRegistered())
                {
                    _logger.Error(new IOException("startup rollback failed"), "Failed to restore startup registration after settings save failure");
                }
            }
            else
            {
                if (!_startup.Remove())
                {
                    _logger.Error(new IOException("startup rollback failed"), "Failed to remove startup registration after settings save failure");
                }
            }
            return;
        }

        _settings = newSettings;

        if (sender is ToolStripMenuItem item)
        {
            item.Checked = _settings.StartupEnabled;
        }
        _logger.Info($"User changed startup setting to {_settings.StartupEnabled}.");
        _diagnosticEvents.RecordInfo(
            "settings.startup_changed",
            "TrayApplicationContext",
            "Usuario alterou inicializacao com Windows.",
            new { enabled = _settings.StartupEnabled });
    }

    private bool RestoreOne(string thumbprint)
    {
        if (!_stateStore.TryLoad(out var state))
        {
            _logger.Error(new IOException("certificate-state.json read failed"), "Failed to restore certificate because state could not be read");
            return false;
        }
        _expiryEvaluator.RestoreCertificate(thumbprint, state);
        if (!_stateStore.Save(state))
        {
            _logger.Error(new IOException("certificate-state.json save failed"), $"Failed to restore certificate {ShortThumbprint(thumbprint)}");
            return false;
        }

        _telemetry.Increment(t => t.Restore++);
        _logger.Info($"User restored certificate {ShortThumbprint(thumbprint)}.");
        _diagnosticEvents.RecordInfo(
            "certificate.restore",
            "TrayApplicationContext",
            "Usuario voltou a lembrar certificado.",
            new { thumbprint });
        return true;
    }

    private bool RemoveCertificate(string thumbprint)
    {
        var removed = _certificateReader.RemoveFromCurrentUserPersonalStore(thumbprint);
        _logger.Info($"User requested certificate removal {ShortThumbprint(thumbprint)}. Removed={removed}.");
        _diagnosticEvents.RecordWarning(
            "certificate.remove_requested",
            "TrayApplicationContext",
            "Usuario solicitou remocao de certificado.",
            new { thumbprint, removed });
        return removed;
    }

    private void OpenWindowsCertificateStore()
    {
        _logger.Info("User opened Windows certificate store.");
        _diagnosticEvents.RecordInfo("ui.open_windows_certificate_store", "TrayApplicationContext", "Usuario abriu o repositorio de certificados do Windows.");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "certmgr.msc",
            UseShellExecute = true
        });
    }

    private bool SaveSettings(DetailsSettingsUpdate update)
    {
        if (!_settingsStore.TryLoad(out var loadedSettings))
        {
            _logger.Error(new IOException("settings.json read failed"), "Failed to save settings because current settings could not be read");
            return false;
        }

        var currentSettings = NormalizeSettings(loadedSettings);
        var plan = DetailsSettingsPlanner.Build(currentSettings, update, DateTime.Now);
        var newSettings = plan.Settings;

        if (!_settingsStore.Save(newSettings))
        {
            _logger.Error(new IOException("settings.json save failed"), "Failed to persist settings");
            _diagnosticEvents.RecordError(
                new IOException("settings.json save failed"),
                "settings.persist_failed",
                "TrayApplicationContext",
                "Falha ao persistir configuracoes.");
            return false;
        }

        _settings = newSettings;
        _forceNextScheduledNotification = plan.ForceNextScheduledNotification;

        if (plan.ScheduleChanged)
        {
            _telemetry.Increment(t => t.ScheduleChanged++);
            _logger.Info($"User changed daily notification time to {plan.SelectedMinute:hh\\:mm}.");
        }

        if (plan.ThresholdsChanged)
        {
            _telemetry.Increment(t => t.ThresholdsChanged++);
            _logger.Info($"User changed thresholds to {_settings.Thresholds.Level1}/{_settings.Thresholds.Level7}/{_settings.Thresholds.Level15}/{_settings.Thresholds.Level30}.");
        }

        if (plan.ThresholdsChanged)
        {
            _ignoreConfiguredTimeOnNextTimer = true;
            ScheduleTimer(TimeSpan.FromSeconds(1));
        }
        else if (plan.ScheduleChanged)
        {
            ScheduleNextDailyCheck(allowImmediateToday: plan.ShouldRunAgainToday);
        }

        if (plan.SoundChanged)
        {
            _logger.Info($"User changed notification sound setting to {update.NotificationSoundEnabled}.");
        }

        // Aplica imediatamente nos servicos em runtime (sem precisar reiniciar o app).
        _logger.ApplySettings(_settings);
        _telemetry.Enabled = update.TelemetryEnabled;

        if (plan.AdvancedChanged)
        {
            _logger.Info($"Advanced settings updated: LogFormat={update.LogFormat}, EventLog={update.EventLogEnabled}, Telemetry={update.TelemetryEnabled}");
        }

        if (plan.ScheduleChanged || plan.ThresholdsChanged || plan.SoundChanged || plan.AdvancedChanged)
        {
            _diagnosticEvents.RecordInfo(
                "settings.changed",
                "TrayApplicationContext",
                "Usuario salvou alteracoes de configuracao.",
                new
                {
                    scheduleChanged = plan.ScheduleChanged,
                    thresholdsChanged = plan.ThresholdsChanged,
                    soundChanged = plan.SoundChanged,
                    advancedChanged = plan.AdvancedChanged,
                    log_format = update.LogFormat.ToString(),
                    event_log_enabled = update.EventLogEnabled,
                    telemetry_enabled = update.TelemetryEnabled
                });
        }

        return true;
    }

    private bool TestNotificationNow()
    {
        _logger.Info("User requested test notification.");
        _diagnosticEvents.RecordInfo("notification.test_requested", "TrayApplicationContext", "Usuario solicitou teste de notificacao.");
        if (!_stateStore.TryLoad(out var state))
        {
            MessageBox.Show("Não foi possível ler o estado dos certificados.", "Certificados digitais", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var certificateRead = _certificateReader.ReadCurrentUserPersonalCertificates();
        if (!certificateRead.IsComplete)
        {
            MessageBox.Show("A leitura do repositório de certificados foi incompleta. O popup de teste não será enviado.", "Certificados digitais", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        var certificates = certificateRead.Certificates;
        var thresholds = _settings.Thresholds.Normalized();
        var plan = _expiryEvaluator.BuildReminderPlan(certificates, state, DateOnly.FromDateTime(DateTime.Today), thresholds);

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
            var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
            var lastCheck = _settings.LastCheckDate?.ToString("dd/MM/yyyy") ?? "nunca";
            var raw = $"CertExpiryMonitor v{version} | Última verificação: {lastCheck}";
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
        catch (Exception ex)
        {
            _logger.Error(ex, errorMessage);
            _diagnosticEvents.RecordError(ex, "ui.action_failed", "TrayApplicationContext", errorMessage);
        }
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

        settings.Thresholds = (settings.Thresholds ?? new ExpiryThresholds()).Normalized();

        return settings;
    }

    internal static Dictionary<string, string> ParseArguments(string? arguments)
        => ToastActionArgumentParser.Parse(arguments);

    private static string ShortThumbprint(string thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint)) return "(empty)";
        var normalized = JsonStateStore.NormalizeThumbprint(thumbprint);
        return normalized.Length <= 8 ? normalized : normalized[..8];
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
    }
}
