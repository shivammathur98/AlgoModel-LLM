namespace AlgoTrader.BacktestingTests;

using AlgoTrader.Backtesting;
using AlgoTrader.Domain.Costing;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.MarketData;
using AlgoTrader.Domain.Sizing;
using AlgoTrader.Domain.Strategy;
using AlgoTrader.Domain.Trading;
using AlgoTrader.Risk;
using FluentAssertions;
using Xunit;

public sealed class BacktestEngineTests
{
    [Fact]
    public void CandleExecutionModel_RealisticModeAppliesDirectionalSlippageAndHalfSpread()
    {
        var execution = new CandleExecutionModel(new CandleExecutionSettings(
            ExecutionModel.Realistic,
            EntrySlippageBps: 5m,
            ExitSlippageBps: 7m,
            AssumedSpreadBps: 3m));

        var buy = execution.FillMarketOrder(new BacktestFillRequest(OrderSide.Buy, 100m, 10));
        var sell = execution.FillMarketOrder(new BacktestFillRequest(OrderSide.Sell, 100m, 10));

        buy.FillPrice.Should().Be(100.065m);
        buy.SlippageAmount.Should().Be(0.650m);
        sell.FillPrice.Should().Be(99.915m);
        sell.SlippageAmount.Should().Be(0.850m);
    }

    [Fact]
    public void Run_FillsEntryOnNextCandleAndClosesAtTarget()
    {
        var start = IstCandleStart(9, 15);
        var result = Run(new[]
        {
            Candle(start, 100m, 200m, 99m, 100m), // A high here must not fill/exit the future signal.
            Candle(start.AddMinutes(5), 100m, 101m, 99m, 100m),
            Candle(start.AddMinutes(10), 102m, 105m, 101m, 104m)
        });

        result.Trades.Should().ContainSingle();
        var trade = result.Trades[0];
        trade.EntryTimestampUtc.Should().Be(start.AddMinutes(5));
        trade.EntryPrice.Should().Be(100m);
        trade.ExitTimestampUtc.Should().Be(start.AddMinutes(10));
        trade.ExitPrice.Should().Be(104m);
        trade.ExitReason.Should().Be("Target");
        result.FinalCapital.Should().Be(1_040m);
    }

    [Fact]
    public void Run_UsesWorstCaseWhenStopAndTargetAreBothTouchedWithinOneCandle()
    {
        var start = IstCandleStart(9, 15);
        var result = Run(new[]
        {
            Candle(start, 100m, 101m, 99m, 100m),
            Candle(start.AddMinutes(5), 100m, 101m, 99m, 100m),
            Candle(start.AddMinutes(10), 100m, 105m, 97m, 100m)
        });

        result.Trades.Should().ContainSingle();
        result.Trades[0].ExitPrice.Should().Be(98m);
        result.Trades[0].ExitReason.Should().Be("StopLoss");
        result.FinalCapital.Should().Be(980m);
    }

    [Fact]
    public void Run_ClosesOpenPositionAtConfiguredEndOfDayBeforeProcessingNewSignals()
    {
        var start = IstCandleStart(14, 55);
        var result = Run(
            new[]
            {
                Candle(start, 100m, 101m, 99m, 100m),
                Candle(start.AddMinutes(5), 100m, 101m, 99m, 100m),
                Candle(start.AddMinutes(20), 101m, 102m, 100m, 101m)
            },
            new TimeOnly(15, 15));

        result.Trades.Should().ContainSingle();
        result.Trades[0].ExitTimestampUtc.Should().Be(start.AddMinutes(20));
        result.Trades[0].ExitPrice.Should().Be(101m);
        result.Trades[0].ExitReason.Should().Be("EndOfDay");
        result.FinalCapital.Should().Be(1_010m);
    }

    [Fact]
    public void Run_FillsAGappedDownStopAtTheCandleOpenNotTheUntouchedStopLevel()
    {
        // Entry fills at 100 on candle 2; candle 3 gaps straight through the 98 stop, opening at 95.
        // The 98 level was never actually available — the exit must fill at the gapped open (95),
        // not the optimistic stop price. Under the pre-fix engine this returned 98 (FinalCapital 980).
        var start = IstCandleStart(9, 15);
        var result = Run(new[]
        {
            Candle(start, 100m, 101m, 99m, 100m),
            Candle(start.AddMinutes(5), 100m, 101m, 99m, 100m),
            Candle(start.AddMinutes(10), 95m, 96m, 94m, 95m)
        });

        result.Trades.Should().ContainSingle();
        var trade = result.Trades[0];
        trade.ExitReason.Should().Be("StopLoss");
        trade.ExitPrice.Should().Be(95m);
        result.FinalCapital.Should().Be(950m);
    }

