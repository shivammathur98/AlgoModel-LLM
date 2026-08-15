namespace AlgoTrader.Application.Configuration;

using System.ComponentModel.DataAnnotations;
using System.Globalization;
using AlgoTrader.Domain.Enums;

/// <summary>
/// MomentumBreakoutV1 research parameters (§11, §12). These are hypothesis values,
/// not optimized constants. Nothing strategy-specific is hardcoded in code (§30).
/// </summary>
public sealed class StrategySettings
{
    public const string SectionName = "Strategy";

    private const string TimePattern = "^([01][0-9]|2[0-3]):[0-5][0-9]$";

    public string Name { get; set; } = "MomentumBreakoutV1";

    public string Version { get; set; } = "1.0.0";

    /// <summary>Decision bar size. Initial research uses 5-minute candles.</summary>
    public Timeframe Timeframe { get; set; } = Timeframe.Minute5;

    /// <summary>Number of prior bars whose high defines the breakout level.</summary>
    [Range(2, 500)]
    public int LookbackBars { get; set; } = 10;

    /// <summary>Volume expansion factor versus the recent average volume.</summary>
    [Range(1.0, 10.0)]
    public decimal VolumeMultiplier { get; set; } = 1.5m;

    /// <summary>Optional trend-filter EMA period.</summary>
    [Range(2, 500)]
    public int EmaPeriod { get; set; } = 20;

    /// <summary>Whether the price-above-EMA trend filter is applied.</summary>
    public bool UseTrendFilter { get; set; } = true;

    /// <summary>Initial stop-loss scenarios: 0.25 / 0.50 / 0.75 / 1.00 (%).</summary>
    [Range(0.05, 10.0)]
    public decimal StopLossPercent { get; set; } = 0.50m;

    /// <summary>Initial target scenarios: 1.0 / 1.25 / 1.5 (%).</summary>
    [Range(0.05, 10.0)]
    public decimal TargetPercent { get; set; } = 1.00m;

    /// <summary>Enable trailing stop instead of fixed target.</summary>
    public bool UseTrailingStop { get; set; }

    /// <summary>Time-based exit: close after this many minutes regardless of P&amp;L.</summary>
    [Range(1, 390)]
    public int MaximumHoldingMinutes { get; set; } = 120;

    /// <summary>Strategy-level cap on new entries per day.</summary>
    [Range(0, 100)]
    public int MaxTradesPerDay { get; set; } = 3;

    /// <summary>First time entries are allowed (IST, "HH:mm").</summary>
    [RegularExpression(TimePattern)]
    public string EntryStartTime { get; set; } = "09:20";

    /// <summary>No new entries after this time (IST, "HH:mm").</summary>
    [RegularExpression(TimePattern)]
    public string EntryCutoffTime { get; set; } = "14:30";

    /// <summary>All positions must be closed before this time (IST, "HH:mm").</summary>
    [RegularExpression(TimePattern)]
    public string ExitTime { get; set; } = "15:15";

    public TimeOnly GetEntryStartTime() => TimeOnly.Parse(EntryStartTime, CultureInfo.InvariantCulture);

    public TimeOnly GetEntryCutoffTime() => TimeOnly.Parse(EntryCutoffTime, CultureInfo.InvariantCulture);

    public TimeOnly GetExitTime() => TimeOnly.Parse(ExitTime, CultureInfo.InvariantCulture);
}
