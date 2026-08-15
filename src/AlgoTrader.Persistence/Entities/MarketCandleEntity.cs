namespace AlgoTrader.Persistence.Entities;

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

/// <summary>OHLCV candle for a specific timeframe.</summary>
[Table("MarketCandles")]
[Index(nameof(InstrumentToken), nameof(Timeframe), nameof(TimestampUtc), IsUnique = true)]
public class MarketCandleEntity
{
    [Key]
    public long Id { get; set; }

    public int InstrumentToken { get; set; }

    [Required, StringLength(50)]
    public string Symbol { get; set; } = string.Empty;

    [Required, StringLength(10)]
    public string Exchange { get; set; } = string.Empty;

    /// <summary>Timeframe as string: "Minute1", "Minute5", "Daily", etc.</summary>
    [Required, StringLength(20)]
    public string Timeframe { get; set; } = string.Empty;

    /// <summary>Bar start time (UTC).</summary>
    public DateTimeOffset TimestampUtc { get; set; }

    public decimal Open { get; set; }

    public decimal High { get; set; }

    public decimal Low { get; set; }

    public decimal Close { get; set; }

    public long Volume { get; set; }
}
