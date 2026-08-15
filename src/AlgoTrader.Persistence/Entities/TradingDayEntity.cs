namespace AlgoTrader.Persistence.Entities;

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

/// <summary>Trading calendar: marks each date as trading or non-trading.</summary>
[Table("TradingDays")]
[Index(nameof(Date), IsUnique = true)]
public class TradingDayEntity
{
    [Key]
    public long Id { get; set; }

    public DateOnly Date { get; set; }

    public bool IsTradingDay { get; set; }

    /// <summary>Session start time (UTC). Null on non-trading days.</summary>
    public DateTimeOffset? SessionStartUtc { get; set; }

    /// <summary>Session end time (UTC). Null on non-trading days.</summary>
    public DateTimeOffset? SessionEndUtc { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }
}
