namespace AlgoTrader.Strategy;

using AlgoTrader.Domain.MarketData;
using AlgoTrader.Domain.Portfolio;
using AlgoTrader.Domain.Strategy;
using AlgoTrader.Domain.Trading;

/// <summary>
/// Long-only, trend-aligned pullback swing hypothesis (§11, §12). The design goal is charge-light,
/// regime-aware decision making rather than trade volume: it acts only in the direction of the trend the
/// market is in *right now*, takes at most one new position per instrument per day, holds across 1–2
/// sessions (delivery product, so no intraday churn/STT drag), and aims for a small fixed target.
/// <para>
/// On each closed decision candle it (a) confirms a current up-trend — the longer trend EMA is rising
/// over the recent slope window AND price is above it; (b) requires a *pullback that has resumed* — the
/// bar dipped to/through the shorter pullback EMA yet closed back above it as an up bar (buying a shallow
/// dip inside an uptrend, not chasing a breakout); (c) sizes a volatility-adaptive stop from ATR, hard
/// capped by a max-loss percent; and (d) attaches a small fixed target. Stop/target are enforced intrabar
/// by the execution/backtest layer. While holding, it emits an explicit exit when the trend invalidates
/// (close falls back below the trend EMA) or a maximum-holding-days time stop is reached.
/// </para>
/// <para>
/// Broker-agnostic: references only Domain market-data, portfolio and trading types; never broker or
/// execution classes (§16). Deterministic — its only state is a per-instrument, per-day entry counter
/// derived purely from the ordered candle stream — and it must be driven single-threaded by one
/// sequential candle feed. Use a fresh instance per independent backtest run.
/// </para>
/// <para>
/// IMPORTANT: this is an unvalidated research hypothesis. Nothing here asserts or implies profitability;
/// "current-trend" gating reduces, but does not remove, the risk that a regime flips against an open
/// position. Parameters are scenario values to be studied, not tuned claims (§12).
/// </para>
/// </summary>
public sealed class TrendAlignedPullbackV1 : IStrategy
{
    /// <summary>IST is UTC+05:30; all time-of-day and trading-session gating is evaluated in IST.</summary>
    private static readonly TimeSpan IndiaStandardTimeOffset = TimeSpan.FromHours(5.5);

    private readonly TrendAlignedPullbackParameters _parameters;

    /// <summary>Per-instrument entry tally for the current trading day; resets when the day rolls.</summary>
    private readonly Dictionary<int, DailyEntryTally> _entryTallies = [];

    public TrendAlignedPullbackV1(TrendAlignedPullbackParameters parameters)
    {
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        _parameters.Validate();
    }

    /// <inheritdoc />
    public string Name => "TrendAlignedPullbackV1";

    /// <inheritdoc />
    public string Version => _parameters.Version;

    /// <inheritdoc />
    public IReadOnlyList<Signal> OnCandleClosed(StrategyContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var candles = context.Candles;
        if (candles.Count == 0) return [];

        var decision = candles[^1];
        var ist = decision.TimestampUtc.ToOffset(IndiaStandardTimeOffset).DateTime;
        var istTime = TimeOnly.FromDateTime(ist);
        var istDate = DateOnly.FromDateTime(ist);

        // A single symbol never pyramids: when holding, evaluate exits only.
        if (context.OpenPosition is { } position)
        {
            return EvaluateExit(context, position, decision);
        }

        return EvaluateEntry(context, decision, istTime, istDate);
    }

