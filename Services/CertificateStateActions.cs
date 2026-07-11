using CertExpiryMonitor.Models;

namespace CertExpiryMonitor.Services;

internal sealed class CertificateStateActions
{
    private readonly JsonStateStore _stateStore;
    private readonly ExpiryEvaluator _expiryEvaluator;
    private readonly TelemetryService _telemetry;
    private readonly FileLogger _logger;
    private readonly DiagnosticEventStore? _diagnosticEvents;

    public CertificateStateActions(
        JsonStateStore stateStore,
        ExpiryEvaluator expiryEvaluator,
        TelemetryService telemetry,
        FileLogger logger,
        DiagnosticEventStore? diagnosticEvents = null)
    {
        _stateStore = stateStore;
        _expiryEvaluator = expiryEvaluator;
        _telemetry = telemetry;
        _logger = logger;
        _diagnosticEvents = diagnosticEvents;
    }

    public bool DismissOne(string thumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);
        if (!TryLoadState("dismiss certificate", out var state)) return false;

        _expiryEvaluator.DismissCertificate(thumbprint, state);
        if (!TrySaveState(state, $"dismiss certificate {ShortThumbprint(thumbprint)}")) return false;

        _telemetry.Increment(t => t.DismissOne++);
        _logger.Info($"User dismissed certificate {ShortThumbprint(thumbprint)}.");
        _diagnosticEvents?.RecordInfo(
            "certificate.dismiss_one",
            nameof(CertificateStateActions),
            "Usuario marcou certificado para nao lembrar.",
            new { thumbprint });
        return true;
    }

    public bool DismissAll(IEnumerable<string> thumbprints)
    {
        ArgumentNullException.ThrowIfNull(thumbprints);
        var thumbprintList = thumbprints
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (thumbprintList.Length == 0) return true;
        if (!TryLoadState("dismiss certificates", out var state)) return false;

        _expiryEvaluator.DismissCertificates(thumbprintList, state);
        if (!TrySaveState(state, "dismiss certificates")) return false;

        _telemetry.Increment(t => t.DismissAll++);
        _logger.Info("User dismissed certificates from notification action.");
        _diagnosticEvents?.RecordInfo(
            "certificate.dismiss_all",
            nameof(CertificateStateActions),
            "Usuario marcou certificados para nao lembrar.",
            new { count = thumbprintList.Length });
        return true;
    }

    public bool RestoreOne(string thumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);
        if (!TryLoadState("restore certificate", out var state)) return false;

        _expiryEvaluator.RestoreCertificate(thumbprint, state);
        if (!TrySaveState(state, $"restore certificate {ShortThumbprint(thumbprint)}")) return false;

        _telemetry.Increment(t => t.Restore++);
        _logger.Info($"User restored certificate {ShortThumbprint(thumbprint)}.");
        _diagnosticEvents?.RecordInfo(
            "certificate.restore",
            nameof(CertificateStateActions),
            "Usuario voltou a lembrar certificado.",
            new { thumbprint });
        return true;
    }

    private bool TryLoadState(string action, out Dictionary<string, CertificateStateRecord> state)
    {
        if (_stateStore.TryLoad(out state)) return true;

        _logger.Error(
            new IOException("certificate-state.json read failed"),
            $"Failed to {action} because state could not be read");
        return false;
    }

    private bool TrySaveState(Dictionary<string, CertificateStateRecord> state, string action)
    {
        if (_stateStore.Save(state)) return true;

        _logger.Error(new IOException("certificate-state.json save failed"), $"Failed to {action}");
        return false;
    }

    private static string ShortThumbprint(string thumbprint) =>
        thumbprint.Length <= 8 ? thumbprint : thumbprint[^8..];
}
