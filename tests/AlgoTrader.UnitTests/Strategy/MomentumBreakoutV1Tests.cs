namespace AlgoTrader.UnitTests.Strategy;

using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.MarketData;
using AlgoTrader.Domain.Portfolio;
using AlgoTrader.Domain.Strategy;
using AlgoTrader.Domain.Trading;
using AlgoTrader.Strategy;
using FluentAssertions;
using Xunit;

public sealed class MomentumBreakoutV1Tests
{
    private const int Token = 738561;

    [Fact]
    public void Entry_Emitted_OnBreakoutWithVolumeExpansion_InsideWindow()
    {
        var strategy = new MomentumBreakoutV1(Params());

        var signals = strategy.OnCandleClosed(Ctx(Breakout(new TimeOnly(9, 50))));

        signals.Should().ContainSingle();
        var signal = signals[0];
        signal.Direction.Should().Be(SignalDirection.LongEntry);
        signal.EntryPrice.Should().Be(100m);
        signal.StopPrice.Should().Be(99.50m);   // 100 * (1 - 0.5%)
        signal.TargetPrice.Should().Be(101.00m); // 100 * (1 + 1.0%)
    }

    [Fact]
    public void Entry_Suppressed_WhenCloseDoesNotExceedPriorHigh()
    {
        var strategy = new MomentumBreakoutV1(Params());
        var candles = new List<Candle>
        {
            Bar(new TimeOnly(9, 35), high: 100m, close: 100m, vol: 100),
            Bar(new TimeOnly(9, 40), high: 100m, close: 100m, vol: 100),
            Bar(new TimeOnly(9, 45), high: 100m, close: 100m, vol: 100),
            Bar(new TimeOnly(9, 50), high: 100m, close: 100m, vol: 300) // close == prior high, not a breakout
        };

        strategy.OnCandleClosed(Ctx(candles)).Should().BeEmpty();
    }

    [Fact]
    public void Entry_Suppressed_WhenVolumeBelowMultiple()
    {
        var strategy = new MomentumBreakoutV1(Params());

        // 100 < 1.5 * 100 baseline.
        strategy.OnCandleClosed(Ctx(Breakout(new TimeOnly(9, 50), decisionVol: 100))).Should().BeEmpty();
    }

    [Fact]
    public void Entry_Suppressed_BeforeWindowAndAtCutoff()
    {
        var strategy = new MomentumBreakoutV1(Params());

        strategy.OnCandleClosed(Ctx(Breakout(new TimeOnly(9, 15)))).Should().BeEmpty();   // before 09:20 start
        strategy.OnCandleClosed(Ctx(Breakout(new TimeOnly(14, 30)))).Should().BeEmpty();  // at 14:30 cutoff
    }

    [Fact]
    public void TrendFilter_BlocksBreakoutBelowEma_ButAllowsWhenDisabled()
    {
        // Older bars sit well above the recent 3-bar high, so EMA(5) > breakout close.
        List<Candle> Data() => new()
        {
            Bar(new TimeOnly(9, 25), high: 120m, close: 120m, vol: 100),
            Bar(new TimeOnly(9, 30), high: 118m, close: 118m, vol: 100),
            Bar(new TimeOnly(9, 35), high: 100m, close: 100m, vol: 100),
            Bar(new TimeOnly(9, 40), high: 100m, close: 100m, vol: 100),
            Bar(new TimeOnly(9, 45), high: 100m, close: 100m, vol: 100),
            Bar(new TimeOnly(9, 50), high: 101m, close: 101m, vol: 300) // breaks last-3 high (100), below EMA(5)
        };

        new MomentumBreakoutV1(Params(trend: true)).OnCandleClosed(Ctx(Data())).Should().BeEmpty();
        new MomentumBreakoutV1(Params(trend: false)).OnCandleClosed(Ctx(Data())).Should().ContainSingle();
    }

    [Fact]
    public void Entry_UsesNoTarget_WhenTrailingStopEnabled()
    {
        var strategy = new MomentumBreakoutV1(Params(trailing: true));

        var signal = strategy.OnCandleClosed(Ctx(Breakout(new TimeOnly(9, 50))))[0];

        signal.StopPrice.Should().Be(99.50m);
        signal.TargetPrice.Should().BeNull();
    }

    [Fact]
    public void MaxTradesPerDay_CapsEntries_AndResetsNextDay()
    {
        var strategy = new MomentumBreakoutV1(Params(maxTrades: 2));
        var day1 = new DateOnly(2026, 1, 15);

        // Three flat breakout candles on day 1: first two enter, third is capped.
        strategy.OnCandleClosed(Ctx(Breakout(new TimeOnly(9, 50), date: day1))).Should().ContainSingle();
        strategy.OnCandleClosed(Ctx(Breakout(new TimeOnly(10, 0), date: day1))).Should().ContainSingle();
        strategy.OnCandleClosed(Ctx(Breakout(new TimeOnly(10, 10), date: day1))).Should().BeEmpty();

        // New trading day resets the tally.
        strategy.OnCandleClosed(Ctx(Breakout(new TimeOnly(9, 50), date: new DateOnly(2026, 1, 16))))
            .Should().ContainSingle();
    }