    private IReadOnlyList<Signal> EvaluateEntry(StrategyContext context, Candle decision, TimeOnly istTime, DateOnly istDate)
    {
        // Entry window: no entries before the opening buffer or at/after the cutoff.
        if (istTime < _parameters.EntryStartTime || istTime >= _parameters.EntryCutoffTime) return [];

        // Per-instrument daily entry cap (kept at 1 for low churn).
        if (EntriesToday(context.InstrumentToken, istDate) >= _parameters.MaxTradesPerDay) return [];

        var trendSeries = Indicators.EmaSeries(context.Candles, _parameters.TrendEmaPeriod);
        var lastIndex = context.Candles.Count - 1;
        var slopeIndex = lastIndex - _parameters.TrendSlopeLookback;
        if (slopeIndex < 0) return []; // insufficient history for a slope reading

        // Current trend regime: the trend EMA must be defined now and one slope-window ago.
        if (trendSeries[lastIndex] is not { } trendNow || trendSeries[slopeIndex] is not { } trendThen) return [];

        var pullbackEma = Indicators.Ema(context.Candles, _parameters.PullbackEmaPeriod);
        var atr = Indicators.Atr(context.Candles, _parameters.AtrPeriod);
        if (pullbackEma is not { } fastEma || atr is not { } atrValue) return []; // insufficient history

        // (a) The trend is currently UP: rising trend EMA and price above it. This is the "decide on the
        //     current trend, not stale backdata" gate — if the regime is flat/down we simply stand aside.
        if (trendNow <= trendThen) return [];
        if (decision.Close <= trendNow) return [];

        // (b) Pullback that resumed: the bar dipped to/through the pullback EMA but closed back above it
        //     as an up bar. Buying a shallow dip within the uptrend rather than chasing an extended move.
        var pulledBack = decision.Low <= fastEma;
        var resumed = decision.Close > fastEma && decision.Close > decision.Open;
        if (!pulledBack || !resumed) return [];

        // (c) Volatility-adaptive stop, hard-capped by the max-loss percent so worst-case risk is bounded.
        var atrStopDistance = _parameters.AtrStopMultiplier * atrValue;
        var cappedDistance = _parameters.MaxStopLossPercent / 100m * decision.Close;
        var stopDistance = Math.Min(atrStopDistance, cappedDistance);
        if (stopDistance <= 0m) return []; // degenerate volatility/price guard

        var entryReference = decision.Close;
        var stopPrice = RoundToPaise(entryReference - stopDistance);
        var targetPrice = RoundToPaise(entryReference * (1m + _parameters.TargetPercent / 100m));

        RecordEntry(context.InstrumentToken, istDate);

        var notes = $"Trend up (EMA{_parameters.TrendEmaPeriod} rising {trendThen}->{trendNow}, close>{trendNow}); " +
                    $"pullback to EMA{_parameters.PullbackEmaPeriod} {fastEma} resumed; " +
                    $"stop {stopDistance} (ATR x{_parameters.AtrStopMultiplier}, cap {_parameters.MaxStopLossPercent}%)";
        return
        [
            new Signal(
                Name,
                Version,
                context.InstrumentToken,
                context.Symbol,
                SignalDirection.LongEntry,
                context.CurrentTimestampUtc,
                EntryPrice: entryReference,
                StopPrice: stopPrice,
                TargetPrice: targetPrice,
                Notes: notes)
        ];
    }

    private IReadOnlyList<Signal> EvaluateExit(StrategyContext context, OpenPosition position, Candle decision)
    {
        // 1) Trend invalidation: the regime we entered on has flipped — close is back below the trend EMA.
        //    This is the "current trend changed, get out" exit; fixed stop/target still ride intrabar.
        var trendEma = Indicators.Ema(context.Candles, _parameters.TrendEmaPeriod);
        if (trendEma is { } trend && decision.Close < trend)
        {
            return [ExitSignal(context, "TrendExit")];
        }

        // 2) Time stop: once the position has been open across more than the allowed number of trading
        //    sessions, exit regardless of P&L to respect the 1–2 day swing horizon.
        if (SessionsSinceEntry(context.Candles, position.OpenedAtUtc) > _parameters.MaxHoldingDays)
        {
            return [ExitSignal(context, "TimeStop")];
        }

        return [];
    }

    private Signal ExitSignal(StrategyContext context, string reason) =>
        new(
            Name,
            Version,
            context.InstrumentToken,
            context.Symbol,
            SignalDirection.LongExit,
            context.CurrentTimestampUtc,
            Notes: reason);

    /// <summary>
    /// Number of distinct IST trading-session dates seen across candles at or after the position open.
    /// Counting distinct sessions (not elapsed wall-clock) makes the horizon robust to weekends/holidays.
    /// </summary>
    private static int SessionsSinceEntry(IReadOnlyList<Candle> candles, DateTimeOffset openedAtUtc)
    {
        var sessions = new HashSet<DateOnly>();
        for (var i = 0; i < candles.Count; i++)
        {
            var candle = candles[i];
            if (candle.TimestampUtc < openedAtUtc) continue;
            sessions.Add(DateOnly.FromDateTime(candle.TimestampUtc.ToOffset(IndiaStandardTimeOffset).DateTime));
        }

        return sessions.Count;
    }

    private int EntriesToday(int instrumentToken, DateOnly day) =>
        _entryTallies.TryGetValue(instrumentToken, out var tally) && tally.Day == day ? tally.Count : 0;

    private void RecordEntry(int instrumentToken, DateOnly day) =>
        _entryTallies[instrumentToken] = new DailyEntryTally(day, EntriesToday(instrumentToken, day) + 1);

    /// <summary>Rounds a price to the nearest paise (2 dp). Keeps derived stop/target levels tidy.</summary>
    private static decimal RoundToPaise(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private readonly record struct DailyEntryTally(DateOnly Day, int Count);
}
