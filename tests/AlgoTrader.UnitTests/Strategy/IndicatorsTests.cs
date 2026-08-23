namespace AlgoTrader.UnitTests.Strategy;

using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.MarketData;
using AlgoTrader.Strategy;
using FluentAssertions;
using Xunit;

public sealed class IndicatorsTests
{
    [Fact]
    public void PriorHighestHigh_ExcludesDecisionBar()
    {
        var candles = new[]
        {
            Candle(0, 10m, 12m), // prior
            Candle(1, 10m, 15m), // prior (highest)
            Candle(2, 10m, 11m), // prior
            Candle(3, 10m, 99m)  // decision bar — must be ignored
        };

        Indicators.PriorHighestHigh(candles, 3).Should().Be(15m);
    }

    [Fact]
    public void PriorAverageVolume_AveragesPriorBarsOnly()
    {
        var candles = new[]
        {
            Candle(0, 10m, 11m, volume: 100),
            Candle(1, 10m, 11m, volume: 200),
            Candle(2, 10m, 11m, volume: 300),
            Candle(3, 10m, 11m, volume: 9_999) // decision bar excluded
        };

        Indicators.PriorAverageVolume(candles, 3).Should().Be(200m);
    }

    [Fact]
    public void PriorHighestHigh_ReturnsNull_WhenInsufficientHistory()
    {
        var candles = new[] { Candle(0, 10m, 11m), Candle(1, 10m, 11m) };

        Indicators.PriorHighestHigh(candles, 3).Should().BeNull();
    }

    [Fact]
    public void Ema_SeedsWithSmaThenSmooths()
    {
        // period 3 over [10,20,30,40]: seed SMA=20; k=0.5; ema=40*0.5+20*0.5=30.
        var values = new[] { 10m, 20m, 30m, 40m };

        Indicators.Ema(values, 3).Should().Be(30m);
    }

    [Fact]
    public void Ema_ReturnsNull_WhenFewerValuesThanPeriod()
    {
        Indicators.Ema(new[] { 10m, 20m }, 3).Should().BeNull();
    }

    [Fact]
    public void EmaSeries_IsNullBeforeSeed_ThenTracksEma()
    {
        var series = Indicators.EmaSeries(new[] { 10m, 20m, 30m, 40m }, 3);

        series[0].Should().BeNull();
        series[1].Should().BeNull();
        series[2].Should().Be(20m); // SMA seed of first 3
        series[3].Should().Be(30m); // 40*0.5 + 20*0.5
    }

    [Fact]
    public void EmaSeries_SlopeIsPositive_WhenRising()
    {
        var series = Indicators.EmaSeries(new[] { 10m, 20m, 30m, 40m }, 3);

        (series[3]!.Value - series[2]!.Value).Should().BePositive();
    }

    [Fact]
    public void Atr_UsesWilderSeed_OverTrueRanges()
    {
        // TR1 = 3, TR2 = 2 → seed ATR(2) = 2.5.
        var candles = new[]
        {
            Candle(0, low: 8m, high: 10m, close: 9m),
            Candle(1, low: 9m, high: 12m, close: 11m),
            Candle(2, low: 11m, high: 13m, close: 12m)
        };

        Indicators.Atr(candles, 2).Should().Be(2.5m);
    }

    [Fact]
    public void Atr_ReturnsNull_WhenNotEnoughCandles()
    {
        var candles = new[] { Candle(0, 8m, 10m, 9m), Candle(1, 9m, 12m, 11m) };

        Indicators.Atr(candles, 5).Should().BeNull();
    }

    private static Candle Candle(int index, decimal low, decimal high, decimal close, long volume = 1_000) =>
        new(738561, "RELIANCE", "NSE", Timeframe.Minute15,
            new DateTimeOffset(2026, 1, 15, 9, 15, 0, TimeSpan.FromHours(5.5)).AddMinutes(15 * index).ToUniversalTime(),
            Open: low, High: high, Low: low, Close: close, Volume: volume);

    private static Candle Candle(int index, decimal low, decimal high, long volume = 1_000) =>
        new(738561, "RELIANCE", "NSE", Timeframe.Minute5,
            new DateTimeOffset(2026, 1, 15, 9, 15, 0, TimeSpan.FromHours(5.5)).AddMinutes(5 * index).ToUniversalTime(),
            Open: low, High: high, Low: low, Close: high, Volume: volume);
}
