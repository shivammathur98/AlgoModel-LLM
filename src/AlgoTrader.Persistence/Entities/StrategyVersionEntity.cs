namespace AlgoTrader.Persistence.Entities;

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

/// <summary>Versioned strategy with parameter snapshot.</summary>
[Table("StrategyVersions")]
[Index(nameof(StrategyId), nameof(Version), IsUnique = true)]
public class StrategyVersionEntity
{
    [Key]
    public long Id { get; set; }

    public long StrategyId { get; set; }

    [Required, StringLength(20)]
    public string Version { get; set; } = string.Empty;

    /// <summary>JSON-serialized strategy parameters.</summary>
    public string ParametersJson { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; }

    [StringLength(500)]
    public string? Notes { get; set; }

    // Navigation
    public StrategyEntity? Strategy { get; set; }
}
