using CertExpiryMonitor.Models;
using Xunit;

namespace CertExpiryMonitor.Tests;

public sealed class ExpiryThresholdsTests
{
    // -------------------------------------------------------------------------
    // Valores ja validos — devem ser preservados sem alteracao
    // -------------------------------------------------------------------------

    [Fact]
    public void DefaultValuesArePreservedAsIs()
    {
        var t = new ExpiryThresholds().Normalized();

        Assert.Equal(1,  t.Level1);
        Assert.Equal(7,  t.Level7);
        Assert.Equal(15, t.Level15);
        Assert.Equal(30, t.Level30);
    }

    [Fact]
    public void CustomValidOrderIsPreserved()
    {
        var t = new ExpiryThresholds { Level1 = 2, Level7 = 10, Level15 = 20, Level30 = 45 }.Normalized();

        Assert.Equal(2,  t.Level1);
        Assert.Equal(10, t.Level7);
        Assert.Equal(20, t.Level15);
        Assert.Equal(45, t.Level30);
    }

    // -------------------------------------------------------------------------
    // Valores invalidos — Normalized() deve corrigir a ordem
    // -------------------------------------------------------------------------

    [Fact]
    public void AllZerosProducesStrictlyAscendingSequenceStartingAt1()
    {
        var t = new ExpiryThresholds { Level1 = 0, Level7 = 0, Level15 = 0, Level30 = 0 }.Normalized();

        Assert.Equal(1, t.Level1);
        Assert.True(t.Level7  > t.Level1,  "Level7 deve ser maior que Level1");
        Assert.True(t.Level15 > t.Level7,  "Level15 deve ser maior que Level7");
        Assert.True(t.Level30 > t.Level15, "Level30 deve ser maior que Level15");
    }

    [Fact]
    public void NegativeValuesAreTreatedAsZero()
    {
        var t = new ExpiryThresholds { Level1 = -5, Level7 = -1, Level15 = 0, Level30 = -100 }.Normalized();

        Assert.True(t.Level1  >= 1);
        Assert.True(t.Level7  > t.Level1);
        Assert.True(t.Level15 > t.Level7);
        Assert.True(t.Level30 > t.Level15);
    }

    [Fact]
    public void InvertedOrderIsFixed()
    {
        // Usuario digitou os campos na ordem errada: Level1 > Level30
        var t = new ExpiryThresholds { Level1 = 30, Level7 = 15, Level15 = 7, Level30 = 1 }.Normalized();

        Assert.True(t.Level1  >= 1);
        Assert.True(t.Level7  > t.Level1);
        Assert.True(t.Level15 > t.Level7);
        Assert.True(t.Level30 > t.Level15);
    }

    [Fact]
    public void AllEqualValuesProduceStrictlyIncreasingSequence()
    {
        var t = new ExpiryThresholds { Level1 = 10, Level7 = 10, Level15 = 10, Level30 = 10 }.Normalized();

        Assert.True(t.Level1  >= 1);
        Assert.True(t.Level7  > t.Level1);
        Assert.True(t.Level15 > t.Level7);
        Assert.True(t.Level30 > t.Level15);
    }

    [Fact]
    public void NormalizedIsIdempotent()
    {
        var original = new ExpiryThresholds { Level1 = 5, Level7 = 3, Level15 = 20, Level30 = 10 };
        var once  = original.Normalized();
        var twice = once.Normalized();

        Assert.Equal(once.Level1,  twice.Level1);
        Assert.Equal(once.Level7,  twice.Level7);
        Assert.Equal(once.Level15, twice.Level15);
        Assert.Equal(once.Level30, twice.Level30);
    }

    // -------------------------------------------------------------------------
    // ForBucket — deve retornar o nivel correto para cada bucket
    // -------------------------------------------------------------------------

    [Fact]
    public void ForBucketReturnsCorrectThresholdForEachBucket()
    {
        var t = new ExpiryThresholds { Level1 = 2, Level7 = 10, Level15 = 20, Level30 = 45 };

        Assert.Equal(2,  t.ForBucket(ExpiryBucket.Days1));
        Assert.Equal(10, t.ForBucket(ExpiryBucket.Days7));
        Assert.Equal(20, t.ForBucket(ExpiryBucket.Days15));
        Assert.Equal(45, t.ForBucket(ExpiryBucket.Days30));
    }
}
