namespace AlgoTrader.Persistence.Entities;

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

/// <summary>Tradable instrument master data.</summary>
[Table("Instruments")]
public class InstrumentEntity
{
    [Key]
    public long Id { get; set; }

    /// <summary>Broker-assigned unique token.</summary>
    public int InstrumentToken { get; set; }

    [Required, StringLength(50)]
    public string Symbol { get; set; } = string.Empty;

    [Required, StringLength(10)]
    public string Exchange { get; set; } = string.Empty;

    [Required, StringLength(10)]
    public string Segment { get; set; } = string.Empty;

    [StringLength(200)]
    public string Name { get; set; } = string.Empty;

    public decimal TickSize { get; set; }

    public int LotSize { get; set; }

    public bool IsTradable { get; set; } = true;

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
