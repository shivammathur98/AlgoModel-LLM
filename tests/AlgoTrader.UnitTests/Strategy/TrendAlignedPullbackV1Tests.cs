namespace AlgoTrader.UnitTests.Strategy;

using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.MarketData;
using AlgoTrader.Domain.Portfolio;
using AlgoTrader.Domain.Strategy;
using AlgoTrader.Domain.Trading;
using AlgoTrader.Strategy;
using FluentAssertions;
using Xunit;

public sealed class TrendAlignedPullbackV1Tests
{
    private const int Token = 738561;

    [Fact]
    public void Entry_Emitted_OnTrendAlignedPullback()
    {
        var strategy = new TrendAlignedPullbackV1(Params());

        var signals = strategy.OnCandleClosed(Ctx(Uptrend()));

        signals.Should().ContainSingle();
        var signal = signals[0];
        signal.Direction.Should().Be(SignalDirection.LongEntry);
        signal.EntryPrice.Should().Be(105m);
        signal.StopPrice.Should().Be(103.95m);    // ATR stop (1.95) capped by 1% of 105 -> 105 - 1.05
        signal.TargetPrice.Should().Be(105.79m);   // 105 * (1 + 0.75%)
    }

    [Fact]
    public void Entry_Suppressed_WhenTrendNotRising()
    {
        // Falling closes -> trend EMA is not rising, so we stand aside regardless of bar shape.
        new TrendAlignedPullbackV1(Params()).OnCandleClosed(Ctx(Downtrend())).Should().BeEmpty();
    }

    [Fact]
    public void Entry_Suppressed_WhenNoPullbackToFastEma()
    {
        // Same uptrend, but the decision bar's low never reaches the pullback EMA (104.5).
        var candles = Uptrend();
        candles[5] = Bar(5, open: 104.7m, high: 105.2m, low: 104.6m, close: 105m);

        new TrendAlignedPullbackV1(Params()).OnCandleClosed(Ctx(candles)).Should().BeEmpty();
    }

    [Fact]
    public void Entry_Suppressed_BeforeEntryWindow()
    {
        // Decision bar is at 10:30 IST; a 14:00 start means it is before the window.
        new TrendAlignedPullbackV1(Params(start: new TimeOnly(14, 0)))
            .OnCandleClosed(Ctx(Uptrend())).Should().BeEmpty();
    }

    [Fact]
    public void MaxTradesPerDay_CapsSecondEntrySameDay()
    {
        var strategy = new TrendAlignedPullbackV1(Params(maxTrades: 1));

        strategy.OnCandleClosed(Ctx(Uptrend())).Should().ContainSingle(); // first entry
        strategy.OnCandleClosed(Ctx(Uptrend())).Should().BeEmpty();        // capped on same day
    }

    [Fact]
    public void Exit_OnTrendInvalidation_WhenCloseBelowTrendEma()
    {
        var strategy = new TrendAlignedPullbackV1(Params());
        var candles = Downtrend();

        var signals = strategy.OnCandleClosed(Ctx(candles, Position(candles[0].TimestampUtc)));

        signals.Should().ContainSingle();
        signals[0].Direction.Should().Be(SignalDirection.LongExit);
        signals[0].Notes.Should().Be("TrendExit");
    }

    [Fact]
    public void Exit_OnTimeStop_WhenHeldBeyondMaxHoldingDays()
    {
        var strategy = new TrendAlignedPullbackV1(Params(maxDays: 2));
        var candles = MultiDayUptrend(); // spans 3 distinct IST sessions, close stays above trend EMA

        var signals = strategy.OnCandleClosed(Ctx(candles, Position(candles[0].TimestampUtc)));

        signals.Should().ContainSingle();
        signals[0].Direction.Should().Be(SignalDirection.LongExit);
        signals[0].Notes.Should().Be("TimeStop");
    }

    [Fact]
    public void Exit_None_WhenTrendHoldsWithinHorizon()
    {
        var strategy = new TrendAlignedPullbackV1(Params());
        var candles = Uptrend(); // single session, close above trend EMA

        strategy.OnCandleClosed(Ctx(candles, Position(candles[0].TimestampUtc))).Should().BeEmpty();
    }

