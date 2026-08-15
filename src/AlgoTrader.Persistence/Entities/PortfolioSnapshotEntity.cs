namespace AlgoTrader.Persistence.Entities;

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

/// <summary>Periodic snapshot of portfolio state.</summary>
[Table("PortfolioSnapshots")]
[Index(nameof(SnapshotTimestampUtc))]
public class PortfolioSnapshotEntity
{
    [Key]
    public long Id { get; set; }

    public DateTimeOffset SnapshotTimestampUtc { get; set; }

    public decimal TotalCapital { get; set; }

    public decimal AvailableCash { get; set; }

    public decimal InvestedCapital { get; set; }

    public decimal UnrealizedPnl { get; set; }

    public decimal RealizedPnl { get; set; }

    public int OpenPositionCount { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }
}
