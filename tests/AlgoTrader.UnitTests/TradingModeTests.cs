namespace AlgoTrader.UnitTests;

using AlgoTrader.Domain.Enums;
using FluentAssertions;
using Xunit;

public class TradingModeTests
{
    [Theory]
    [InlineData(TradingMode.Research, false)]
    [InlineData(TradingMode.Backtest, false)]
    [InlineData(TradingMode.Paper, true)]
    [InlineData(TradingMode.Live, true)]
    public void RequiresBrokerConnection_OnlyForPaperAndLive(TradingMode mode, bool expected)
    {
        mode.RequiresBrokerConnection().Should().Be(expected);
    }

    [Theory]
    [InlineData(TradingMode.Research, false)]
    [InlineData(TradingMode.Backtest, false)]
    [InlineData(TradingMode.Paper, true)]
    [InlineData(TradingMode.Live, true)]
    public void UsesLiveMarketData_OnlyForPaperAndLive(TradingMode mode, bool expected)
    {
        mode.UsesLiveMarketData().Should().Be(expected);
    }

    [Theory]
    [InlineData(TradingMode.Research, false)]
    [InlineData(TradingMode.Backtest, false)]
    [InlineData(TradingMode.Paper, false)]
    [InlineData(TradingMode.Live, true)]
    public void AllowsRealOrders_OnlyForLive(TradingMode mode, bool expected)
    {
        mode.AllowsRealOrders().Should().Be(expected);
    }

    [Theory]
    [InlineData(TradingMode.Research, true)]
    [InlineData(TradingMode.Backtest, true)]
    [InlineData(TradingMode.Paper, false)]
    [InlineData(TradingMode.Live, false)]
    public void UsesHistoricalDataOnly_ForResearchAndBacktest(TradingMode mode, bool expected)
    {
        mode.UsesHistoricalDataOnly().Should().Be(expected);
    }

    [Theory]
    [InlineData(TradingMode.Research, false)]
    [InlineData(TradingMode.Backtest, true)]
    [InlineData(TradingMode.Paper, true)]
    [InlineData(TradingMode.Live, false)]
    public void UsesSimulatedExecution_ForBacktestAndPaper(TradingMode mode, bool expected)
    {
        mode.UsesSimulatedExecution().Should().Be(expected);
    }
}
