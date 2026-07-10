namespace CertExpiryMonitor.Models;

/// <summary>
/// Limites de dias para cada faixa de notificacao. Configuravel pelo usuario.
/// </summary>
public sealed class ExpiryThresholds
{
    public const int MaximumDays = 3650;

    public int Level30 { get; set; } = 30;
    public int Level15 { get; set; } = 15;
    public int Level7  { get; set; } = 7;
    public int Level1  { get; set; } = 1;

    /// <summary>Retorna o limite de dias configurado para o bucket fornecido.</summary>
    public int ForBucket(ExpiryBucket bucket) => bucket switch
    {
        ExpiryBucket.Days30 => Level30,
        ExpiryBucket.Days15 => Level15,
        ExpiryBucket.Days7  => Level7,
        ExpiryBucket.Days1  => Level1,
        _                   => (int)bucket
    };

    /// <summary>
    /// Retorna uma copia normalizada garantindo Level1 &lt; Level7 &lt; Level15 &lt; Level30
    /// e todos os valores maiores que zero.
    /// </summary>
    public ExpiryThresholds Normalized()
    {
        var l1  = Clamp(Level1, 1, MaximumDays - 3);
        var l7  = Clamp(Level7, l1 + 1, MaximumDays - 2);
        var l15 = Clamp(Level15, l7 + 1, MaximumDays - 1);
        var l30 = Clamp(Level30, l15 + 1, MaximumDays);
        return new ExpiryThresholds { Level1 = l1, Level7 = l7, Level15 = l15, Level30 = l30 };
    }

    private static int Clamp(int value, int min, int max) => Math.Min(Math.Max(value, min), max);
}
