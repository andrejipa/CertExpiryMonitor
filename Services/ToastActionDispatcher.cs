using CertExpiryMonitor.Models;

namespace CertExpiryMonitor.Services;

internal sealed class ToastActionDispatcher
{
    private readonly Func<NotificationPlan?> _getLastPlan;
    private readonly CertificateStateActions _stateActions;
    private readonly FileLogger _logger;
    private readonly DiagnosticEventStore? _diagnosticEvents;

    public ToastActionDispatcher(
        Func<NotificationPlan?> getLastPlan,
        CertificateStateActions stateActions,
        FileLogger logger,
        DiagnosticEventStore? diagnosticEvents = null)
    {
        _getLastPlan = getLastPlan;
        _stateActions = stateActions;
        _logger = logger;
        _diagnosticEvents = diagnosticEvents;
    }

    public void Dispatch(string? arguments, Action showConfiguration, Action showDetails)
    {
        ArgumentNullException.ThrowIfNull(showConfiguration);
        ArgumentNullException.ThrowIfNull(showDetails);

        var values = ToastActionArgumentParser.Parse(arguments);
        var action = values.GetValueOrDefault("action", "view-details");
        _diagnosticEvents?.RecordInfo(
            "toast.activated",
            nameof(ToastActionDispatcher),
            "Usuario ativou acao de toast.",
            new { action });

        switch (action)
        {
            case "configure-time":
                Execute(showConfiguration, "Failed to open settings from toast");
                break;
            case "dismiss-one" when values.TryGetValue("thumbprint", out var thumbprint):
                Execute(() => _stateActions.DismissOne(thumbprint), "Failed to dismiss certificate from toast");
                break;
            case "dismiss-all" when values.TryGetValue("thumbprints", out var thumbprints):
                Execute(
                    () => _stateActions.DismissAll(thumbprints.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
                    "Failed to dismiss certificates from toast");
                break;
            case "dismiss-all":
                Execute(DismissCurrentPlan, "Failed to dismiss current certificates");
                break;
            case "view-details":
                Execute(showDetails, "Failed to open details from toast");
                break;
            case "remind-later":
            default:
                break;
        }
    }

    private void DismissCurrentPlan()
    {
        var lastPlan = _getLastPlan();
        if (lastPlan is null) return;

        _ = _stateActions.DismissAll(lastPlan.DueCertificates.Select(item => item.Certificate.Thumbprint));
    }

    private void Execute(Action action, string errorMessage)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, errorMessage);
            _diagnosticEvents?.RecordError(ex, "toast.action_failed", nameof(ToastActionDispatcher), errorMessage);
        }
    }
}
