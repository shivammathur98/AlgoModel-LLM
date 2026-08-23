namespace AlgoTrader.BacktestingTests;

using AlgoTrader.Backtesting;
using AlgoTrader.Domain.Costing;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.MarketData;
using AlgoTrader.Domain.Sizing;
using AlgoTrader.Domain.Strategy;
using AlgoTrader.Risk;
using AlgoTrader.Strategy;
using FluentAssertions;
using Xunit;

/// <summary>
/// Proves the real <see cref="TrendAlignedPullbackV1"/> multi-session swing hypothesis is engine-compatible:
/// driven end-to-end through the <see cref="BacktestEngine"/> it produces exactly one round-trip trade that is
/// entered on one IST session and still open when the data ends a LATER IST session, so the engine closes it via
/// its post-data "EndOfData" path (§16 no look-ahead: the strategy only ever sees closed candles).
/// <para>
/// This test asserts MECHANICS ONLY — entry/exit timing, price, quantity, exit reason and the overnight hold.
/// The strategy is an explicitly unvalidated research hypothesis (§11, §12); any incidental P&amp;L here is a
/// structural by-product of the hand-built scenario, NOT a validated edge, so this test NEVER asserts profitability.
/// </para>
/// </summary>
public sealed class TrendAlignedPullbackBacktestTests
{
    [Fact]
    public void Run_SwingEntryHeldAcrossSessions_ClosesAtEndOfDataOnLaterIstSession()
    {
        var candles = new[]
        {
            Bar(0, 99.8m, 100.2m, 99.6m, 100.0m, 0),
            Bar(1, 100.3m, 101.2m, 100.1m, 101.0m, 0),
            Bar(2, 101.3m, 102.2m, 101.1m, 102.0m, 0),
            Bar(3, 102.3m, 103.2m, 102.1m, 103.0m, 0),   // idx3: LongEntry fires here (earliest structurally-fireable bar)
            Bar(4, 103.0m, 103.5m, 102.8m, 103.4m, 0),   // idx4: entry FILLS at this Open = 103.0
            Bar(5, 103.4m, 103.6m, 103.1m, 103.5m, 0),   // idx5: hold
            Bar(0, 103.5m, 103.6m, 103.35m, 103.55m, 1), // idx6: hold, overnight
            Bar(1, 103.55m, 103.65m, 103.4m, 103.6m, 1), // idx7: hold
            Bar(2, 103.6m, 103.7m, 103.45m, 103.65m, 1)  // idx8: last candle -> engine EndOfData close at 103.65
        };

        var result = new BacktestEngine().Run(new BacktestRunRequest(
            Strategy: new TrendAlignedPullbackV1(Params()),
            Candles: candles,
            InitialCapital: 1_000_000m,
            PositionSizer: new RiskAwarePositionSizer(),
            PositionSizing: new BacktestPositionSizingSettings(
                MaxCapitalPerTrade: 1_000_000m,
                MaxRiskPerTrade: 103m,
                MaxExposurePerSymbol: 1_000_000m,
                Method: PositionSizingMethod.RiskBased),
            ExecutionModel: new CandleExecutionModel(new CandleExecutionSettings(ExecutionModel.Ideal)),
            CostCalculator: new ZeroCostCalculator(),
            EndOfDayExitTimeIst: null,     // MUST stay null, else the position is force-closed before EndOfData.
            MaximumHoldingTime: null,      // MUST stay null, else a TimeExit would pre-empt the EndOfData close.
            Product: ProductType.Delivery)); // Delivery: this is a multi-session swing, not an intraday trade.

        // Exactly one signal became exactly one round-trip trade; nothing was rejected.
        result.GeneratedSignals.Should().Be(1);
        result.RejectedSignals.Should().BeEmpty();
        result.Metrics.TotalTrades.Should().Be(1);
        result.Trades.Should().ContainSingle();

        var trade = result.Trades[0];

        // THE key claim: the strategy never self-exited and no stop/target was touched, so the engine closed
        // the still-open position at the very end of the data.
        trade.ExitReason.Should().Be("EndOfData");

        trade.StrategyName.Should().Be("TrendAlignedPullbackV1");
        trade.EntryPrice.Should().Be(103.0m); // Ideal fill of idx4.Open.
        trade.ExitPrice.Should().Be(103.65m); // idx8.Close, the last candle.
        trade.Quantity.Should().Be(100);

        // Filled on the candle AFTER the decision bar (idx4), closed on the final candle (idx8).
        trade.EntryTimestampUtc.Should().Be(candles[4].TimestampUtc);
        trade.ExitTimestampUtc.Should().Be(candles[8].TimestampUtc);

        // OVERNIGHT proof: the IST calendar date of exit is strictly AFTER the IST calendar date of entry.
        var ist = TimeSpan.FromHours(5.5);
        var entryIstDate = DateOnly.FromDateTime(trade.EntryTimestampUtc.ToOffset(ist).DateTime);
        var exitIstDate = DateOnly.FromDateTime(trade.ExitTimestampUtc.ToOffset(ist).DateTime);
        (exitIstDate > entryIstDate).Should().BeTrue();
        trade.HoldingTime.Should().BeGreaterThan(TimeSpan.FromHours(20));

        // NOTE: any resulting NetPnl/GrossPnl is incidental to this hand-built scenario and is deliberately
        // NOT asserted — profitability is an unvalidated hypothesis (§11, §12), not a success criterion.
    }

    private static TrendAlignedPullbackParameters Params() => new()
    {
        Version = "1.0.0",
        TrendEmaPeriod = 3,
        PullbackEmaPeriod = 2,
        TrendSlopeLookback = 1,
        AtrPeriod = 2,
        AtrStopMultiplier = 1.5m,
        MaxStopLossPercent = 1.0m,
        TargetPercent = 0.75m,
        MaxHoldingDays = 2,
        MaxTradesPerDay = 1,
        EntryStartTime = new TimeOnly(9, 30),
        EntryCutoffTime = new TimeOnly(14, 45)
    };

    private static Candle Bar(int slot, decimal open, decimal high, decimal low, decimal close, int dayOffset)
    {
        var day = new DateOnly(2026, 1, 15).AddDays(dayOffset);
        var ts = new DateTimeOffset(day.Year, day.Month, day.Day, 9, 15, 0, TimeSpan.FromHours(5.5)).AddMinutes(15 * slot).ToUniversalTime();
        return new Candle(738561, "RELIANCE", "NSE", Timeframe.Minute15, ts, open, high, low, close, 1_000L);
    }

    private sealed class ZeroCostCalculator : ITradingCostCalculator
    {
        public TradingCostBreakdown Calculate(CostCalculationContext context) => new(0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m);
    }
}
