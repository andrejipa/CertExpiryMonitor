namespace CertExpiryMonitor.Models;

/// <summary>
/// Limites de dias para cada faixa de notificacao. Configuravel pelo usuario.
/// </summary>
public sealed class ExpiryThresholds
{
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
        var l1  = Math.Max(1, Level1);
        var l7  = Math.Max(l1 + 1, Level7);
        var l15 = Math.Max(l7 + 1, Level15);
        var l30 = Math.Max(l15 + 1, Level30);
        return new ExpiryThresholds { Level1 = l1, Level7 = l7, Level15 = l15, Level30 = l30 };
    }
}
