using CertExpiryMonitor.Models;

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
        _showFallback = showFallback ?? ((plan, settings, showDetails) =>
            FallbackNotificationWindow.Show(plan, settings, showDetails, _logger, _diagnosticEvents));
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
            // Stryker disable all: eventos estruturados nao alteram a decisao de apresentacao.
            _diagnosticEvents?.RecordInfo(
                "notification.toast_submitted",
                nameof(NotificationPresenter),
                "Notificacao toast submetida ao Windows.",
                new { channel = "windows_toast", due_count = plan.DueCertificates.Count });
            // Stryker restore all
            return new NotificationPresentationResult(true, currentSettings);
        }

        // Stryker disable all
        _logger.Info("Toast notification was not accepted by Windows; app popup fallback was used.");
        _diagnosticEvents?.RecordWarning(
            "notification.fallback_used",
            nameof(NotificationPresenter),
            "Toast do Windows nao foi aceito; popup proprio sera usado.",
            new { due_count = plan.DueCertificates.Count });
        // Stryker restore all

        if (_settingsStore.TryLoad(out var latestSettings))
        {
            currentSettings = NotificationCheckCoordinator.NormalizeSettings(latestSettings);
        }

        shown = _showFallback(plan, currentSettings, showDetails);
        // Stryker disable all
        _diagnosticEvents?.RecordInfo(
            shown ? "notification.shown" : "notification.fallback_closed",
            nameof(NotificationPresenter),
            shown ? "Popup proprio exibido e usuario abriu detalhes." : "Popup proprio fechado sem acao efetiva.",
            new { channel = "app_popup", due_count = plan.DueCertificates.Count });
        // Stryker restore all
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

    private static string FormatCount(string label, int count) => count == 0 ? string.Empty : $"{count} {label}";
}
