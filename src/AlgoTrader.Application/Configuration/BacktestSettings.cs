namespace AlgoTrader.Application.Configuration;

using System.ComponentModel.DataAnnotations;
using AlgoTrader.Domain.Enums;

/// <summary>
/// Backtesting defaults (§16, §21). Data-split boundaries define training, validation and
/// out-of-sample windows; the out-of-sample window must never be used for optimization (§21).
/// </summary>
public sealed class BacktestSettings
{
    public const string SectionName = "Backtest";

    [Range(1_000.0, 1_000_000_000.0)]
    public decimal InitialCapital { get; set; } = 525_000m;

    /// <summary>Start of the data range to load.</summary>
    public DateTimeOffset? DataStartUtc { get; set; }

    /// <summary>End of the data range to load.</summary>
    public DateTimeOffset? DataEndUtc { get; set; }

    /// <summary>End of the training (development) window.</summary>
    public DateTimeOffset? TrainEndUtc { get; set; }

    /// <summary>End of the validation window. Later data is out-of-sample.</summary>
    public DateTimeOffset? ValidationEndUtc { get; set; }

    /// <summary>Fill simulation fidelity. Defaults to Realistic (§17).</summary>
    public ExecutionModel ExecutionModel { get; set; } = ExecutionModel.Realistic;
}
