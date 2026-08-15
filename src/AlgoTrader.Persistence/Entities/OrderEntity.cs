namespace AlgoTrader.Persistence.Entities;

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

/// <summary>Local order record tracking the full lifecycle.</summary>
[Table("Orders")]
[Index(nameof(BrokerOrderId))]
[Index(nameof(CorrelationId))]
public class OrderEntity
{
    [Key]
    public long Id { get; set; }

    /// <summary>Broker-assigned order ID (null until submitted).</summary>
    [StringLength(100)]
    public string? BrokerOrderId { get; set; }

    public int InstrumentToken { get; set; }

    [StringLength(50)]
    public string Symbol { get; set; } = string.Empty;

    [StringLength(10)]
    public string Exchange { get; set; } = string.Empty;

    /// <summary>"Buy" or "Sell".</summary>
    [StringLength(4)]
    public string Side { get; set; } = string.Empty;

    /// <summary>"Market", "Limit", "StopLoss", "StopLossLimit".</summary>
    [StringLength(20)]
    public string Type { get; set; } = string.Empty;

    /// <summary>"Day" or "Ioc".</summary>
    [StringLength(10)]
    public string Validity { get; set; } = string.Empty;

    /// <summary>"Intraday" or "Delivery".</summary>
    [StringLength(10)]
    public string Product { get; set; } = string.Empty;

    public int Quantity { get; set; }

    public decimal? Price { get; set; }

    public decimal? TriggerPrice { get; set; }

    public int FilledQuantity { get; set; }

    public decimal? AverageFillPrice { get; set; }

    /// <summary>"New", "Pending", "Submitted", "Open", "PartiallyFilled", "Filled", "CancelPending", "Cancelled", "Rejected", "Failed".</summary>
    [StringLength(20)]
    public string State { get; set; } = string.Empty;

    [StringLength(500)]
    public string? RejectionReason { get; set; }

    [StringLength(100)]
    public string? Tag { get; set; }

    [StringLength(50)]
    public string CorrelationId { get; set; } = string.Empty;

    [StringLength(100)]
    public string? StrategyName { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset LastUpdatedAtUtc { get; set; }

    public DateTimeOffset? FilledAtUtc { get; set; }

    // Navigation
    public ICollection<OrderExecutionEntity> Executions { get; set; } = new List<OrderExecutionEntity>();
}
