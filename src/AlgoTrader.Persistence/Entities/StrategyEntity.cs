namespace AlgoTrader.Persistence.Entities;

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

/// <summary>Strategy definition.</summary>
[Table("Strategies")]
[Index(nameof(Name), IsUnique = true)]
public class StrategyEntity
{
    [Key]
    public long Id { get; set; }

    [Required, StringLength(100)]
    public string Name { get; set; } = string.Empty;

    [StringLength(500)]
    public string? Description { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public bool IsActive { get; set; } = true;

    // Navigation
    public ICollection<StrategyVersionEntity> Versions { get; set; } = new List<StrategyVersionEntity>();
}
