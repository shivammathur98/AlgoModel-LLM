namespace AlgoTrader.Strategy;

using AlgoTrader.Application.Configuration;

/// <summary>
/// Immutable, strongly typed research parameters for <see cref="TrendAlignedPullbackV1"/> (§11, §12).
/// Percentages are whole-number percents (0.75 == 0.75%). These are hypothesis values, not optimized
/// constants, and nothing is hardcoded in code (§30).
/// </summary>
public sealed record TrendAlignedPullbackParameters
{
    public string Version { get; init; } = "1.0.0";

    /// <summary>Longer EMA whose rising slope defines the up-trend regime.</summary>
    public required int TrendEmaPeriod { get; init; }

    /// <summary>Shorter EMA used as the pullback / re-entry reference.</summary>
    public required int PullbackEmaPeriod { get; init; }

    /// <summary>Bars over which the trend EMA slope must be positive (currently rising).</summary>
    public required int TrendSlopeLookback { get; init; }

    /// <summary>ATR period for the volatility-adaptive stop.</summary>
    public required int AtrPeriod { get; init; }

    /// <summary>Stop distance in ATR multiples, capped by <see cref="MaxStopLossPercent"/>.</summary>
    public required decimal AtrStopMultiplier { get; init; }

    /// <summary>Hard cap on stop distance as a percent of entry price.</summary>
    public required decimal MaxStopLossPercent { get; init; }

    /// <summary>Fixed profit target as a percent of entry price (small: 0.5–1.0%).</summary>
    public required decimal TargetPercent { get; init; }

    /// <summary>Maximum trading sessions a position may be held before a forced time exit.</summary>
    public required int MaxHoldingDays { get; init; }

    /// <summary>Cap on new entries per instrument per day.</summary>
    public required int MaxTradesPerDay { get; init; }

    /// <summary>First IST time entries are allowed.</summary>
    public required TimeOnly EntryStartTime { get; init; }

    /// <summary>No new entries at or after this IST time.</summary>
    public required TimeOnly EntryCutoffTime { get; init; }

    /// <summary>Throws when any parameter is outside its valid research range or internally inconsistent.</summary>
    public void Validate()
    {
        if (TrendEmaPeriod < 2)
            throw new ArgumentException("TrendEmaPeriod must be at least 2.", nameof(TrendEmaPeriod));
        if (PullbackEmaPeriod < 2)
            throw new ArgumentException("PullbackEmaPeriod must be at least 2.", nameof(PullbackEmaPeriod));
        if (TrendSlopeLookback < 1)
            throw new ArgumentException("TrendSlopeLookback must be at least 1.", nameof(TrendSlopeLookback));
        if (AtrPeriod < 1)
            throw new ArgumentException("AtrPeriod must be at least 1.", nameof(AtrPeriod));
        if (AtrStopMultiplier <= 0m)
            throw new ArgumentException("AtrStopMultiplier must be positive.", nameof(AtrStopMultiplier));
        if (MaxStopLossPercent <= 0m)
            throw new ArgumentException("MaxStopLossPercent must be positive.", nameof(MaxStopLossPercent));
        if (TargetPercent <= 0m)
            throw new ArgumentException("TargetPercent must be positive.", nameof(TargetPercent));
        if (MaxHoldingDays < 1)
            throw new ArgumentException("MaxHoldingDays must be at least 1.", nameof(MaxHoldingDays));
        if (MaxTradesPerDay < 0)
            throw new ArgumentException("MaxTradesPerDay cannot be negative.", nameof(MaxTradesPerDay));
        if (EntryStartTime >= EntryCutoffTime)
            throw new ArgumentException("EntryStartTime must be strictly before EntryCutoffTime.", nameof(EntryStartTime));
    }

    /// <summary>Projects validated <see cref="SwingStrategySettings"/> onto typed strategy parameters.</summary>
    public static TrendAlignedPullbackParameters FromSettings(SwingStrategySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var parameters = new TrendAlignedPullbackParameters
        {
            Version = settings.Version,
            TrendEmaPeriod = settings.TrendEmaPeriod,
            PullbackEmaPeriod = settings.PullbackEmaPeriod,
            TrendSlopeLookback = settings.TrendSlopeLookback,
            AtrPeriod = settings.AtrPeriod,
            AtrStopMultiplier = settings.AtrStopMultiplier,
            MaxStopLossPercent = settings.MaxStopLossPercent,
            TargetPercent = settings.TargetPercent,
            MaxHoldingDays = settings.MaxHoldingDays,
            MaxTradesPerDay = settings.MaxTradesPerDay,
            EntryStartTime = settings.GetEntryStartTime(),
            EntryCutoffTime = settings.GetEntryCutoffTime()
        };
        parameters.Validate();
        return parameters;
    }
}
