using System.Windows.Forms;
using CertExpiryMonitor.Services;

namespace CertExpiryMonitor;

internal static class Program
{
    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _activationEvent;
    private static EventWaitHandle? _configurationEvent;
    private static FileLogger? _logger;
    private static DiagnosticEventStore? _diagnosticEvents;

    [STAThread]
    private static void Main(string[] args)
    {
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CertExpiryMonitor.CurrentUser.Activate");
        _configurationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CertExpiryMonitor.CurrentUser.Configure");
        _singleInstanceMutex = new Mutex(true, @"Local\CertExpiryMonitor.CurrentUser", out var createdNew);

        if (!createdNew)
        {
            if (args.Any(arg => arg.Equals("--configure", StringComparison.OrdinalIgnoreCase)))
            {
                _configurationEvent.Set();
            }
            else if (!args.Any(arg => arg.Equals("--background", StringComparison.OrdinalIgnoreCase)))
            {
                _activationEvent.Set();
            }

            return;
        }

        ApplicationConfiguration.Initialize();

        var paths = new AppPaths();
        var logger = new FileLogger(paths);
        _logger = logger;

        // Composicao dos stores primeiro: precisamos carregar as preferencias antes
        // do primeiro log para manter monitor.log em JSONL puro quando configurado.
        var stateStore = new JsonStateStore(paths, logger);
        var settingsStore = new JsonSettingsStore(paths, logger);
        var settingsAvailable = settingsStore.TryLoad(out var currentSettings);
        logger.ApplySettings(currentSettings);
        var diagnosticEvents = new DiagnosticEventStore(paths, logger);
        _diagnosticEvents = diagnosticEvents;
        diagnosticEvents.Initialize();

        var version = System.Reflection.Assembly.GetExecutingAssembly()
                          .GetName().Version?.ToString() ?? "unknown";
        logger.Info($"CertExpiryMonitor v{version} starting (args: [{string.Join(", ", args)}])");
        diagnosticEvents.RecordInfo(
            "app.startup",
            "Program",
            "Aplicativo iniciado.",
            new
            {
                version,
                arguments = args.Select(arg => arg.StartsWith("--", StringComparison.Ordinal) ? arg : "(redacted)").ToArray(),
                executable = Environment.ProcessPath ?? Application.ExecutablePath
            });

        Application.ThreadException += (_, e) =>
        {
            logger.Error(e.Exception, "Unhandled UI exception");
            _diagnosticEvents?.RecordError(e.Exception, "app.unhandled_ui_exception", "Program", "Excecao nao tratada na UI.");
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                _logger?.Error(ex, "Unhandled application exception");
                _diagnosticEvents?.RecordError(ex, "app.unhandled_exception", "Program", "Excecao nao tratada no aplicativo.");
            }
        };

        // Composicao dos servicos
        var certificateReader = new CertificateReader(logger);
        var detailsLoader = new DetailsDataLoader(settingsStore, stateStore, certificateReader);
        var expiryEvaluator = new ExpiryEvaluator();
        var checkService = new CertificateCheckService(settingsStore, stateStore, certificateReader, expiryEvaluator, logger, diagnosticEvents);
        var checkCoordinator = new NotificationCheckCoordinator(settingsStore, checkService, logger, diagnosticEvents);
        var notifier = new ToastNotifierService(logger);
        var notificationPresenter = new NotificationPresenter(notifier, settingsStore, logger, diagnosticEvents);
        var startup = new StartupRegistration(logger);
        var telemetry = new TelemetryService(paths, logger);
        var stateActions = new CertificateStateActions(stateStore, expiryEvaluator, telemetry, logger, diagnosticEvents);
        var toastActions = new ToastActionDispatcher(() => checkService.LastPlan, stateActions, logger, diagnosticEvents);
        var settingsUpdater = new SettingsUpdateCoordinator(settingsStore, telemetry, logger, diagnosticEvents);
        var diagnostics = new DiagnosticsBundleService(paths, startup, certificateReader, logger, diagnosticEvents);

        if (settingsAvailable)
        {
            if (currentSettings.StartupEnabled) startup.EnsureRegistered();
            else startup.Remove();
        }
        else
        {
            logger.Error(new IOException("settings.json read failed"), "Startup registration was left unchanged because settings could not be read");
        }

        notifier.EnsureShortcut();

        using var context = new TrayApplicationContext(
            args,
            _activationEvent,
            _configurationEvent,
            settingsStore,
            stateStore,
            certificateReader,
            detailsLoader,
            expiryEvaluator,
            checkCoordinator,
            notifier,
            notificationPresenter,
            startup,
            telemetry,
            stateActions,
            toastActions,
            settingsUpdater,
            diagnostics,
            logger,
            paths,
            diagnosticEvents);

        try
        {
            Application.Run(context);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Application terminated unexpectedly");
            diagnosticEvents.RecordError(ex, "app.terminated_unexpectedly", "Program", "Aplicativo terminou inesperadamente.");
        }
        finally
        {
            diagnosticEvents.RecordInfo("app.shutdown", "Program", "Aplicativo encerrado.");

            // ReleaseMutex pode lancar ApplicationException se a thread atual nao
            // possuir o mutex (cenarios de reentrancia incomuns); envolvido em try
            // para nao mascarar a excecao original do Application.Run.
            try { _singleInstanceMutex.ReleaseMutex(); }
            catch (ApplicationException ex) { _logger?.Error(ex, "Failed to release single-instance mutex"); }

            _singleInstanceMutex.Dispose();
            _activationEvent.Dispose();
            _configurationEvent.Dispose();
        }
    }
}
