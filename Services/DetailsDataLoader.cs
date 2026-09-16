using CertExpiryMonitor.Models;

namespace CertExpiryMonitor.Services;

/// <summary>Lê cada fonte independentemente, sem transformar falhas em dados recuperados.</summary>
internal sealed class DetailsDataLoader(
    JsonSettingsStore settingsStore,
    JsonStateStore stateStore,
    CertificateReader certificateReader)
{
    public DetailsLoadResult Load()
    {
        var settingsAvailable = settingsStore.TryLoad(out var settings);
        var stateAvailable = stateStore.TryLoad(out var state);
        var certificateRead = certificateReader.ReadCurrentUserPersonalCertificates();
        return new DetailsLoadResult(
            certificateRead,
            state,
            settingsAvailable ? NotificationCheckCoordinator.NormalizeSettings(settings) : null,
            stateAvailable);
    }
}
