namespace AlgoTrader.UnitTests.MarketData;

using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.MarketData;
using AlgoTrader.MarketData;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public sealed class CandleAggregatorTests
{
    private const int Token = 123;
    private const string Symbol = "TEST";
    private const string Exchange = "NSE";
    private readonly CandleAggregator _aggregator;
    private readonly DateTimeOffset _dayStartIst;

    public CandleAggregatorTests()
    {
        _aggregator = new CandleAggregator(
            NullLogger<CandleAggregator>.Instance,
            _ => (Symbol, Exchange));

        // Let's use an aligned time for ease of testing: 09:15:00 IST = 03:45:00 UTC
        _dayStartIst = new DateTimeOffset(2026, 8, 25, 3, 45, 0, TimeSpan.Zero);
    }

    [Fact]
    public void CandleAggregator_CumulativeVolume_ProducesCorrectDeltaVolume()
    {
        // First tick establishes baseline (Volume = 0)
        var t1 = TickAt(0, 100m, volume: 1000);
        _aggregator.OnTick(t1, Timeframe.Minute1).Should().BeNull();
        var current = _aggregator.GetCurrentCandle(Token, Timeframe.Minute1);
        current.Should().NotBeNull();
        current!.Volume.Should().Be(0);

        // Second tick in same bar: delta is 1050 - 1000 = 50
        var t2 = TickAt(10, 101m, volume: 1050);
        _aggregator.OnTick(t2, Timeframe.Minute1).Should().BeNull();
        current = _aggregator.GetCurrentCandle(Token, Timeframe.Minute1);
        current!.Volume.Should().Be(50);

        // Third tick in same bar: delta is 1100 - 1050 = 50. Total = 100.
        var t3 = TickAt(20, 102m, volume: 1100);
        _aggregator.OnTick(t3, Timeframe.Minute1).Should().BeNull();
        current = _aggregator.GetCurrentCandle(Token, Timeframe.Minute1);
        current!.Volume.Should().Be(100);

        // Fourth tick crosses into next bar (after 60s). It closes the previous candle.
        // It brings cumulative to 1120. Delta is 20 for the new bar.
        var t4 = TickAt(65, 103m, volume: 1120);
        var closed = _aggregator.OnTick(t4, Timeframe.Minute1);
        
        closed.Should().NotBeNull();
        closed!.Volume.Should().Be(100); // the old candle closed with 100

        // The new current candle should have a volume of 20 (1120 - 1100).
        current = _aggregator.GetCurrentCandle(Token, Timeframe.Minute1);
        current!.Volume.Should().Be(20);
    }

    [Fact]
    public void CandleAggregator_VolumeReset_OnNewBar_StartsFromZero()
    {
        var t1 = TickAt(0, 100m, volume: 5000);
        _aggregator.OnTick(t1, Timeframe.Minute1);

        // Skip to next bar, meaning there are no more ticks in the first bar.
        var t2 = TickAt(65, 101m, volume: 5050);
        var closed = _aggregator.OnTick(t2, Timeframe.Minute1);

        // Old candle should have 0 volume (only had 1 tick which formed the baseline)
        closed!.Volume.Should().Be(0);

        // New candle gets delta of 50
        var current = _aggregator.GetCurrentCandle(Token, Timeframe.Minute1);
        current!.Volume.Should().Be(50);
    }

    [Fact]
    public void CandleAggregator_DayRollover_DoesNotResultInNegativeVolume()
    {
        // End of day tick
        var t1 = TickAt(0, 100m, volume: 100000);
        _aggregator.OnTick(t1, Timeframe.Minute1);

        // Next day tick (cumulative resets at broker). Tick crosses bar.
        var t2 = TickAt(10000, 101m, volume: 100);
        var closed = _aggregator.OnTick(t2, Timeframe.Minute1);

        // The new candle should not have negative volume. Delta = Math.Max(0, 100 - 100000) = 0
        var current = _aggregator.GetCurrentCandle(Token, Timeframe.Minute1);
        current!.Volume.Should().Be(0);
    }

    private Tick TickAt(int secondsFromStart, decimal price, long volume) =>
        new(Token, _dayStartIst.AddSeconds(secondsFromStart), price, price, price, volume);
}
