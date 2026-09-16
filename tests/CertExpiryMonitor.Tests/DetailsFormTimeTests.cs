using CertExpiryMonitor.Services;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class DetailsFormTimeTests
{
    [Fact]
    public void DetailsFormMinimumClientSizeFitsLowResolutionRdp()
    {
        Assert.True(DetailsForm.MinimumClientWidth <= 560);
        Assert.True(DetailsForm.MinimumClientHeight <= 420);
    }

    [Theory]
    [InlineData("00:00", 0, 0)]
    [InlineData("09:05", 9, 5)]
    [InlineData("23:59", 23, 59)]
    public void TryParseDailyTimeAcceptsFullDayRange(string text, int hours, int minutes)
    {
        var ok = DetailsForm.TryParseDailyTime(text, out var time);

        Assert.True(ok);
        Assert.Equal(new TimeSpan(hours, minutes, 0), time);
    }

    [Theory]
    [InlineData("")]
    [InlineData("24:00")]
    [InlineData("23:60")]
    [InlineData("-1:00")]
    [InlineData("9")]
    [InlineData("9:00")]
    [InlineData("12:3")]
    [InlineData("12:__")]
    [InlineData(" 09:00 ")]
    public void TryParseDailyTimeRejectsInvalidTimes(string text)
    {
        Assert.False(DetailsForm.TryParseDailyTime(text, out _));
    }

    [Theory]
    [InlineData(0, 0, "00:00")]
    [InlineData(23, 59, "23:59")]
    public void FormatDailyTimeUsesTwentyFourHourClock(int hours, int minutes, string expected)
    {
        Assert.Equal(expected, DetailsForm.FormatDailyTime(new TimeSpan(hours, minutes, 0)));
    }
}
