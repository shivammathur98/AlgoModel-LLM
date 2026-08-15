namespace AlgoTrader.Persistence.Entities;

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

/// <summary>Individual trade within a backtest run.</summary>
[Table("BacktestTrades")]
[Index(nameof(BacktestRunId))]
public class BacktestTradeEntity
{
    [Key]
    public long Id { get; set; }

    public long BacktestRunId { get; set; }

    public int InstrumentToken { get; set; }

    [StringLength(50)]
    public string Symbol { get; set; } = string.Empty;

    public DateTimeOffset EntryTimestampUtc { get; set; }

    public DateTimeOffset ExitTimestampUtc { get; set; }

    /// <summary>"Buy" or "Sell".</summary>
    [StringLength(4)]
    public string Side { get; set; } = string.Empty;

    public decimal EntryPrice { get; set; }

    public decimal ExitPrice { get; set; }

    public int Quantity { get; set; }

    public decimal GrossPnl { get; set; }

    public decimal Charges { get; set; }

    public decimal Slippage { get; set; }

    public decimal NetPnl { get; set; }

    public int HoldingMinutes { get; set; }

    /// <summary>"Target", "StopLoss", "TimeExit", "EODExit".</summary>
    [StringLength(20)]
    public string ExitReason { get; set; } = string.Empty;

    [StringLength(50)]
    public string CorrelationId { get; set; } = string.Empty;

    // Navigation
    public BacktestRunEntity? BacktestRun { get; set; }
}
