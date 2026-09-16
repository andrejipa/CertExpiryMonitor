using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class CertificateIdentityTests
{
    [Theory]
    [InlineData("aa bb\tcc\r\n", "AABBCC")]
    [InlineData("aa\u200Bbb\u2060cc\uFEFF", "AABBCC")]
    [InlineData("", "")]
    [InlineData(" \u200E", "")]
    public void NormalizesIdentityIndependentlyOfStorage(string input, string expected)
    {
        Assert.Equal(expected, CertificateIdentity.NormalizeThumbprint(input));
        Assert.Equal(expected, JsonStateStore.NormalizeThumbprint(input));
        Assert.Equal(expected, CertificateIdentity.NormalizeThumbprint(expected));
    }

    [Fact]
    public void RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => CertificateIdentity.NormalizeThumbprint(null!));
        Assert.Throws<ArgumentNullException>(() => JsonStateStore.NormalizeThumbprint(null!));
    }
}
