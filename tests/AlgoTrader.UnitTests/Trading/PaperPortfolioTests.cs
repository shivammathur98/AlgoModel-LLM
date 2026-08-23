namespace AlgoTrader.UnitTests.Trading;

using AlgoTrader.Domain.Costing;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Trading;
using FluentAssertions;
using Xunit;

/// <summary>
/// Verifies the in-memory paper portfolio ledger (§8, §10, §18): honest cash accounting, round-trip P&amp;L net
/// of charges, one open lot per instrument, and per-IST-session resetting of the daily counters that feed the
/// risk gates while open positions and cash carry across sessions.
/// </summary>
public sealed class PaperPortfolioTests
{
    // 10:00 IST on 24 Aug 2026 and 25 Aug 2026 (IST = UTC+05:30).
    private static readonly DateTimeOffset Day1 = new(2026, 8, 24, 4, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Day2 = new(2026, 8, 25, 4, 30, 0, TimeSpan.Zero);

    private const decimal StartingCapital = 100_000m;
    private const decimal PerLegCharge = 20m;

    private static PaperPortfolio Build() => new(StartingCapital, new FlatCostCalculator(PerLegCharge));

    private static PaperEntryFill Entry(int token, string symbol, decimal price, int qty, DateTimeOffset at) =>
        new(token, symbol, "NSE", "TestStrategy", ProductType.Intraday, qty, price, at,
            StopPrice: price - 5m, TargetPrice: price + 10m, CorrelationId: $"corr-{token}");

    [Fact]
    public void RecordEntryFill_OpensPositionAndDeductsCashIncludingCharges()
    {
        var portfolio = Build();

        var opened = portfolio.RecordEntryFill(Entry(111, "INFY", price: 100m, qty: 10, Day1));

        opened.Should().BeTrue();
        var position = portfolio.GetOpenPosition(111);
        position.Should().NotBeNull();
        position!.Quantity.Should().Be(10);
        position.AveragePrice.Should().Be(100m);
        position.StopPrice.Should().Be(95m);
        position.TargetPrice.Should().Be(110m);

        var snapshot = portfolio.Snapshot(Day1);
        snapshot.Cash.Should().Be(StartingCapital - (100m * 10 + PerLegCharge)); // 98_980
        snapshot.TradesToday.Should().Be(1);
        snapshot.OpenPositions.Should().HaveCount(1);
        snapshot.SymbolsWithOpenPositions.Should().Contain("INFY");
        snapshot.RealizedPnlToday.Should().Be(0m);
    }

    [Fact]
    public void RecordExitFill_ClosesPositionAndRealizesPnlNetOfRoundTripCharges()
    {
        var portfolio = Build();
        portfolio.RecordEntryFill(Entry(111, "INFY", price: 100m, qty: 10, Day1));

        var closed = portfolio.RecordExitFill(111, fillPrice: 110m, Day1.AddMinutes(30));

        closed.Should().BeTrue();
        portfolio.GetOpenPosition(111).Should().BeNull();

        var snapshot = portfolio.Snapshot(Day1);
        // Gross (110-100)*10 = 100, minus 20 entry + 20 exit charges = 60 net.
        snapshot.RealizedPnlToday.Should().Be(60m);
        snapshot.Cash.Should().Be(StartingCapital + 60m);
        snapshot.OpenPositions.Should().BeEmpty();
        snapshot.SymbolsWithOpenPositions.Should().BeEmpty();
    }

    [Fact]
    public void RecordEntryFill_WhenInstrumentAlreadyOpen_ReturnsFalseAndDoesNotDoubleCount()
    {
        var portfolio = Build();
        portfolio.RecordEntryFill(Entry(111, "INFY", price: 100m, qty: 10, Day1));
        var cashAfterFirst = portfolio.Snapshot(Day1).Cash;

        var second = portfolio.RecordEntryFill(Entry(111, "INFY", price: 105m, qty: 5, Day1.AddMinutes(1)));

        second.Should().BeFalse();
        var snapshot = portfolio.Snapshot(Day1);
        snapshot.TradesToday.Should().Be(1);
        snapshot.Cash.Should().Be(cashAfterFirst);
        portfolio.GetOpenPosition(111)!.Quantity.Should().Be(10); // unchanged
    }

    [Fact]
    public void RecordExitFill_WhenFlat_ReturnsFalseAndLeavesLedgerUnchanged()
    {
        var portfolio = Build();

        var closed = portfolio.RecordExitFill(999, fillPrice: 50m, Day1);

        closed.Should().BeFalse();
        var snapshot = portfolio.Snapshot(Day1);
        snapshot.Cash.Should().Be(StartingCapital);
        snapshot.RealizedPnlToday.Should().Be(0m);
    }

    [Fact]
    public void SymbolsWithOpenPositions_ReflectsAllConcurrentPositions()
    {
        var portfolio = Build();
        portfolio.RecordEntryFill(Entry(111, "INFY", price: 100m, qty: 10, Day1));
        portfolio.RecordEntryFill(Entry(222, "TCS", price: 200m, qty: 5, Day1));

        var snapshot = portfolio.Snapshot(Day1);

        snapshot.OpenPositions.Should().HaveCount(2);
        snapshot.SymbolsWithOpenPositions.Should().BeEquivalentTo(new[] { "INFY", "TCS" });
    }

    [Fact]
    public void Snapshot_OnNewIstSession_ResetsDailyCountersButKeepsCash()
    {
        var portfolio = Build();
        portfolio.RecordEntryFill(Entry(111, "INFY", price: 100m, qty: 10, Day1));
        portfolio.RecordExitFill(111, fillPrice: 110m, Day1.AddMinutes(30));

        var day1 = portfolio.Snapshot(Day1);
        day1.RealizedPnlToday.Should().Be(60m);
        day1.TradesToday.Should().Be(1);

        var day2 = portfolio.Snapshot(Day2);
        day2.RealizedPnlToday.Should().Be(0m);
        day2.TradesToday.Should().Be(0);
        day2.Cash.Should().Be(StartingCapital + 60m); // cash carries across sessions
    }

    [Fact]
    public void OpenPosition_CarriesAcrossSessions_WhileTradeCounterResets()
    {
        var portfolio = Build();
        portfolio.RecordEntryFill(Entry(222, "TCS", price: 200m, qty: 5, Day1));

        var day2 = portfolio.Snapshot(Day2);

        day2.TradesToday.Should().Be(0);                 // reset for the new session
        day2.OpenPositions.Should().HaveCount(1);        // overnight position still open
        portfolio.GetOpenPosition(222).Should().NotBeNull();
    }

    [Fact]
    public void RecordExitFill_OnLaterSession_RealizesPnlAgainstTheNewSession()
    {
        var portfolio = Build();
        portfolio.RecordEntryFill(Entry(222, "TCS", price: 200m, qty: 5, Day1));

        portfolio.RecordExitFill(222, fillPrice: 210m, Day2.AddMinutes(30));

        var snapshot = portfolio.Snapshot(Day2);
        // (210-200)*5 = 50 gross, minus 40 round-trip charges = 10; counted in Day2, not Day1.
        snapshot.RealizedPnlToday.Should().Be(10m);
        snapshot.TradesToday.Should().Be(0); // the entry was counted in Day1, then reset
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(-5, 100)]
    [InlineData(10, 0)]
    [InlineData(10, -1)]
    public void RecordEntryFill_WithNonPositiveQuantityOrPrice_Throws(int quantity, decimal price)
    {
        var portfolio = Build();

        var act = () => portfolio.RecordEntryFill(Entry(111, "INFY", price, quantity, Day1));

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void RecordExitFill_WithNonPositivePrice_Throws()
    {
        var portfolio = Build();

        var act = () => portfolio.RecordExitFill(111, fillPrice: 0m, Day1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    /// <summary>Deterministic cost model: a flat brokerage per leg, no other charges — keeps P&amp;L math exact.</summary>
    private sealed class FlatCostCalculator : ITradingCostCalculator
    {
        private readonly decimal _perLeg;

        public FlatCostCalculator(decimal perLeg) => _perLeg = perLeg;

        public TradingCostBreakdown Calculate(CostCalculationContext context) =>
            new(Brokerage: _perLeg, Stt: 0m, ExchangeTransactionCharges: 0m, SebiCharges: 0m,
                StampDuty: 0m, Gst: 0m, DpCharges: 0m, OtherCharges: 0m);
    }
}
