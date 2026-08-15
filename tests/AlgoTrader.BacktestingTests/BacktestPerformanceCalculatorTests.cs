namespace AlgoTrader.BacktestingTests;

using AlgoTrader.Backtesting;
using FluentAssertions;
using Xunit;

public sealed class BacktestPerformanceCalculatorTests
{
    [Fact]
    public void Calculate_ProducesCostAwareClosedTradeMetricsAndCapitalCurve()
    {
        var calculator = new BacktestPerformanceCalculator();
        var entry = new DateTimeOffset(2026, 1, 15, 4, 0, 0, TimeSpan.Zero);
        var trades = new[]
        {
            Trade("one", entry, 100m, 110m, 10, 5m, 1m),
            Trade("two", entry.AddDays(1), 100m, 90m, 10, 5m, 1m),
            Trade("three", entry.AddDays(2), 100m, 120m, 10, 5m, 1m)
        };

        var metrics = calculator.Calculate(1_000m, trades);

        metrics.TotalTrades.Should().Be(3);
        metrics.WinningTrades.Should().Be(2);
        metrics.LosingTrades.Should().Be(1);
        metrics.WinRatePercent.Should().BeApproximately(66.66666666666666666666666667m, 0.00000000000000000000000001m);
        metrics.GrossPnl.Should().Be(200m);
        metrics.TotalCharges.Should().Be(15m);
        metrics.TotalSlippage.Should().Be(3m);
        metrics.NetPnl.Should().Be(185m);
        metrics.MaximumDrawdown.Should().Be(105m);
        metrics.ProfitFactor.Should().BeApproximately(290m / 105m, 0.00000000000000000000000001m);
        metrics.MaximumConsecutiveWins.Should().Be(1);
        metrics.MaximumConsecutiveLosses.Should().Be(1);
        metrics.CapitalCurve.Should().HaveCount(4);
        metrics.DailyPnl.Should().HaveCount(3);
    }

    [Fact]
    public void DataSplit_SeparatesOutOfSampleDataFromDevelopmentPeriods()
    {
        var start = new DateTimeOffset(2021, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var split = new BacktestDataSplit(start.AddYears(3), start.AddYears(4));
        split.Validate(start, start.AddYears(6));

        split.GetPartition(start.AddYears(2)).Should().Be(BacktestDataPartition.Training);
        split.GetPartition(start.AddYears(3).AddDays(1)).Should().Be(BacktestDataPartition.Validation);
        split.GetPartition(start.AddYears(5)).Should().Be(BacktestDataPartition.OutOfSample);
    }

    private static BacktestTrade Trade(string id, DateTimeOffset entry, decimal entryPrice, decimal exitPrice, int quantity, decimal charges, decimal slippage) => new(
        id, "TestStrategy", "1.0.0", 738561, "RELIANCE", entry, entryPrice, entry.AddMinutes(5), exitPrice,
        quantity, charges / 2m, charges / 2m, slippage / 2m, slippage / 2m, "Signal");
}
