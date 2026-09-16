using CertExpiryMonitor.Models;
using System.Windows.Forms;

namespace CertExpiryMonitor.Services;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly EventWaitHandle _activationEvent;
    private readonly EventWaitHandle _configurationEvent;
    private readonly JsonSettingsStore _settingsStore;
    private readonly JsonStateStore _stateStore;
    private readonly CertificateReader _certificateReader;
    private readonly DetailsDataLoader _detailsLoader;
    private readonly ExpiryEvaluator _expiryEvaluator;
    private readonly NotificationCheckCoordinator _checkCoordinator;
    private readonly ToastNotifierService _notifier;
    private readonly NotificationPresenter _notificationPresenter;
    private readonly StartupRegistration _startup;
    private readonly TelemetryService _telemetry;
    private readonly CertificateStateActions _stateActions;
    private readonly ToastActionDispatcher _toastActions;
    private readonly SettingsUpdateCoordinator _settingsUpdater;
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

    internal TrayApplicationContext(
        string[] args,
        EventWaitHandle activationEvent,
        EventWaitHandle configurationEvent,
        JsonSettingsStore settingsStore,
        JsonStateStore stateStore,
        CertificateReader certificateReader,
        DetailsDataLoader detailsLoader,
        ExpiryEvaluator expiryEvaluator,
        NotificationCheckCoordinator checkCoordinator,
        ToastNotifierService notifier,
        NotificationPresenter notificationPresenter,
        StartupRegistration startup,
        TelemetryService telemetry,
        CertificateStateActions stateActions,
        ToastActionDispatcher toastActions,
        SettingsUpdateCoordinator settingsUpdater,
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
        _detailsLoader = detailsLoader;
        _expiryEvaluator = expiryEvaluator;
        _checkCoordinator = checkCoordinator;
        _notifier = notifier;
        _notificationPresenter = notificationPresenter;
        _startup = startup;
        _telemetry = telemetry;
        _stateActions = stateActions;
        _toastActions = toastActions;
        _settingsUpdater = settingsUpdater;
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

        if (showNoAlertFeedback && result.ManualFeedback is { } feedback)
        {
            _notifyIcon.ShowBalloonTip(
                result.Status == CheckCycleStatus.ReadFailed ? 5000 : 3000,
                "Monitor de Certificados A1",
                feedback,
                result.Status == CheckCycleStatus.ReadFailed ? ToolTipIcon.Warning : ToolTipIcon.Info);
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
        var presentation = _notificationPresenter.Show(plan, _settings, () => ShowDetails());
        _settings = presentation.Settings;
        return presentation.Shown;
    }

    // -------------------------------------------------------------------------
    // Acoes do toast
    // -------------------------------------------------------------------------

    private void HandleToastAction(string arguments)
    {
        _toastActions.Dispatch(arguments, ShowTimeConfiguration, () => ShowDetails());
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

        var loaded = LoadCertificateDetails();
        if (loaded.Settings is { } availableSettings)
        {
            _settings = availableSettings;
        }

        _detailsForm = new DetailsForm(new DetailsFormOptions
        {
            Certificates = loaded.CertificateRead.Certificates,
            CertificateReadStatus = loaded.CertificateRead.Status,
            State = loaded.State,
            SettingsAvailable = loaded.SettingsAvailable,
            StateAvailable = loaded.StateAvailable,
            NotificationTime = _settings.DailyCheckTime,
            NotificationSoundEnabled = _settings.NotificationSoundEnabled,
            Thresholds = _settings.Thresholds.Normalized(),
            DismissCertificate = _stateActions.DismissOne,
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

    private DetailsLoadResult LoadCertificateDetails() => _detailsLoader.Load();

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

        var previousSettings = NotificationCheckCoordinator.NormalizeSettings(loadedSettings);
        var previousStartupEnabled = previousSettings.StartupEnabled;
        var newSettings = NotificationCheckCoordinator.NormalizeSettings(loadedSettings);
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
        return _stateActions.RestoreOne(thumbprint);
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
        var result = _settingsUpdater.Apply(update, DateTime.Now);
        if (!result.Saved || result.Plan is null) return false;

        var plan = result.Plan;
        _settings = plan.Settings;
        _forceNextScheduledNotification = plan.ForceNextScheduledNotification;
        if (plan.ThresholdsChanged)
        {
            _ignoreConfiguredTimeOnNextTimer = true;
            ScheduleTimer(TimeSpan.FromSeconds(1));
        }
        else if (plan.ScheduleChanged)
        {
            ScheduleNextDailyCheck(allowImmediateToday: plan.ShouldRunAgainToday);
        }
        return true;
    }

    private bool TestNotificationNow()
    {
        _logger.Info("User requested test notification.");
        _diagnosticEvents.RecordInfo("notification.test_requested", "TrayApplicationContext", "Usuario solicitou teste de notificacao.");
        if (!_settingsStore.TryLoad(out var currentSettings))
        {
            MessageBox.Show("Não foi possível ler as configurações. Tente novamente após recuperar o arquivo.", "Certificados digitais", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        _settings = NotificationCheckCoordinator.NormalizeSettings(currentSettings);
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

    internal static Dictionary<string, string> ParseArguments(string? arguments)
        => ToastActionArgumentParser.Parse(arguments);

    private static string ShortThumbprint(string thumbprint)
    {
        if (string.IsNullOrWhiteSpace(thumbprint)) return "(empty)";
        var normalized = CertificateIdentity.NormalizeThumbprint(thumbprint);
        return normalized.Length <= 8 ? normalized : normalized[..8];
    }

}
