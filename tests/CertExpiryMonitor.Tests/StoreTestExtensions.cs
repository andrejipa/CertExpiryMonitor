using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

internal static class StoreTestExtensions
{
    internal static AppSettings Load(this JsonSettingsStore store)
    {
        Assert.True(store.TryLoad(out var settings));
        return settings;
    }

    internal static Dictionary<string, CertificateStateRecord> Load(this JsonStateStore store)
    {
        Assert.True(store.TryLoad(out var state));
        return state;
    }
}
