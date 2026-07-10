using System.Xml.Linq;
using CertExpiryMonitor.Models;
using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class ToastAndActivationTests
{
    [Fact]
    public void BuildToastXmlProducesCompactToastWithoutActionButtons()
    {
        var plan = new NotificationPlan
        {
            DueCertificates =
            [
                new CertificateDueNotification(
                    new CertificateSnapshot("AA\"&<>", "CN=A", "CN=Issuer", DateTime.Today.AddDays(5), "01"),
                    ExpiryBucket.Days7,
                    5)
            ]
        };

        var xml = ToastNotifierService.BuildToastXml(plan, new ExpiryThresholds().Normalized());

        var document = XDocument.Parse(xml);
        var actions = document.Descendants("action").ToList();

        Assert.Equal("toast", document.Root?.Name.LocalName);
        Assert.Equal(ToastNotifierService.DetailsProtocolUri, (string?)document.Root?.Attribute("launch"));
        Assert.Equal("protocol", (string?)document.Root?.Attribute("activationType"));
        Assert.Equal("urgent", (string?)document.Root?.Attribute("scenario"));
        Assert.Empty(actions);

        var texts = document.Descendants("text").Select(text => text.Value).ToArray();
        Assert.Equal("Certificados vencendo", texts[0]);
        Assert.Equal("1 certificado requer atenção.", texts[1]);
    }

    [Fact]
    public void BuildToastXmlUsesCustomThresholdLabelsInBucketSummary()
    {
        var thresholds = new ExpiryThresholds { Level1 = 2, Level7 = 5, Level15 = 20, Level30 = 45 }.Normalized();
        var plan = new NotificationPlan
        {
            DueCertificates =
            [
                new CertificateDueNotification(
                    new CertificateSnapshot("AA", "CN=A", "CN=Issuer", DateTime.Today.AddDays(40), "01"),
                    ExpiryBucket.Days30,
                    40),
                new CertificateDueNotification(
                    new CertificateSnapshot("BB", "CN=B", "CN=Issuer", DateTime.Today.AddDays(4), "02"),
                    ExpiryBucket.Days7,
                    4)
            ]
        };

        var xml = ToastNotifierService.BuildToastXml(plan, thresholds);
        var texts = XDocument.Parse(xml).Descendants("text").Select(text => text.Value).ToArray();

        Assert.Equal("45d: 1 | 5d: 1", texts[2]);
    }

    [Fact]
    public void BuildToastXmlUsesPluralSummaryAndExactCompactBucketLine()
    {
        var thresholds = new ExpiryThresholds { Level1 = 1, Level7 = 7, Level15 = 15, Level30 = 30 }.Normalized();
        var plan = new NotificationPlan
        {
            DueCertificates =
            [
                new CertificateDueNotification(
                    new CertificateSnapshot("AA", "CN=A", "CN=Issuer", DateTime.Today.AddDays(28), "01"),
                    ExpiryBucket.Days30,
                    28),
                new CertificateDueNotification(
                    new CertificateSnapshot("BB", "CN=B", "CN=Issuer", DateTime.Today.AddDays(13), "02"),
                    ExpiryBucket.Days15,
                    13),
                new CertificateDueNotification(
                    new CertificateSnapshot("CC", "CN=C", "CN=Issuer", DateTime.Today.AddDays(5), "03"),
                    ExpiryBucket.Days7,
                    5),
                new CertificateDueNotification(
                    new CertificateSnapshot("DD", "CN=D", "CN=Issuer", DateTime.Today.AddDays(1), "04"),
                    ExpiryBucket.Days1,
                    1)
            ]
        };

        var texts = XDocument.Parse(ToastNotifierService.BuildToastXml(plan, thresholds))
            .Descendants("text")
            .Select(text => text.Value)
            .ToArray();

        Assert.Equal("4 certificados requerem atenção.", texts[1]);
        Assert.Equal("30d: 1 | 15d: 1 | 7d: 1 | 1d: 1", texts[2]);
    }

    [Fact]
    public void BuildToastXmlKeepsActionsOutOfVisibleToastArea()
    {
        var plan = new NotificationPlan
        {
            DueCertificates =
            [
                new CertificateDueNotification(
                    new CertificateSnapshot("AA", "CN=A", "CN=Issuer", DateTime.Today.AddDays(5), "01"),
                    ExpiryBucket.Days7,
                    5),
                new CertificateDueNotification(
                    new CertificateSnapshot("BB", "CN=B", "CN=Issuer", DateTime.Today.AddDays(10), "02"),
                    ExpiryBucket.Days15,
                    10)
            ]
        };

        var xml = ToastNotifierService.BuildToastXml(plan, new ExpiryThresholds().Normalized());

        var document = XDocument.Parse(xml);

        Assert.Empty(document.Descendants("action"));
        Assert.Equal(ToastNotifierService.DetailsProtocolUri, (string?)document.Root?.Attribute("launch"));
        Assert.Equal("protocol", (string?)document.Root?.Attribute("activationType"));
    }

    [Fact]
    public void BuildProtocolCommandQuotesExecutableAndForwardsUriToDetailsFlag()
    {
        var command = ToastNotifierService.BuildProtocolCommand(
            @"C:\Users\Fulano\AppData\Local\Programs\CertExpiryMonitor\CertExpiryMonitor.exe");

        Assert.Equal(
            "\"C:\\Users\\Fulano\\AppData\\Local\\Programs\\CertExpiryMonitor\\CertExpiryMonitor.exe\" --details \"%1\"",
            command);
    }

    [Fact]
    public void BuildProtocolCommandRejectsUnsafeExecutablePath()
    {
        Assert.Throws<ArgumentException>(() => ToastNotifierService.BuildProtocolCommand(" "));
        Assert.Throws<ArgumentException>(() => ToastNotifierService.BuildProtocolCommand("C:\\bad\"path\\app.exe"));
        Assert.Throws<ArgumentException>(() => ToastNotifierService.BuildProtocolCommand("C:\\bad\rpath\\app.exe"));
    }

    [Fact]
    public void BuildToastXmlHonorsMutedNotificationSetting()
    {
        var plan = new NotificationPlan
        {
            DueCertificates =
            [
                new CertificateDueNotification(
                    new CertificateSnapshot("AA", "CN=A", "CN=Issuer", DateTime.Today.AddDays(5), "01"),
                    ExpiryBucket.Days7,
                    5)
            ]
        };

        var document = XDocument.Parse(ToastNotifierService.BuildToastXml(
            plan,
            new ExpiryThresholds().Normalized(),
            soundEnabled: false));
        var audio = Assert.Single(document.Descendants("audio"));

        Assert.Equal("true", (string?)audio.Attribute("silent"));
    }

    [Fact]
    public void BuildToastXmlOmitsSilentAudioWhenSoundIsEnabled()
    {
        var plan = new NotificationPlan
        {
            DueCertificates =
            [
                new CertificateDueNotification(
                    new CertificateSnapshot("AA", "CN=A", "CN=Issuer", DateTime.Today.AddDays(5), "01"),
                    ExpiryBucket.Days7,
                    5)
            ]
        };

        var document = XDocument.Parse(ToastNotifierService.BuildToastXml(
            plan,
            new ExpiryThresholds().Normalized(),
            soundEnabled: true));

        Assert.Empty(document.Descendants("audio"));
    }

    [Fact]
    public void ParseArgumentsToleratesMalformedAndDuplicateValues()
    {
        var parsed = TrayApplicationContext.ParseArguments("action=view-details&ignored&=empty&thumbprint=%&action=dismiss-one");

        Assert.Equal("dismiss-one", parsed["action"]);
        Assert.Equal("%", parsed["thumbprint"]);
        Assert.False(parsed.ContainsKey(string.Empty));
    }

    [Fact]
    public void ParseArgumentsTrimsKeysWithoutTrimmingDecodedValues()
    {
        var parsed = TrayApplicationContext.ParseArguments(" action =%20valor%20%20");

        Assert.True(parsed.ContainsKey("action"));
        Assert.Equal(" valor  ", parsed["action"]);
        Assert.False(parsed.ContainsKey(" action "));
    }

    [Fact]
    public void ParseArgumentsDecodesKeys()
    {
        var parsed = TrayApplicationContext.ParseArguments("%61ction=view-details&thumb%70rint=AA%20BB");

        Assert.Equal("view-details", parsed["action"]);
        Assert.Equal("AA BB", parsed["thumbprint"]);
    }

    [Fact]
    public void ParseArgumentsFallsBackWhenEncodedKeyIsMalformed()
    {
        var parsed = TrayApplicationContext.ParseArguments("action%=view-details");

        Assert.Equal("view-details", parsed["action%"]);
    }

    [Fact]
    public void ParseArgumentsReturnsEmptyDictionaryForEmptyInput()
    {
        Assert.Empty(TrayApplicationContext.ParseArguments(null));
        Assert.Empty(TrayApplicationContext.ParseArguments(""));
        Assert.Empty(TrayApplicationContext.ParseArguments("   "));
    }
}