    [Fact]
    public void Run_FillsAGappedUpTargetAtTheCandleOpenNotTheUntouchedTargetLevel()
    {
        // Symmetric to the stop case: candle 3 gaps above the 104 target, opening at 110. A long exit
        // that gaps in the trader's favour fills at the better price (110), not the stale target (104).
        var start = IstCandleStart(9, 15);
        var result = Run(new[]
        {
            Candle(start, 100m, 101m, 99m, 100m),
            Candle(start.AddMinutes(5), 100m, 101m, 99m, 100m),
            Candle(start.AddMinutes(10), 110m, 111m, 109m, 110m)
        });

        result.Trades.Should().ContainSingle();
        var trade = result.Trades[0];
        trade.ExitReason.Should().Be("Target");
        trade.ExitPrice.Should().Be(110m);
        result.FinalCapital.Should().Be(1_100m);
    }

    [Fact]
    public void Run_DoesNotLetAStrategyObserveFutureCandlesThroughARetainedHistoryReference()
    {
        // A strategy that captures the candle collection it is handed must never see it grow underneath
        // it on later iterations. The pre-fix engine passed a live view over the mutable history list,
        // so the reference captured on candle 1 later reported 3 candles — a look-ahead leak.
        var start = IstCandleStart(9, 15);
        var strategy = new HistoryAliasProbeStrategy();
        new BacktestEngine().Run(new BacktestRunRequest(
            strategy,
            new[]
            {
                Candle(start, 100m, 101m, 99m, 100m),
                Candle(start.AddMinutes(5), 101m, 102m, 100m, 101m),
                Candle(start.AddMinutes(10), 102m, 103m, 101m, 102m)
            },
            InitialCapital: 1_000m,
            PositionSizer: new RiskAwarePositionSizer(),
            PositionSizing: new BacktestPositionSizingSettings(
                MaxCapitalPerTrade: 1_000m,
                MaxRiskPerTrade: 100m,
                MaxExposurePerSymbol: 1_000m,
                Method: PositionSizingMethod.FixedCapital),
            ExecutionModel: new CandleExecutionModel(new CandleExecutionSettings(ExecutionModel.Ideal)),
            CostCalculator: new ZeroCostCalculator(),
            EndOfDayExitTimeIst: null));

        strategy.CapturedView.Should().NotBeNull();
        strategy.CapturedViewCountObservedOnCandleThree.Should().Be(1);
        strategy.CapturedView!.Count.Should().Be(1);
        strategy.CapturedView[0].TimestampUtc.Should().Be(start);
    }

    private static BacktestRunResult Run(IReadOnlyList<Candle> candles, TimeOnly? endOfDayExitTime = null) =>
        new BacktestEngine().Run(new BacktestRunRequest(
            new FirstCandleEntryStrategy(),
            candles,
            InitialCapital: 1_000m,
            PositionSizer: new RiskAwarePositionSizer(),
            PositionSizing: new BacktestPositionSizingSettings(
                MaxCapitalPerTrade: 1_000m,
                MaxRiskPerTrade: 100m,
                MaxExposurePerSymbol: 1_000m,
                Method: PositionSizingMethod.FixedCapital),
            ExecutionModel: new CandleExecutionModel(new CandleExecutionSettings(ExecutionModel.Ideal)),
            CostCalculator: new ZeroCostCalculator(),
            EndOfDayExitTimeIst: endOfDayExitTime));

    private static Candle Candle(DateTimeOffset timestamp, decimal open, decimal high, decimal low, decimal close) =>
        new(738561, "RELIANCE", "NSE", Timeframe.Minute5, timestamp, open, high, low, close, 1_000L);

    private static DateTimeOffset IstCandleStart(int hour, int minute) =>
        new DateTimeOffset(2026, 1, 15, hour, minute, 0, TimeSpan.FromHours(5.5)).ToUniversalTime();

    private sealed class FirstCandleEntryStrategy : IStrategy
    {
        public string Name => "TestStrategy";
        public string Version => "1.0.0";

        public IReadOnlyList<Signal> OnCandleClosed(StrategyContext context) => context.Candles.Count == 1
            ? [new Signal(Name, Version, context.InstrumentToken, context.Symbol, SignalDirection.LongEntry,
                context.CurrentTimestampUtc, EntryPrice: context.Candles[^1].Close, StopPrice: 98m, TargetPrice: 104m)]
            : [];
    }

    private sealed class ZeroCostCalculator : ITradingCostCalculator
    {
        public TradingCostBreakdown Calculate(CostCalculationContext context) => new(0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m);
    }

    // Captures the candle collection handed to it on candle 1 and re-reads that same reference on
    // candle 3. A correct engine hands out an immutable snapshot, so the captured view still reports
    // exactly one candle; a leaky engine that shares its mutable history makes it report three.
    private sealed class HistoryAliasProbeStrategy : IStrategy
    {
        public string Name => "HistoryAliasProbe";
        public string Version => "1.0.0";

        public IReadOnlyList<Candle>? CapturedView { get; private set; }
        public int CapturedViewCountObservedOnCandleThree { get; private set; } = -1;

        public IReadOnlyList<Signal> OnCandleClosed(StrategyContext context)
        {
            if (context.Candles.Count == 1)
                CapturedView = context.Candles;
            else if (context.Candles.Count == 3 && CapturedView is not null)
                CapturedViewCountObservedOnCandleThree = CapturedView.Count;
            return [];
        }
    }
}
