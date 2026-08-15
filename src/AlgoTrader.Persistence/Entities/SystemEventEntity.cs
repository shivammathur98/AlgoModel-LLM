namespace AlgoTrader.Persistence.Entities;

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

/// <summary>System-level operational events (startup, shutdown, WebSocket connect, reconciliation).</summary>
[Table("SystemEvents")]
[Index(nameof(EventTimestampUtc))]
public class SystemEventEntity
{
    [Key]
    public long Id { get; set; }

    public DateTimeOffset EventTimestampUtc { get; set; }

    /// <summary>"Startup", "Shutdown", "WebSocketConnected", "Reconciliation", etc.</summary>
    [StringLength(50)]
    public string EventType { get; set; } = string.Empty;

    /// <summary>"Info", "Warning", "Critical".</summary>
    [StringLength(20)]
    public string Severity { get; set; } = string.Empty;

    public string Details { get; set; } = string.Empty;

    [StringLength(50)]
    public string? CorrelationId { get; set; }
}
