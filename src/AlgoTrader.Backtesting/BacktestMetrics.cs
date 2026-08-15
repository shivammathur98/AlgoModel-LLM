namespace AlgoTrader.Backtesting;

/// <summary>One point in a closed-trade capital curve.</summary>
public sealed record CapitalCurvePoint(DateTimeOffset TimestampUtc, decimal Equity);

/// <summary>
/// Performance measures calculated exclusively from completed trades. Financial values are decimals;
/// return ratios are doubles because they use square roots and standard deviation.
/// </summary>
public sealed record BacktestMetrics(
    int TotalTrades,
    int WinningTrades,
    int LosingTrades,
    decimal WinRatePercent,
    decimal AverageWin,
    decimal AverageLoss,
    decimal GrossPnl,
    decimal TotalCharges,
    decimal TotalSlippage,
    decimal NetPnl,
    decimal MaximumDrawdown,
    decimal AverageDrawdown,
    decimal? ProfitFactor,
    decimal Expectancy,
    double? SharpeRatio,
    double? SortinoRatio,
    decimal LargestWin,
    decimal LargestLoss,
    TimeSpan AverageHoldingTime,
    int MaximumConsecutiveWins,
    int MaximumConsecutiveLosses,
    decimal ReturnPercent,
    IReadOnlyDictionary<DateOnly, decimal> DailyPnl,
    IReadOnlyDictionary<string, decimal> MonthlyPnl,
    IReadOnlyList<CapitalCurvePoint> CapitalCurve);
