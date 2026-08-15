namespace AlgoTrader.Persistence.Entities;

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

/// <summary>Risk engine events (daily loss breach, kill switch, etc.).</summary>
[Table("RiskEvents")]
[Index(nameof(EventTimestampUtc))]
public class RiskEventEntity
{
    [Key]
    public long Id { get; set; }

    public DateTimeOffset EventTimestampUtc { get; set; }

    /// <summary>"MaxDailyLossBreached", "KillSwitchActivated", "MaxTradesPerDayBreached", etc.</summary>
    [StringLength(50)]
    public string EventType { get; set; } = string.Empty;

    /// <summary>"Info", "Warning", "Critical".</summary>
    [StringLength(20)]
    public string Severity { get; set; } = string.Empty;

    public string Details { get; set; } = string.Empty;

    [StringLength(50)]
    public string? CorrelationId { get; set; }

    [StringLength(100)]
    public string? StrategyName { get; set; }
}
