namespace AlgoTrader.Persistence.Entities;

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

/// <summary>Live trade record (real broker execution).</summary>
[Table("LiveTrades")]
[Index(nameof(CorrelationId), IsUnique = true)]
public class LiveTradeEntity
{
    [Key]
    public long Id { get; set; }

    public int InstrumentToken { get; set; }

    [StringLength(50)]
    public string Symbol { get; set; } = string.Empty;

    [StringLength(100)]
    public string StrategyName { get; set; } = string.Empty;

    [StringLength(20)]
    public string StrategyVersion { get; set; } = string.Empty;

    public DateTimeOffset EntryTimestampUtc { get; set; }

    public DateTimeOffset? ExitTimestampUtc { get; set; }

    /// <summary>"Buy" or "Sell".</summary>
    [StringLength(4)]
    public string Side { get; set; } = string.Empty;

    public decimal EntryPrice { get; set; }

    public decimal? ExitPrice { get; set; }

    public int Quantity { get; set; }

    public decimal? GrossPnl { get; set; }

    public decimal? Charges { get; set; }

    public decimal? NetPnl { get; set; }

    /// <summary>"Open" or "Closed".</summary>
    [StringLength(10)]
    public string Status { get; set; } = "Open";

    [StringLength(50)]
    public string CorrelationId { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Notes { get; set; }
}
