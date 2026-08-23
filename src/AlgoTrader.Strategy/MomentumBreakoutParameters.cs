namespace AlgoTrader.Strategy;

using AlgoTrader.Application.Configuration;

/// <summary>
/// Immutable, strongly typed research parameters for <see cref="MomentumBreakoutV1"/> (§11, §12).
/// The strategy depends on this typed record rather than on <see cref="StrategySettings"/> directly,
/// so its decision logic is independent of the configuration/binding machinery and is trivially
/// testable. Percentages are expressed as whole-number percents (0.50 == 0.50%), matching
/// <see cref="StrategySettings"/>; they are converted to fractions where prices are computed.
/// These are hypothesis values — not optimized constants — and nothing is hardcoded in code (§30).
/// </summary>
public sealed record MomentumBreakoutParameters
{
    /// <summary>Strategy version recorded with every backtest run (§21).</summary>
    public string Version { get; init; } = "1.0.0";

    /// <summary>Number of prior bars whose highest high defines the breakout level.</summary>
    public required int LookbackBars { get; init; }

    /// <summary>Volume expansion factor versus the recent average volume.</summary>
    public required decimal VolumeMultiplier { get; init; }

    /// <summary>Trend-filter EMA period (applied only when <see cref="UseTrendFilter"/> is true).</summary>
    public required int EmaPeriod { get; init; }

    /// <summary>Whether the price-above-EMA trend filter gates entries.</summary>
    public required bool UseTrendFilter { get; init; }

    /// <summary>Initial stop-loss distance as a percent of entry price (0.50 == 0.50%).</summary>
    public required decimal StopLossPercent { get; init; }

    /// <summary>Fixed target distance as a percent of entry price (ignored when trailing).</summary>
    public required decimal TargetPercent { get; init; }

    /// <summary>When true, trail the stop below the running peak instead of using a fixed target.</summary>
    public required bool UseTrailingStop { get; init; }

    /// <summary>Time-based exit: close the position after this many minutes regardless of P&amp;L.</summary>
    public required int MaximumHoldingMinutes { get; init; }

    /// <summary>Strategy-level cap on new entries per instrument per trading day.</summary>
    public required int MaxTradesPerDay { get; init; }

    /// <summary>First IST time entries are allowed.</summary>
    public required TimeOnly EntryStartTime { get; init; }

    /// <summary>No new entries at or after this IST time.</summary>
    public required TimeOnly EntryCutoffTime { get; init; }

    /// <summary>All positions are exited at or after this IST time (forced end-of-day flat).</summary>
    public required TimeOnly ExitTime { get; init; }

    /// <summary>Throws when any parameter is outside its valid research range or internally inconsistent.</summary>
    public void Validate()
    {
        if (LookbackBars < 2)
            throw new ArgumentException("LookbackBars must be at least 2.", nameof(LookbackBars));
        if (EmaPeriod < 2)
            throw new ArgumentException("EmaPeriod must be at least 2.", nameof(EmaPeriod));
        if (VolumeMultiplier < 1m)
            throw new ArgumentException("VolumeMultiplier must be at least 1.0.", nameof(VolumeMultiplier));
        if (StopLossPercent <= 0m)
            throw new ArgumentException("StopLossPercent must be positive.", nameof(StopLossPercent));
        if (!UseTrailingStop && TargetPercent <= 0m)
            throw new ArgumentException("TargetPercent must be positive when trailing stop is disabled.", nameof(TargetPercent));
        if (MaximumHoldingMinutes < 1)
            throw new ArgumentException("MaximumHoldingMinutes must be at least 1.", nameof(MaximumHoldingMinutes));
        if (MaxTradesPerDay < 0)
            throw new ArgumentException("MaxTradesPerDay cannot be negative.", nameof(MaxTradesPerDay));
        if (EntryStartTime >= EntryCutoffTime)
            throw new ArgumentException("EntryStartTime must be strictly before EntryCutoffTime.", nameof(EntryStartTime));
        if (EntryCutoffTime > ExitTime)
            throw new ArgumentException("EntryCutoffTime must not be after ExitTime.", nameof(EntryCutoffTime));
    }

    /// <summary>Projects validated <see cref="StrategySettings"/> onto typed strategy parameters.</summary>
    public static MomentumBreakoutParameters FromSettings(StrategySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var parameters = new MomentumBreakoutParameters
        {
            Version = settings.Version,
            LookbackBars = settings.LookbackBars,
            VolumeMultiplier = settings.VolumeMultiplier,
            EmaPeriod = settings.EmaPeriod,
            UseTrendFilter = settings.UseTrendFilter,
            StopLossPercent = settings.StopLossPercent,
            TargetPercent = settings.TargetPercent,
            UseTrailingStop = settings.UseTrailingStop,
            MaximumHoldingMinutes = settings.MaximumHoldingMinutes,
            MaxTradesPerDay = settings.MaxTradesPerDay,
            EntryStartTime = settings.GetEntryStartTime(),
            EntryCutoffTime = settings.GetEntryCutoffTime(),
            ExitTime = settings.GetExitTime()
        };
        parameters.Validate();
        return parameters;
    }
}