    // ---- helpers ------------------------------------------------------------

    private static TrendAlignedPullbackParameters Params(int maxTrades = 1, int maxDays = 2, TimeOnly? start = null) => new()
    {
        Version = "1.0.0",
        TrendEmaPeriod = 3,
        PullbackEmaPeriod = 2,
        TrendSlopeLookback = 1,
        AtrPeriod = 2,
        AtrStopMultiplier = 1.5m,
        MaxStopLossPercent = 1.0m,
        TargetPercent = 0.75m,
        MaxHoldingDays = maxDays,
        MaxTradesPerDay = maxTrades,
        EntryStartTime = start ?? new TimeOnly(9, 30),
        EntryCutoffTime = new TimeOnly(14, 45)
    };

    /// <summary>Rising closes with a decision bar that dips to the fast EMA then closes back above it.</summary>
    private static List<Candle> Uptrend() => new()
    {
        Bar(0, 99.8m, 100.2m, 99.6m, 100m),
        Bar(1, 100.3m, 101.2m, 100.1m, 101m),
        Bar(2, 101.3m, 102.2m, 101.1m, 102m),
        Bar(3, 102.3m, 103.2m, 102.1m, 103m),
        Bar(4, 103.3m, 104.2m, 103.1m, 104m),
        Bar(5, 104.5m, 105.2m, 103.8m, 105m) // dip to 103.8 (< fast EMA 104.5), close 105 up bar
    };

    /// <summary>Falling closes: trend EMA is not rising and price sits below it.</summary>
    private static List<Candle> Downtrend() => new()
    {
        Bar(0, 105.2m, 105.4m, 104.8m, 105m),
        Bar(1, 104.2m, 104.4m, 103.8m, 104m),
        Bar(2, 103.2m, 103.4m, 102.8m, 103m),
        Bar(3, 102.2m, 102.4m, 101.8m, 102m),
        Bar(4, 101.2m, 101.4m, 100.8m, 101m),
        Bar(5, 100.7m, 100.9m, 99.6m, 100m)
    };

    /// <summary>Rising closes spread over three consecutive IST sessions (two bars per day).</summary>
    private static List<Candle> MultiDayUptrend() => new()
    {
        Bar(0, 99.8m, 100.2m, 99.6m, 100m, dayOffset: 0),
        Bar(1, 100.3m, 101.2m, 100.1m, 101m, dayOffset: 0),
        Bar(0, 101.3m, 102.2m, 101.1m, 102m, dayOffset: 1),
        Bar(1, 102.3m, 103.2m, 102.1m, 103m, dayOffset: 1),
        Bar(0, 103.3m, 104.2m, 103.1m, 104m, dayOffset: 2),
        Bar(1, 104.3m, 105.2m, 104.1m, 105m, dayOffset: 2)
    };

    private static Candle Bar(int index, decimal open, decimal high, decimal low, decimal close, long vol = 1_000, int dayOffset = 0)
    {
        var day = new DateOnly(2026, 1, 15).AddDays(dayOffset);
        var timestamp = new DateTimeOffset(day.Year, day.Month, day.Day, 9, 15, 0, TimeSpan.FromHours(5.5))
            .AddMinutes(15 * index).ToUniversalTime();
        return new Candle(Token, "RELIANCE", "NSE", Timeframe.Minute15, timestamp, open, high, low, close, vol);
    }

    private static StrategyContext Ctx(IReadOnlyList<Candle> candles, OpenPosition? position = null) => new()
    {
        Symbol = "RELIANCE",
        InstrumentToken = Token,
        Timeframe = Timeframe.Minute15,
        Candles = candles,
        OpenPosition = position,
        CurrentTimestampUtc = candles[^1].TimestampUtc,
        AvailableCapital = 100_000m
    };

    private static OpenPosition Position(DateTimeOffset openedAtUtc) =>
        new(Token, "RELIANCE", "TrendAlignedPullbackV1", 10, 105m, openedAtUtc, 103.95m, 105.79m, "corr-1");
}
