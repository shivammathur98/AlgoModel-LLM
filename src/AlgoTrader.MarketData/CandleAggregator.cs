namespace AlgoTrader.MarketData;

using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.MarketData;
using Microsoft.Extensions.Logging;

/// <summary>
/// Aggregates incoming ticks into OHLCV candles (§7).
/// Maintains one in-progress candle per (InstrumentToken, Timeframe) pair.
/// All candle timestamps are UTC-aligned to IST market boundaries.
/// </summary>
/// <remarks>
/// The <see cref="Tick"/> domain record carries only the token and prices; symbol and exchange
/// metadata is resolved via the injected <paramref name="symbolResolver"/> so the aggregator
/// stays decoupled from the instrument repository.
/// </remarks>
public sealed class CandleAggregator : ICandleAggregator
{
    private readonly ILogger<CandleAggregator> _logger;
    private readonly Func<int, (string Symbol, string Exchange)> _symbolResolver;
    private readonly Dictionary<(int Token, Timeframe Timeframe), (Candle Candle, DateTimeOffset NextStartUtc)> _inProgress = new();
    private readonly object _lock = new();

    public CandleAggregator(
        ILogger<CandleAggregator> logger,
        Func<int, (string Symbol, string Exchange)> symbolResolver)
    {
        _logger = logger;
        _symbolResolver = symbolResolver ?? throw new ArgumentNullException(nameof(symbolResolver));
    }

    /// <inheritdoc/>
    public Candle? OnTick(Tick tick, Timeframe timeframe)
    {
        var candleStartUtc = AlignToInterval(tick.TimestampUtc, timeframe);
        var nextCandleStartUtc = candleStartUtc.AddMinutes(GetIntervalMinutes(timeframe));

        lock (_lock)
        {
            var key = (tick.InstrumentToken, timeframe);

            if (_inProgress.TryGetValue(key, out var current))
            {
                // If the tick falls within the current candle, update it.
                if (tick.TimestampUtc >= current.Candle.TimestampUtc && tick.TimestampUtc < current.NextStartUtc)
                {
                    var updated = current.Candle with
                    {
                        High = Math.Max(current.Candle.High, tick.LastPrice),
                        Low = Math.Min(current.Candle.Low, tick.LastPrice),
                        Close = tick.LastPrice,
                        Volume = current.Candle.Volume + tick.Volume
                    };
                    _inProgress[key] = (updated, current.NextStartUtc);
                    return null;
                }

                // If the tick is later, close the current candle and start a new one.
                if (tick.TimestampUtc >= current.NextStartUtc)
                {
                    var closedCandle = current.Candle;
                    var (symbol, exchange) = _symbolResolver(tick.InstrumentToken);
                    var newCandle = new Candle(
                        InstrumentToken: tick.InstrumentToken,
                        Symbol: symbol,
                        Exchange: exchange,
                        Timeframe: timeframe,
                        TimestampUtc: candleStartUtc,
                        Open: tick.LastPrice,
                        High: tick.LastPrice,
                        Low: tick.LastPrice,
                        Close: tick.LastPrice,
                        Volume: tick.Volume);

                    _inProgress[key] = (newCandle, nextCandleStartUtc);
                    _logger.LogDebug("Closed candle {Key} at {Time} (O={O} H={H} L={L} C={C})",
                        key, closedCandle.TimestampUtc, closedCandle.Open, closedCandle.High, closedCandle.Low, closedCandle.Close);
                    return closedCandle;
                }

                // Tick is earlier than the current candle (late tick) — ignore.
                _logger.LogDebug("Ignoring late tick at {Time} for candle starting at {CandleStart}",
                    tick.TimestampUtc, current.Candle.TimestampUtc);
                return null;
            }

            // No current candle — start a new one.
            var (initialSymbol, initialExchange) = _symbolResolver(tick.InstrumentToken);
            var initialCandle = new Candle(
                InstrumentToken: tick.InstrumentToken,
                Symbol: initialSymbol,
                Exchange: initialExchange,
                Timeframe: timeframe,
                TimestampUtc: candleStartUtc,
                Open: tick.LastPrice,
                High: tick.LastPrice,
                Low: tick.LastPrice,
                Close: tick.LastPrice,
                Volume: tick.Volume);

            _inProgress[key] = (initialCandle, nextCandleStartUtc);
            return null;
        }
    }

    /// <inheritdoc/>
    public void Reset(int instrumentToken)
    {
        lock (_lock)
        {
            var keysToRemove = _inProgress.Keys.Where(k => k.Token == instrumentToken).ToList();
            foreach (var key in keysToRemove)
            {
                _inProgress.Remove(key);
            }
            _logger.LogDebug("Reset aggregator for instrument {Token}", instrumentToken);
        }
    }

    /// <summary>
    /// Returns the current in-progress candle for a given instrument and timeframe, or null if none exists.
    /// Useful for testing and monitoring.
    /// </summary>
    public Candle? GetCurrentCandle(int instrumentToken, Timeframe timeframe)
    {
        lock (_lock)
        {
            return _inProgress.TryGetValue((instrumentToken, timeframe), out var current) ? current.Candle : null;
        }
    }

    internal static int GetIntervalMinutes(Timeframe timeframe) => timeframe switch
    {
        Timeframe.Minute1 => 1,
        Timeframe.Minute5 => 5,
        Timeframe.Minute15 => 15,
        Timeframe.Minute30 => 30,
        Timeframe.Minute60 => 60,
        Timeframe.Daily => 1440,
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe))
    };

    internal static DateTimeOffset AlignToInterval(DateTimeOffset utcTimestamp, Timeframe timeframe)
    {
        // Convert to IST for alignment (IST = UTC + 5:30)
        var istOffset = TimeSpan.FromHours(5.5);
        var ist = utcTimestamp.ToOffset(istOffset);

        // Calculate the start of the day in IST
        var dayStart = new DateTimeOffset(ist.Year, ist.Month, ist.Day, 0, 0, 0, istOffset);
        var minutesSinceDayStart = (long)(ist - dayStart).TotalMinutes;

        // Align to the timeframe interval
        var intervalMinutes = GetIntervalMinutes(timeframe);
        var alignedMinutes = (minutesSinceDayStart / intervalMinutes) * intervalMinutes;

        // Construct the aligned timestamp in IST, then convert back to UTC
        var alignedIst = dayStart.AddMinutes(alignedMinutes);
        return alignedIst.ToUniversalTime();
    }
}
