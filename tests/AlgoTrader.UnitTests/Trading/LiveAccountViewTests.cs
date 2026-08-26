namespace AlgoTrader.UnitTests.Trading;

using AlgoTrader.Application.Repositories;
using AlgoTrader.Domain.Broker;
using AlgoTrader.Domain.Costing;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Orders;
using AlgoTrader.Trading;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Verifies the live account view (§8, §11, §26): broker truth (positions, funds) is the source of what is held
/// and the buying power available, while the local order store supplies the provenance the broker cannot carry
/// (open time, strategy, correlation id). It reports only long positions in the traded product, never fabricates
/// state, and flags positions it cannot attribute to a local entry order.
/// </summary>
public sealed class LiveAccountViewTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 24, 6, 0, 0, TimeSpan.Zero);

    private static LiveAccountView View(ITradingCostCalculator? costs = null) =>
        new(costs ?? new FakeCostCalculator(), NullLogger<LiveAccountView>.Instance);
    private static BrokerFunds Funds(decimal cash) => new(cash, UsedMargin: 0m, AvailableMargin: cash);

    [Fact]
    public async Task LongPosition_TakesSizeAndPriceFromBroker_AndProvenanceFromLocalEntryOrder()
    {
        var opened = T0.AddHours(-2);
        var broker = new FakeBroker(Funds(50_000m), LongPos(111, "INFY", 100, 250m));
        var orders = new FakeOrderRepository(FilledBuy(111, "INFY", 100, "MomentumBreakoutV1", "corr-1", opened));

        var snapshot = await View().CaptureAsync(broker, orders, ProductType.Intraday, T0);

        snapshot.AvailableCash.Should().Be(50_000m);
        var position = snapshot.GetOpenPosition(111);
        position.Should().NotBeNull();
        position!.Quantity.Should().Be(100);              // broker truth
        position.AveragePrice.Should().Be(250m);          // broker truth
        position.OpenedAtUtc.Should().Be(opened);         // local provenance
        position.StrategyName.Should().Be("MomentumBreakoutV1");
        position.CorrelationId.Should().Be("corr-1");
        position.StopPrice.Should().BeNull();             // strategies read neither off the open position
        position.TargetPrice.Should().BeNull();
    }

    [Fact]
    public async Task OpenOrders_SurfaceAsInFlightTokens_FilledOrdersDoNot()
    {
        var broker = new FakeBroker(Funds(50_000m)); // flat
        var orders = new FakeOrderRepository(
            Working(222, "TCS"),                                     // non-terminal → in flight
            FilledBuy(111, "INFY", 100, "S", "corr", T0.AddHours(-1))); // terminal → not in flight

        var snapshot = await View().CaptureAsync(broker, orders, ProductType.Intraday, T0);

        snapshot.HasInFlightOrder(222).Should().BeTrue();
        snapshot.HasInFlightOrder(111).Should().BeFalse();
        snapshot.InFlightOrderTokens.Should().BeEquivalentTo(new[] { 222 });
    }

    [Fact]
    public async Task PositionInADifferentProduct_IsIgnored()
    {
        var broker = new FakeBroker(Funds(50_000m), LongPos(111, "INFY", 100, 250m, ProductType.Delivery));
        var orders = new FakeOrderRepository();

        var snapshot = await View().CaptureAsync(broker, orders, ProductType.Intraday, T0);

        snapshot.GetOpenPosition(111).Should().BeNull();
        snapshot.OpenPositions.Should().BeEmpty();
    }

    [Fact]
    public async Task NonLongPosition_IsIgnored()
    {
        // The broker reports Quantity as an unsigned magnitude with direction in Side (see ZerodhaKiteBroker:
        // Math.Abs(qty) + side = qty>0?Buy:Sell). A real short therefore arrives as Side=Sell with a POSITIVE
        // quantity — it must be ignored, not mapped as a phantom long the long-only cycle would then "exit" with
        // a real SELL that deepens the short.
        var broker = new FakeBroker(Funds(50_000m),
            new BrokerPositionSummary("INFY", 111, ProductType.Intraday, OrderSide.Sell, 50, 250m, 0m));
        var orders = new FakeOrderRepository();

        var snapshot = await View().CaptureAsync(broker, orders, ProductType.Intraday, T0);

        snapshot.OpenPositions.Should().BeEmpty();
        snapshot.GetOpenPosition(111).Should().BeNull();
    }

    [Fact]
    public async Task PositionWithoutLocalEntryOrder_IsReportedWithUnknownProvenance()
    {
        var broker = new FakeBroker(Funds(50_000m), LongPos(111, "INFY", 100, 250m));
        var orders = new FakeOrderRepository(); // nothing local

        var snapshot = await View().CaptureAsync(broker, orders, ProductType.Intraday, T0);

        var position = snapshot.GetOpenPosition(111);
        position.Should().NotBeNull();                    // still non-flat, so the cycle won't re-enter
        position!.Quantity.Should().Be(100);
        position.OpenedAtUtc.Should().Be(T0);             // dated at observation → no spurious time-exit
        position.StrategyName.Should().BeEmpty();
        position.CorrelationId.Should().BeEmpty();
    }

    [Fact]
    public async Task Provenance_UsesTheLatestFilledBuyForTheInstrument()
    {
        var older = T0.AddHours(-3);
        var newer = T0.AddHours(-1);
        var broker = new FakeBroker(Funds(50_000m), LongPos(111, "INFY", 100, 250m));
        var orders = new FakeOrderRepository(
            FilledBuy(111, "INFY", 50, "Old", "corr-old", older),
            FilledBuy(111, "INFY", 100, "New", "corr-new", newer));

        var snapshot = await View().CaptureAsync(broker, orders, ProductType.Intraday, T0);

        var position = snapshot.GetOpenPosition(111)!;
        position.OpenedAtUtc.Should().Be(newer);
        position.CorrelationId.Should().Be("corr-new");
    }

    [Fact]
    public async Task SymbolsWithOpenPositions_ReflectsEveryHeldSymbol()
    {
        var broker = new FakeBroker(Funds(50_000m),
            LongPos(111, "INFY", 100, 250m), LongPos(222, "TCS", 10, 3800m));
        var orders = new FakeOrderRepository();

        var snapshot = await View().CaptureAsync(broker, orders, ProductType.Intraday, T0);

        snapshot.OpenPositions.Should().HaveCount(2);
        snapshot.SymbolsWithOpenPositions.Should().BeEquivalentTo(new[] { "INFY", "TCS" });
    }

    // ---- Session-day risk figures (realized P&L, trade count) -------------

    [Fact]
    public async Task DayFigures_AreZero_WhenThereAreNoFillsToday()
    {
        var broker = new FakeBroker(Funds(50_000m));
        var orders = new FakeOrderRepository(Working(222, "TCS")); // a working order is not a fill

        var snapshot = await View().CaptureAsync(broker, orders, ProductType.Intraday, T0);

        snapshot.RealizedPnlToday.Should().Be(0m);
        snapshot.TradesToday.Should().Be(0);
    }

    [Fact]
    public async Task TradesToday_CountsFilledEntriesForTheSession()
    {
        var broker = new FakeBroker(Funds(50_000m));
        var orders = new FakeOrderRepository(
            Fill(111, OrderSide.Buy, 100, 250m, T0.AddHours(-2)),
            Fill(222, OrderSide.Buy, 10, 3_800m, T0.AddHours(-1)));

        var snapshot = await View().CaptureAsync(broker, orders, ProductType.Intraday, T0);

        snapshot.TradesToday.Should().Be(2);       // two filled entries
        snapshot.RealizedPnlToday.Should().Be(0m); // neither has been closed yet
    }

    [Fact]
    public async Task RealizedPnlToday_AccruesNetRoundTripPnl_AfterCharges()
    {
        // Buy 100 @100, sell 100 @110 → gross 1,000; with a flat 2/share on each leg, charges are 400 → net 600.
        var costs = new FakeCostCalculator { PerShare = 2m };
        var broker = new FakeBroker(Funds(50_000m));
        var orders = new FakeOrderRepository(
            Fill(111, OrderSide.Buy, 100, 100m, T0.AddMinutes(-10)),
            Fill(111, OrderSide.Sell, 100, 110m, T0.AddMinutes(-1)));

        var snapshot = await View(costs).CaptureAsync(broker, orders, ProductType.Intraday, T0);

        snapshot.RealizedPnlToday.Should().Be(600m);
        snapshot.TradesToday.Should().Be(1); // the entry; the exit closes it but is not itself a new trade
    }

    [Fact]
    public async Task RealizedPnlToday_OnPartialExit_ValuesOnlyTheFilledQuantity()
    {
        // Buy 100 @100 (entry charge 200 @2/share), then sell only 40 @110. Realized is over the 40 filled:
        // gross (110-100)*40 = 400; apportioned entry charge 200*40/100 = 80; exit charge 40*2 = 80 → net 240.
        var costs = new FakeCostCalculator { PerShare = 2m };
        var broker = new FakeBroker(Funds(50_000m));
        var orders = new FakeOrderRepository(
            Fill(111, OrderSide.Buy, 100, 100m, T0.AddMinutes(-10)),
            Fill(111, OrderSide.Sell, 40, 110m, T0.AddMinutes(-1)));

        var snapshot = await View(costs).CaptureAsync(broker, orders, ProductType.Intraday, T0);

        snapshot.RealizedPnlToday.Should().Be(240m);
        snapshot.TradesToday.Should().Be(1);
    }

    [Fact]
    public async Task FillsFromAPriorSession_AreExcludedFromTodaysFigures()
    {
        // A completed round-trip yesterday (IST) must not bleed into today's daily-loss / trades-per-day figures.
        var broker = new FakeBroker(Funds(50_000m));
        var orders = new FakeOrderRepository(
            Fill(111, OrderSide.Buy, 100, 100m, T0.AddDays(-1).AddMinutes(-10)),
            Fill(111, OrderSide.Sell, 100, 90m, T0.AddDays(-1).AddMinutes(-1)));

        var snapshot = await View().CaptureAsync(broker, orders, ProductType.Intraday, T0);

        snapshot.RealizedPnlToday.Should().Be(0m);
        snapshot.TradesToday.Should().Be(0);
    }

    [Fact]
    public async Task ASellWithoutASameDayEntry_IsExcludedFromRealizedPnl()
    {
        // An overnight position squared off today has no same-day entry to value against; it is left out rather
        // than guessed at, and is not counted as a trade (only entries are).
        var broker = new FakeBroker(Funds(50_000m));
        var orders = new FakeOrderRepository(Fill(111, OrderSide.Sell, 100, 110m, T0.AddMinutes(-1)));

        var snapshot = await View().CaptureAsync(broker, orders, ProductType.Intraday, T0);

        snapshot.RealizedPnlToday.Should().Be(0m);
        snapshot.TradesToday.Should().Be(0);
    }

    [Fact]
    public async Task DayFigures_IgnoreFillsInADifferentProduct()
    {
        // The cycle trades one product; a delivery round-trip must not affect the intraday session figures.
        var broker = new FakeBroker(Funds(50_000m));
        var orders = new FakeOrderRepository(
            Fill(111, OrderSide.Buy, 100, 100m, T0.AddMinutes(-10), ProductType.Delivery),
            Fill(111, OrderSide.Sell, 100, 130m, T0.AddMinutes(-1), ProductType.Delivery));

        var snapshot = await View().CaptureAsync(broker, orders, ProductType.Intraday, T0);

        snapshot.RealizedPnlToday.Should().Be(0m);
        snapshot.TradesToday.Should().Be(0);
    }

    // ---- Helpers ----------------------------------------------------------

    private static BrokerPositionSummary LongPos(
        int token, string symbol, int qty, decimal avg, ProductType product = ProductType.Intraday) =>
        new(symbol, token, product, OrderSide.Buy, qty, avg, UnrealizedPnl: 0m);

    private static Order FilledBuy(int token, string symbol, int qty, string strategy, string correlationId, DateTimeOffset filledAt) =>
        new()
        {
            InstrumentToken = token,
            Symbol = symbol,
            Exchange = "NSE",
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Product = ProductType.Intraday,
            Quantity = qty,
            FilledQuantity = qty,
            AverageFillPrice = 250m,
            State = OrderState.Filled,
            StrategyName = strategy,
            CorrelationId = correlationId,
            CreatedAtUtc = filledAt,
            FilledAtUtc = filledAt,
            LastUpdatedAtUtc = filledAt
        };

    private static Order Working(int token, string symbol) =>
        new()
        {
            InstrumentToken = token,
            Symbol = symbol,
            Exchange = "NSE",
            Side = OrderSide.Buy,
            Type = OrderType.Market,
            Product = ProductType.Intraday,
            Quantity = 10,
            State = OrderState.Open,
            CorrelationId = "working",
            CreatedAtUtc = T0.AddMinutes(-1),
            LastUpdatedAtUtc = T0.AddMinutes(-1)
        };

    /// <summary>A filled order leg used to exercise the session-day realized-P&amp;L and trade-count computation.</summary>
    private static Order Fill(
        int token, OrderSide side, int qty, decimal price, DateTimeOffset filledAt, ProductType product = ProductType.Intraday) =>
        new()
        {
            InstrumentToken = token,
            Symbol = "SYM",
            Exchange = "NSE",
            Side = side,
            Type = OrderType.Market,
            Product = product,
            Quantity = qty,
            FilledQuantity = qty,
            AverageFillPrice = price,
            State = OrderState.Filled,
            CorrelationId = "c",
            CreatedAtUtc = filledAt,
            FilledAtUtc = filledAt,
            LastUpdatedAtUtc = filledAt
        };

    /// <summary>A predictable cost model for the P&amp;L tests: a flat per-share charge on each leg (default free).</summary>
    private sealed class FakeCostCalculator : ITradingCostCalculator
    {
        public decimal PerShare { get; set; }

        public TradingCostBreakdown Calculate(CostCalculationContext context) =>
            new(context.Quantity * PerShare, Stt: 0m, ExchangeTransactionCharges: 0m, SebiCharges: 0m,
                StampDuty: 0m, Gst: 0m, DpCharges: 0m, OtherCharges: 0m);
    }

    private sealed class FakeBroker : ITradingBroker
    {
        private readonly BrokerFunds _funds;
        private readonly IReadOnlyList<BrokerPositionSummary> _positions;

        public FakeBroker(BrokerFunds funds, params BrokerPositionSummary[] positions)
        {
            _funds = funds;
            _positions = positions;
        }

        public string ProviderName => "FakeBroker";
        public bool IsAuthenticated => true;

        public Task<BrokerFunds> GetFundsAsync(CancellationToken cancellationToken = default) => Task.FromResult(_funds);
        public Task<IReadOnlyList<BrokerPositionSummary>> GetPositionsAsync(CancellationToken cancellationToken = default) => Task.FromResult(_positions);

        public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<BrokerProfile> GetProfileAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BrokerHolding>> GetHoldingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BrokerOrderInfo>> GetOrdersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrokerOrderInfo> GetOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PlaceOrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ModifyOrderResult> ModifyOrderAsync(string brokerOrderId, OrderModification modification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

#pragma warning disable CS0067 // Not raised in these tests.
        public event EventHandler<BrokerOrderUpdate>? OrderUpdated;
        public event EventHandler<EventArgs>? StreamDisconnected;
        public bool IsConnected => true;

#pragma warning restore CS0067
    }

    private sealed class FakeOrderRepository : IOrderRepository
    {
        private readonly List<Order> _orders;

        public FakeOrderRepository(params Order[] orders) => _orders = orders.ToList();

        public Task<IReadOnlyList<Order>> GetOpenOrdersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Order>>(_orders.Where(o => !o.State.IsTerminal()).ToList());

        public Task<IReadOnlyList<Order>> GetOrdersAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Order>>(_orders.Where(o => o.CreatedAtUtc >= fromUtc && o.CreatedAtUtc <= toUtc).ToList());

        public Task<Order> AddAsync(Order order, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateAsync(Order order, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Order?> GetByIdAsync(long id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Order?> GetByBrokerIdAsync(string brokerOrderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Order>> GetByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

