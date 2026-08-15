namespace AlgoTrader.Persistence.Entities;

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

/// <summary>Single tick from a live market data feed.</summary>
[Table("MarketTicks")]
[Index(nameof(InstrumentToken), nameof(TimestampUtc))]
public class MarketTickEntity
{
    [Key]
    public long Id { get; set; }

    public int InstrumentToken { get; set; }

    public DateTimeOffset TimestampUtc { get; set; }

    public decimal LastPrice { get; set; }

    public decimal BidPrice { get; set; }

    public decimal AskPrice { get; set; }

    public long Volume { get; set; }
}
