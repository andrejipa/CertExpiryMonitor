using System.Windows.Forms;
using CertExpiryMonitor.Services;

namespace CertExpiryMonitor;

internal static class Program
{
    private static Mutex? _singleInstanceMutex;
    private static EventWaitHandle? _activationEvent;
    private static EventWaitHandle? _configurationEvent;
    private static FileLogger? _logger;

    [STAThread]
    private static void Main(string[] args)
    {
        _activationEvent    = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CertExpiryMonitor.CurrentUser.Activate");
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

        var paths        = new AppPaths();
        var logger       = new FileLogger(paths);
        _logger          = logger;

        var version = System.Reflection.Assembly.GetExecutingAssembly()
                          .GetName().Version?.ToString() ?? "unknown";
        logger.Info($"CertExpiryMonitor v{version} starting (args: [{string.Join(", ", args)}])");

        Application.ThreadException += (_, e) =>
            logger.Error(e.Exception, "Unhandled UI exception");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                _logger?.Error(ex, "Unhandled application exception");
            }
        };

        // Composicao dos servicos
        var stateStore          = new JsonStateStore(paths, logger);
        var settingsStore       = new JsonSettingsStore(paths, logger);
        var certificateReader   = new CertificateReader(logger);
        var expiryEvaluator     = new ExpiryEvaluator();
        var checkService        = new CertificateCheckService(settingsStore, stateStore, certificateReader, expiryEvaluator, logger);
        var notifier            = new ToastNotifierService(logger);
        var startup             = new StartupRegistration(logger);
        var telemetry           = new TelemetryService(paths, logger);

        var currentSettings = settingsStore.Load();

        // Aplica preferencias de logging (formato Text/Json + Event Log) imediatamente,
        // antes do primeiro write. As mensagens de "starting" acima ainda vao em texto;
        // tudo depois deste ponto respeita o que o usuario configurou.
        logger.ApplySettings(currentSettings);

        if (currentSettings.StartupEnabled) startup.EnsureRegistered();
        else startup.Remove();

        notifier.EnsureShortcut();

        using var context = new TrayApplicationContext(
            args,
            _activationEvent,
            _configurationEvent,
            settingsStore,
            stateStore,
            certificateReader,
            expiryEvaluator,
            checkService,
            notifier,
            startup,
            telemetry,
            logger,
            paths);

        try
        {
            Application.Run(context);
        }
        catch (Exception ex)
        {
            logger.Error(ex, "Application terminated unexpectedly");
        }
        finally
        {
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
