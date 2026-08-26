namespace AlgoTrader.Persistence.Entities;

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

/// <summary>Open or closed position tracked by the platform.</summary>
[Table("Positions")]
[Index(nameof(CorrelationId), IsUnique = true)]
[Index(nameof(Status))]
public class PositionEntity
{
    [Key]
    public long Id { get; set; }

    [Timestamp]
    public byte[]? RowVersion { get; set; }

    public int InstrumentToken { get; set; }

    [StringLength(50)]
    public string Symbol { get; set; } = string.Empty;

    [StringLength(100)]
    public string StrategyName { get; set; } = string.Empty;

    /// <summary>Positive for long, negative for short.</summary>
    public int Quantity { get; set; }

    public decimal AveragePrice { get; set; }

    public DateTimeOffset OpenedAtUtc { get; set; }

    public DateTimeOffset? ClosedAtUtc { get; set; }

    public decimal? StopPrice { get; set; }

    public decimal? TargetPrice { get; set; }

    public decimal? RealizedPnl { get; set; }

    /// <summary>"Open" or "Closed".</summary>
    [StringLength(10)]
    public string Status { get; set; } = "Open";

    [StringLength(50)]
    public string CorrelationId { get; set; } = string.Empty;
}
