namespace AlgoTrader.Strategy;

using AlgoTrader.Domain.MarketData;

/// <summary>
/// Deterministic, allocation-light technical indicators used by strategies. Every method operates
/// only on the candles supplied (which the engine guarantees are closed bars ordered oldest → newest),
/// so results depend solely on information available at decision time — no look-ahead (§16).
/// All computation is in <see cref="decimal"/>; there is no floating-point arithmetic on prices (§37).
/// </summary>
public static class Indicators
{
    /// <summary>
    /// Highest high over the <paramref name="lookback"/> bars immediately BEFORE the last (decision)
    /// bar in <paramref name="candles"/>. Returns null when there are not enough prior bars.
    /// The decision bar itself is deliberately excluded so a breakout is measured against history only.
    /// </summary>
    public static decimal? PriorHighestHigh(IReadOnlyList<Candle> candles, int lookback)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (lookback < 1) throw new ArgumentOutOfRangeException(nameof(lookback), "Lookback must be positive.");

        var decisionIndex = candles.Count - 1;
        var start = decisionIndex - lookback;
        if (start < 0) return null;

        var highest = decimal.MinValue;
        for (var i = start; i < decisionIndex; i++)
        {
            if (candles[i].High > highest) highest = candles[i].High;
        }

        return highest;
    }

    /// <summary>
    /// Arithmetic mean volume over the <paramref name="lookback"/> bars immediately BEFORE the last
    /// (decision) bar. Returns null when there are not enough prior bars. Excludes the decision bar so
    /// the current bar's own volume can be compared against a purely historical baseline.
    /// </summary>
    public static decimal? PriorAverageVolume(IReadOnlyList<Candle> candles, int lookback)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (lookback < 1) throw new ArgumentOutOfRangeException(nameof(lookback), "Lookback must be positive.");

        var decisionIndex = candles.Count - 1;
        var start = decisionIndex - lookback;
        if (start < 0) return null;

        long sum = 0;
        for (var i = start; i < decisionIndex; i++)
        {
            sum += candles[i].Volume;
        }

        return (decimal)sum / lookback;
    }

    /// <summary>
    /// Exponential moving average of candle closes up to and including the last bar. Seeded with the
    /// simple moving average of the first <paramref name="period"/> closes, then smoothed with
    /// k = 2 / (period + 1). Returns null when fewer than <paramref name="period"/> bars are supplied.
    /// </summary>
    public static decimal? Ema(IReadOnlyList<Candle> candles, int period)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (candles.Count < period) return null;

        var closes = new decimal[candles.Count];
        for (var i = 0; i < candles.Count; i++)
        {
            closes[i] = candles[i].Close;
        }

        return Ema(closes, period);
    }

    /// <summary>
    /// Exponential moving average over a raw value series. Seeded with the SMA of the first
    /// <paramref name="period"/> values. Returns null when fewer than <paramref name="period"/> values exist.
    /// </summary>
    public static decimal? Ema(IReadOnlyList<decimal> values, int period)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period), "Period must be positive.");
        if (values.Count < period) return null;

        decimal seed = 0m;
        for (var i = 0; i < period; i++)
        {
            seed += values[i];
        }

        var ema = seed / period;
        var multiplier = 2m / (period + 1);
        for (var i = period; i < values.Count; i++)
        {
            ema = values[i] * multiplier + ema * (1m - multiplier);
        }

        return ema;
    }

    /// <summary>
    /// EMA value at every index of <paramref name="values"/>. Entries before the seed is available
    /// (index &lt; period − 1) are null. Used to measure how the average is moving *right now* (its slope),
    /// so the strategy reads the current trend rather than a single stale reading.
    /// </summary>
    public static decimal?[] EmaSeries(IReadOnlyList<decimal> values, int period)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period), "Period must be positive.");

        var series = new decimal?[values.Count];
        if (values.Count < period) return series;

        decimal seed = 0m;
        for (var i = 0; i < period; i++)
        {
            seed += values[i];
        }

        var ema = seed / period;
        series[period - 1] = ema;
        var multiplier = 2m / (period + 1);
        for (var i = period; i < values.Count; i++)
        {
            ema = values[i] * multiplier + ema * (1m - multiplier);
            series[i] = ema;
        }

        return series;
    }

    /// <summary>EMA value at every index computed over candle closes (see the value-series overload).</summary>
    public static decimal?[] EmaSeries(IReadOnlyList<Candle> candles, int period)
    {
        ArgumentNullException.ThrowIfNull(candles);
        var closes = new decimal[candles.Count];
        for (var i = 0; i < candles.Count; i++)
        {
            closes[i] = candles[i].Close;
        }

        return EmaSeries(closes, period);
    }

    /// <summary>
    /// Average True Range using Wilder's smoothing (§indicators). ATR measures *current* volatility, so a
    /// stop placed at a multiple of ATR widens in fast markets and tightens in calm ones instead of using a
    /// single fixed distance that is wrong whenever conditions change. Returns null when there are fewer than
    /// <paramref name="period"/> + 1 candles (each true range needs the prior close).
    /// </summary>
    public static decimal? Atr(IReadOnlyList<Candle> candles, int period)
    {
        ArgumentNullException.ThrowIfNull(candles);
        if (period < 1) throw new ArgumentOutOfRangeException(nameof(period), "Period must be positive.");
        if (candles.Count < period + 1) return null;

        decimal seed = 0m;
        for (var i = 1; i <= period; i++)
        {
            seed += TrueRange(candles[i], candles[i - 1]);
        }

        var atr = seed / period;
        for (var i = period + 1; i < candles.Count; i++)
        {
            atr = (atr * (period - 1) + TrueRange(candles[i], candles[i - 1])) / period;
        }

        return atr;
    }

    private static decimal TrueRange(Candle current, Candle previous)
    {
        var highLow = current.High - current.Low;
        var highClose = Math.Abs(current.High - previous.Close);
        var lowClose = Math.Abs(current.Low - previous.Close);
        return Math.Max(highLow, Math.Max(highClose, lowClose));
    }
}
