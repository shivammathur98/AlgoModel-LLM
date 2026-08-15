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
}
