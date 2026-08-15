namespace AlgoTrader.Persistence.Entities;

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

/// <summary>Individual fill or partial fill for an order.</summary>
[Table("OrderExecutions")]
[Index(nameof(OrderId))]
public class OrderExecutionEntity
{
    [Key]
    public long Id { get; set; }

    public long OrderId { get; set; }

    public DateTimeOffset ExecutionTimestampUtc { get; set; }

    public int FilledQuantity { get; set; }

    public decimal FillPrice { get; set; }

    /// <summary>Broker's execution reference ID.</summary>
    [StringLength(100)]
    public string? BrokerExecutionId { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }

    // Navigation
    public OrderEntity? Order { get; set; }
}
