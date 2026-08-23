namespace AlgoTrader.Application.Configuration;

using System.ComponentModel.DataAnnotations;
using System.Globalization;

/// <summary>
/// Research parameters for the multi-day swing hypothesis TrendAlignedPullbackV1 (§11, §12).
/// The strategy trades in the direction of the *current* trend only, holds 1–2 sessions, and takes a
/// small fixed target — so charge drag (delivery product, no per-day churn) stays low. These are
/// hypothesis values, not optimized constants; nothing strategy-specific is hardcoded in code (§30).
/// </summary>
public sealed class SwingStrategySettings
{
    public const string SectionName = "SwingStrategy";

    private const string TimePattern = "^([01][0-9]|2[0-3]):[0-5][0-9]$";

    public string Version { get; set; } = "1.0.0";

    /// <summary>Longer EMA whose rising slope defines an up-trend regime.</summary>
    [Range(2, 500)]
    public int TrendEmaPeriod { get; set; } = 50;

    /// <summary>Shorter EMA used as the pullback / re-entry reference.</summary>
    [Range(2, 500)]
    public int PullbackEmaPeriod { get; set; } = 20;

    /// <summary>Number of bars over which the trend EMA slope is measured (must be currently rising).</summary>
    [Range(1, 500)]
    public int TrendSlopeLookback { get; set; } = 10;

    /// <summary>ATR period used to size the volatility-adaptive stop.</summary>
    [Range(2, 500)]
    public int AtrPeriod { get; set; } = 14;

    /// <summary>Stop distance in ATR multiples (capped by <see cref="MaxStopLossPercent"/>).</summary>
    [Range(0.1, 20.0)]
    public decimal AtrStopMultiplier { get; set; } = 1.5m;

    /// <summary>Hard cap on stop distance as a percent of entry price, bounding worst-case risk per trade.</summary>
    [Range(0.1, 10.0)]
    public decimal MaxStopLossPercent { get; set; } = 1.0m;

    /// <summary>Fixed profit target as a percent of entry price (kept small: 0.5–1.0%).</summary>
    [Range(0.1, 5.0)]
    public decimal TargetPercent { get; set; } = 0.75m;

    /// <summary>Maximum number of trading sessions a position may be held before a forced time exit.</summary>
    [Range(1, 20)]
    public int MaxHoldingDays { get; set; } = 2;

    /// <summary>Cap on new entries per instrument per day (kept at 1 for low churn).</summary>
    [Range(0, 20)]
    public int MaxTradesPerDay { get; set; } = 1;

    /// <summary>First IST time entries are allowed (avoids the opening auction).</summary>
    [RegularExpression(TimePattern)]
    public string EntryStartTime { get; set; } = "09:30";

    /// <summary>No new entries at or after this IST time.</summary>
    [RegularExpression(TimePattern)]
    public string EntryCutoffTime { get; set; } = "14:45";

    public TimeOnly GetEntryStartTime() => TimeOnly.Parse(EntryStartTime, CultureInfo.InvariantCulture);

    public TimeOnly GetEntryCutoffTime() => TimeOnly.Parse(EntryCutoffTime, CultureInfo.InvariantCulture);
}
