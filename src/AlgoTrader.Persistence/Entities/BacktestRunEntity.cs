namespace AlgoTrader.Persistence.Entities;

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

/// <summary>Backtest execution record with aggregated metrics.</summary>
[Table("BacktestRuns")]
[Index(nameof(RunCorrelationId), IsUnique = true)]
public class BacktestRunEntity
{
    [Key]
    public long Id { get; set; }

    public long StrategyVersionId { get; set; }

    [Required, StringLength(50)]
    public string RunCorrelationId { get; set; } = string.Empty;

    [Required, StringLength(64)]
    public string ParametersHash { get; set; } = string.Empty;

    [Required, StringLength(64)]
    public string DataFingerprint { get; set; } = string.Empty;

    [Required, StringLength(4000)]
    public string Universe { get; set; } = string.Empty;

    public DateTimeOffset DataStartUtc { get; set; }

    public DateTimeOffset DataEndUtc { get; set; }

    public decimal InitialCapital { get; set; }

    public decimal FinalCapital { get; set; }

    /// <summary>"Ideal", "Conservative", "Realistic".</summary>
    [StringLength(20)]
    public string ExecutionModel { get; set; } = string.Empty;

    [Required, StringLength(100)]
    public string CostModel { get; set; } = string.Empty;

    [Required, StringLength(100)]
    public string SlippageModel { get; set; } = string.Empty;

    public int TotalTrades { get; set; }

    public int WinningTrades { get; set; }

    public int LosingTrades { get; set; }

    public decimal WinRate { get; set; }

    public decimal GrossPnl { get; set; }

    public decimal TotalCharges { get; set; }

    public decimal TotalSlippage { get; set; }

    public decimal NetPnl { get; set; }

    public decimal MaxDrawdown { get; set; }

    public decimal? ProfitFactor { get; set; }

    public decimal? SharpeRatio { get; set; }

    public decimal? SortinoRatio { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    /// <summary>"Running", "Completed", "Failed".</summary>
    [StringLength(20)]
    public string Status { get; set; } = "Running";

    [StringLength(500)]
    public string? Notes { get; set; }

    // Navigation
    public StrategyVersionEntity? StrategyVersion { get; set; }
    public ICollection<BacktestTradeEntity> Trades { get; set; } = new List<BacktestTradeEntity>();
}
