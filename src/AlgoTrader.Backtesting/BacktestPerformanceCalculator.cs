namespace AlgoTrader.Backtesting;

/// <summary>
/// Produces deterministic closed-trade analytics. It deliberately has no persistence, strategy,
/// broker, or wall-clock dependency so an identical trade ledger always yields identical results.
/// </summary>
public sealed class BacktestPerformanceCalculator
{
    private const int TradingDaysPerYear = 252;

    /// <summary>Calculates all performance metrics from a closed-trade ledger.</summary>
    public BacktestMetrics Calculate(decimal initialCapital, IReadOnlyList<BacktestTrade> trades)
    {
        if (initialCapital <= 0m)
            throw new ArgumentOutOfRangeException(nameof(initialCapital), "Initial capital must be positive.");

        var orderedTrades = trades.OrderBy(trade => trade.ExitTimestampUtc).ThenBy(trade => trade.TradeId, StringComparer.Ordinal).ToList();
        var winningTrades = orderedTrades.Where(trade => trade.NetPnl > 0m).ToList();
        var losingTrades = orderedTrades.Where(trade => trade.NetPnl < 0m).ToList();
        var grossPnl = orderedTrades.Sum(trade => trade.GrossPnl);
        var totalCharges = orderedTrades.Sum(trade => trade.TotalCharges);
        var totalSlippage = orderedTrades.Sum(trade => trade.TotalSlippage);
        var netPnl = orderedTrades.Sum(trade => trade.NetPnl);
        var dailyPnl = orderedTrades
            .GroupBy(trade => DateOnly.FromDateTime(trade.ExitTimestampUtc.UtcDateTime))
            .ToDictionary(group => group.Key, group => group.Sum(trade => trade.NetPnl));
        var monthlyPnl = orderedTrades
            .GroupBy(trade => trade.ExitTimestampUtc.UtcDateTime.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture))
            .ToDictionary(group => group.Key, group => group.Sum(trade => trade.NetPnl));
        var curveAndDrawdowns = BuildCapitalCurve(initialCapital, orderedTrades);
        var returns = BuildDailyReturns(initialCapital, dailyPnl);

        return new BacktestMetrics(
            TotalTrades: orderedTrades.Count,
            WinningTrades: winningTrades.Count,
            LosingTrades: losingTrades.Count,
            WinRatePercent: Percentage(winningTrades.Count, orderedTrades.Count),
            AverageWin: Average(winningTrades.Select(trade => trade.NetPnl)),
            AverageLoss: Average(losingTrades.Select(trade => trade.NetPnl)),
            GrossPnl: grossPnl,
            TotalCharges: totalCharges,
            TotalSlippage: totalSlippage,
            NetPnl: netPnl,
            MaximumDrawdown: curveAndDrawdowns.Drawdowns.DefaultIfEmpty(0m).Max(),
            AverageDrawdown: Average(curveAndDrawdowns.Drawdowns.Where(drawdown => drawdown > 0m)),
            ProfitFactor: ProfitFactor(winningTrades, losingTrades),
            Expectancy: Average(orderedTrades.Select(trade => trade.NetPnl)),
            SharpeRatio: CalculateSharpe(returns),
            SortinoRatio: CalculateSortino(returns),
            LargestWin: winningTrades.Select(trade => trade.NetPnl).DefaultIfEmpty(0m).Max(),
            LargestLoss: losingTrades.Select(trade => trade.NetPnl).DefaultIfEmpty(0m).Min(),
            AverageHoldingTime: AverageHoldingTime(orderedTrades),
            MaximumConsecutiveWins: MaximumStreak(orderedTrades, trade => trade.NetPnl > 0m),
            MaximumConsecutiveLosses: MaximumStreak(orderedTrades, trade => trade.NetPnl < 0m),
            ReturnPercent: ((initialCapital + netPnl - initialCapital) / initialCapital) * 100m,
            DailyPnl: dailyPnl,
            MonthlyPnl: monthlyPnl,
            CapitalCurve: curveAndDrawdowns.Curve);
    }

    private static (IReadOnlyList<CapitalCurvePoint> Curve, IReadOnlyList<decimal> Drawdowns) BuildCapitalCurve(
        decimal initialCapital,
        IReadOnlyList<BacktestTrade> trades)
    {
        var equity = initialCapital;
        var highWaterMark = initialCapital;
        var points = new List<CapitalCurvePoint> { new(DateTimeOffset.MinValue, initialCapital) };
        var drawdowns = new List<decimal>();

        foreach (var trade in trades)
        {
            equity += trade.NetPnl;
            highWaterMark = Math.Max(highWaterMark, equity);
            points.Add(new CapitalCurvePoint(trade.ExitTimestampUtc, equity));
            drawdowns.Add(highWaterMark - equity);
        }

        return (points, drawdowns);
    }

    private static IReadOnlyList<double> BuildDailyReturns(decimal initialCapital, IReadOnlyDictionary<DateOnly, decimal> dailyPnl)
    {
        var equity = initialCapital;
        var returns = new List<double>(dailyPnl.Count);
        foreach (var pnl in dailyPnl.OrderBy(entry => entry.Key).Select(entry => entry.Value))
        {
            returns.Add((double)(pnl / equity));
            equity += pnl;
        }

        return returns;
    }

    private static decimal? ProfitFactor(IReadOnlyList<BacktestTrade> winners, IReadOnlyList<BacktestTrade> losers)
    {
        var grossLoss = losers.Sum(trade => -trade.NetPnl);
        return grossLoss == 0m ? null : winners.Sum(trade => trade.NetPnl) / grossLoss;
    }

    private static double? CalculateSharpe(IReadOnlyList<double> dailyReturns)
    {
        if (dailyReturns.Count < 2) return null;
        var standardDeviation = SampleStandardDeviation(dailyReturns);
        return standardDeviation == 0d ? null : dailyReturns.Average() / standardDeviation * Math.Sqrt(TradingDaysPerYear);
    }

    private static double? CalculateSortino(IReadOnlyList<double> dailyReturns)
    {
        if (dailyReturns.Count < 2) return null;
        var downsideReturns = dailyReturns.Where(value => value < 0d).ToList();
        if (downsideReturns.Count == 0) return null;
        var downsideDeviation = Math.Sqrt(downsideReturns.Average(value => value * value));
        return downsideDeviation == 0d ? null : dailyReturns.Average() / downsideDeviation * Math.Sqrt(TradingDaysPerYear);
    }

    private static double SampleStandardDeviation(IReadOnlyList<double> values)
    {
        var mean = values.Average();
        return Math.Sqrt(values.Sum(value => Math.Pow(value - mean, 2d)) / (values.Count - 1));
    }

    private static decimal Percentage(int numerator, int denominator) => denominator == 0 ? 0m : (decimal)numerator / denominator * 100m;

    private static decimal Average(IEnumerable<decimal> values)
    {
        var materialized = values.ToList();
        return materialized.Count == 0 ? 0m : materialized.Average();
    }

    private static TimeSpan AverageHoldingTime(IReadOnlyList<BacktestTrade> trades) =>
        trades.Count == 0 ? TimeSpan.Zero : TimeSpan.FromTicks((long)trades.Average(trade => trade.HoldingTime.Ticks));

    private static int MaximumStreak(IReadOnlyList<BacktestTrade> trades, Func<BacktestTrade, bool> predicate)
    {
        var maximum = 0;
        var current = 0;
        foreach (var trade in trades)
        {
            current = predicate(trade) ? current + 1 : 0;
            maximum = Math.Max(maximum, current);
        }

        return maximum;
    }
}
