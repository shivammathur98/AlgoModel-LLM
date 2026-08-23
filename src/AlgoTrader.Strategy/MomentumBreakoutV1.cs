namespace AlgoTrader.Strategy;

using AlgoTrader.Domain.MarketData;
using AlgoTrader.Domain.Portfolio;
using AlgoTrader.Domain.Strategy;
using AlgoTrader.Domain.Trading;

/// <summary>
/// Long-only intraday momentum breakout hypothesis (§11, §12). On each closed decision candle the
/// strategy enters long when price breaks the prior N-bar high on expanding volume (with an optional
/// price-above-EMA trend filter), inside a configured intraday entry window and under a per-day trade
/// cap. Protective fixed stop/target levels are attached to the entry signal and enforced intrabar by
/// the execution/backtest layer; the strategy additionally emits explicit exits for the forced
/// end-of-day flat, a maximum-holding-time exit, and — when enabled — a trailing stop.
/// <para>
/// The strategy is broker-agnostic: it references only Domain market-data, portfolio and trading types
/// and never touches broker or execution classes (§16). It is deterministic — its only state is a
/// per-instrument, per-day entry counter derived purely from the ordered candle stream — and it must be
/// driven single-threaded by one sequential candle feed (as the engine and live loop both do). A fresh
/// instance should be used per independent backtest run.
/// </para>
/// <para>
/// IMPORTANT: this strategy is an unvalidated research hypothesis. Nothing here asserts or implies
/// profitability; parameters are scenario values to be studied, not tuned claims (§12).
/// </para>
/// </summary>
public sealed class MomentumBreakoutV1 : IStrategy
{
    /// <summary>IST is UTC+05:30; all intraday time-of-day gating is evaluated in IST.</summary>
    private static readonly TimeSpan IndiaStandardTimeOffset = TimeSpan.FromHours(5.5);

    private readonly MomentumBreakoutParameters _parameters;

    /// <summary>Per-instrument entry tally for the current trading day; resets when the day rolls.</summary>
    private readonly Dictionary<int, DailyEntryTally> _entryTallies = [];

    public MomentumBreakoutV1(MomentumBreakoutParameters parameters)
    {
        _parameters = parameters ?? throw new ArgumentNullException(nameof(parameters));
        _parameters.Validate();
    }

    /// <inheritdoc />
    public string Name => "MomentumBreakoutV1";

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

        // When holding, evaluate exits only. A single symbol never pyramids in this hypothesis.
        if (context.OpenPosition is { } position)
        {
            return EvaluateExit(context, position, decision, istTime);
        }

        return EvaluateEntry(context, decision, istTime, istDate);
    }

    private IReadOnlyList<Signal> EvaluateEntry(StrategyContext context, Candle decision, TimeOnly istTime, DateOnly istDate)
    {
        // Intraday entry window: no entries before the open buffer or at/after the cutoff.
        if (istTime < _parameters.EntryStartTime || istTime >= _parameters.EntryCutoffTime) return [];

        // Strategy-level per-instrument daily trade cap.
        if (EntriesToday(context.InstrumentToken, istDate) >= _parameters.MaxTradesPerDay) return [];

        var priorHigh = Indicators.PriorHighestHigh(context.Candles, _parameters.LookbackBars);
        var priorAverageVolume = Indicators.PriorAverageVolume(context.Candles, _parameters.LookbackBars);
        if (priorHigh is null || priorAverageVolume is null) return []; // insufficient history

        // Breakout: the decision candle closes strictly above the prior N-bar high.
        if (decision.Close <= priorHigh.Value) return [];

        // Volume expansion: current volume exceeds the multiple of the recent average.
        if (decision.Volume < _parameters.VolumeMultiplier * priorAverageVolume.Value) return [];

        // Optional trend filter: price must be above its EMA.
        if (_parameters.UseTrendFilter)
        {
            var ema = Indicators.Ema(context.Candles, _parameters.EmaPeriod);
            if (ema is null || decision.Close <= ema.Value) return [];
        }

        var entryReference = decision.Close;
        var stopPrice = RoundToPaise(entryReference * (1m - _parameters.StopLossPercent / 100m));
        decimal? targetPrice = _parameters.UseTrailingStop
            ? null
            : RoundToPaise(entryReference * (1m + _parameters.TargetPercent / 100m));

        RecordEntry(context.InstrumentToken, istDate);

        var notes = $"Breakout close {entryReference} > prior {_parameters.LookbackBars}-bar high {priorHigh.Value}; " +
                    $"volume x{_parameters.VolumeMultiplier} baseline";
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

    private IReadOnlyList<Signal> EvaluateExit(StrategyContext context, OpenPosition position, Candle decision, TimeOnly istTime)
    {
        // 1) Forced end-of-day flat: never carry an intraday position past the exit time.
        if (istTime >= _parameters.ExitTime)
        {
            return [ExitSignal(context, "EndOfDay")];
        }

        // 2) Maximum-holding-time exit, independent of P&L.
        if (context.CurrentTimestampUtc - position.OpenedAtUtc >= TimeSpan.FromMinutes(_parameters.MaximumHoldingMinutes))
        {
            return [ExitSignal(context, "TimeExit")];
        }

        // 3) Trailing stop (only when enabled; the fixed stop/target ride on the entry signal and are
        //    enforced intrabar by the execution layer). Trail below the highest high seen since entry.
        if (_parameters.UseTrailingStop)
        {
            var peakHigh = HighestHighSinceEntry(context.Candles, position.OpenedAtUtc);
            if (peakHigh is { } peak)
            {
                var trailingStop = RoundToPaise(peak * (1m - _parameters.StopLossPercent / 100m));
                if (decision.Close <= trailingStop)
                {
                    return [ExitSignal(context, "TrailingStop")];
                }
            }
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

    /// <summary>Highest high across every closed candle at or after the position's open timestamp.</summary>
    private static decimal? HighestHighSinceEntry(IReadOnlyList<Candle> candles, DateTimeOffset openedAtUtc)
    {
        decimal? peak = null;
        for (var i = 0; i < candles.Count; i++)
        {
            var candle = candles[i];
            if (candle.TimestampUtc < openedAtUtc) continue;
            if (peak is null || candle.High > peak.Value) peak = candle.High;
        }

        return peak;
    }

    private int EntriesToday(int instrumentToken, DateOnly day) =>
        _entryTallies.TryGetValue(instrumentToken, out var tally) && tally.Day == day ? tally.Count : 0;

    private void RecordEntry(int instrumentToken, DateOnly day) =>
        _entryTallies[instrumentToken] = new DailyEntryTally(day, EntriesToday(instrumentToken, day) + 1);

    /// <summary>Rounds a price to the nearest paise (2 dp). Keeps derived stop/target levels tidy.</summary>
    private static decimal RoundToPaise(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private readonly record struct DailyEntryTally(DateOnly Day, int Count);
}