    [Fact]
    public void Exit_ForcedEndOfDay_AtOrAfterExitTime()
    {
        var strategy = new MomentumBreakoutV1(Params());
        var candles = new List<Candle> { Bar(new TimeOnly(15, 15), high: 101m, close: 100m, vol: 100) };

        var signals = strategy.OnCandleClosed(Ctx(candles, Position(candles[0].TimestampUtc.AddHours(-2))));

        signals.Should().ContainSingle();
        signals[0].Direction.Should().Be(SignalDirection.LongExit);
        signals[0].Notes.Should().Be("EndOfDay");
    }

    [Fact]
    public void Exit_TimeBased_WhenHeldBeyondMaximumMinutes()
    {
        var strategy = new MomentumBreakoutV1(Params());
        var decision = Bar(new TimeOnly(12, 0), high: 101m, close: 100m, vol: 100);
        var openedUtc = decision.TimestampUtc.AddMinutes(-130); // > 120 minute cap

        var signals = strategy.OnCandleClosed(Ctx(new List<Candle> { decision }, Position(openedUtc)));

        signals.Should().ContainSingle();
        signals[0].Direction.Should().Be(SignalDirection.LongExit);
        signals[0].Notes.Should().Be("TimeExit");
    }

    [Fact]
    public void Exit_TrailingStop_WhenCloseFallsBelowTrailedPeak()
    {
        var strategy = new MomentumBreakoutV1(Params(trailing: true));
        var opened = new TimeOnly(11, 0);
        var candles = new List<Candle>
        {
            Bar(opened, high: 110m, close: 108m, vol: 100),                 // peak 110 since entry
            Bar(new TimeOnly(11, 5), high: 106m, close: 100m, vol: 100)     // 100 <= 110 * (1 - 0.5%) = 109.45
        };

        var signals = strategy.OnCandleClosed(Ctx(candles, Position(candles[0].TimestampUtc)));

        signals.Should().ContainSingle();
        signals[0].Notes.Should().Be("TrailingStop");
    }

    [Fact]
    public void Exit_None_WhenHoldingWithinLimitsAndNoTrailing()
    {
        var strategy = new MomentumBreakoutV1(Params());
        var decision = Bar(new TimeOnly(12, 0), high: 101m, close: 100m, vol: 100);

        strategy.OnCandleClosed(Ctx(new List<Candle> { decision }, Position(decision.TimestampUtc.AddMinutes(-10))))
            .Should().BeEmpty();
    }

    // ---- helpers ------------------------------------------------------------

    private static MomentumBreakoutParameters Params(bool trend = false, bool trailing = false, int maxTrades = 3) => new()
    {
        LookbackBars = 3,
        VolumeMultiplier = 1.5m,
        EmaPeriod = 5,
        UseTrendFilter = trend,
        StopLossPercent = 0.5m,
        TargetPercent = 1.0m,
        UseTrailingStop = trailing,
        MaximumHoldingMinutes = 120,
        MaxTradesPerDay = maxTrades,
        EntryStartTime = new TimeOnly(9, 20),
        EntryCutoffTime = new TimeOnly(14, 30),
        ExitTime = new TimeOnly(15, 15)
    };

    private static List<Candle> Breakout(TimeOnly decisionTime, long decisionVol = 200, DateOnly? date = null) => new()
    {
        Bar(decisionTime.AddMinutes(-15), high: 99m, close: 99m, vol: 100, date: date),
        Bar(decisionTime.AddMinutes(-10), high: 99m, close: 99m, vol: 100, date: date),
        Bar(decisionTime.AddMinutes(-5), high: 99m, close: 99m, vol: 100, date: date),
        Bar(decisionTime, high: 100m, close: 100m, vol: decisionVol, date: date)
    };

    private static Candle Bar(TimeOnly ist, decimal high, decimal close, long vol, DateOnly? date = null)
    {
        var day = date ?? new DateOnly(2026, 1, 15);
        var timestamp = new DateTimeOffset(day.Year, day.Month, day.Day, ist.Hour, ist.Minute, 0, TimeSpan.FromHours(5.5))
            .ToUniversalTime();
        return new Candle(Token, "RELIANCE", "NSE", Timeframe.Minute5, timestamp, close, high, close - 5m, close, vol);
    }

    private static StrategyContext Ctx(IReadOnlyList<Candle> candles, OpenPosition? position = null) => new()
    {
        Symbol = "RELIANCE",
        InstrumentToken = Token,
        Timeframe = Timeframe.Minute5,
        Candles = candles,
        OpenPosition = position,
        CurrentTimestampUtc = candles[^1].TimestampUtc,
        AvailableCapital = 100_000m
    };

    private static OpenPosition Position(DateTimeOffset openedAtUtc) =>
        new(Token, "RELIANCE", "MomentumBreakoutV1", 10, 100m, openedAtUtc, 99.50m, 101.00m, "corr-1");
}
