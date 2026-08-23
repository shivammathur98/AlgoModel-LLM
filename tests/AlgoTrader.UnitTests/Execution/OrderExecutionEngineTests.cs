namespace AlgoTrader.UnitTests.Execution;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Application.Repositories;
using AlgoTrader.Application.Safety;
using AlgoTrader.Domain.Broker;
using AlgoTrader.Domain.Common;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Execution;
using AlgoTrader.Domain.Orders;
using AlgoTrader.Execution;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Verifies the execution engine (§8, §11, §25). The load-bearing property is safety: a real broker order
/// is transmitted only when the platform is fully gated for Live (§6, §36); every other mode simulates or
/// refuses and must never call the broker.
/// </summary>
public sealed class OrderExecutionEngineTests
{
    // ---- Simulated modes --------------------------------------------------

    [Fact]
    public async Task PaperLimitOrder_SimulatesFillAtLimitPrice_WithoutBroker()
    {
        var broker = new FakeBroker();
        var repo = new InMemoryOrderRepository();
        var engine = Engine(Paper(), broker, repo);

        var result = await engine.SubmitAsync(LimitOrder(quantity: 10, price: 250.50m));

        result.IsAccepted.Should().BeTrue();
        result.State.Should().Be(OrderState.Filled);
        result.OrderId.Should().BeGreaterThan(0);
        broker.PlaceOrderCallCount.Should().Be(0);

        var stored = await repo.GetByIdAsync(result.OrderId);
        stored!.State.Should().Be(OrderState.Filled);
        stored.FilledQuantity.Should().Be(10);
        stored.AverageFillPrice.Should().Be(250.50m);
        stored.FilledAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task PaperMarketOrder_RestsAsOpen_WithoutFabricatingPrice()
    {
        var broker = new FakeBroker();
        var engine = Engine(Paper(), broker, new InMemoryOrderRepository());

        var result = await engine.SubmitAsync(MarketOrder(quantity: 10));

        result.IsAccepted.Should().BeTrue();
        result.State.Should().Be(OrderState.Open);
        broker.PlaceOrderCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ResearchMode_RefusesAllExecution()
    {
        var broker = new FakeBroker();
        var repo = new InMemoryOrderRepository();
        var engine = Engine(Mode(TradingMode.Research), broker, repo);

        var result = await engine.SubmitAsync(LimitOrder(quantity: 10, price: 100m));

        result.IsAccepted.Should().BeFalse();
        result.State.Should().Be(OrderState.Rejected);
        result.Message.Should().Contain("Research");
        broker.PlaceOrderCallCount.Should().Be(0);

        // Even a refused order is persisted for audit (§28).
        var stored = await repo.GetByIdAsync(result.OrderId);
        stored!.State.Should().Be(OrderState.Rejected);
    }

    // ---- Live safety gate (§6, §36) — the critical invariant --------------

    [Fact]
    public async Task LiveMode_WithGatesUnsatisfied_RejectsAndNeverTouchesBroker()
    {
        // Mode is Live but the acknowledgement + enable flags are missing: no real order may be sent.
        var broker = new FakeBroker();
        var repo = new InMemoryOrderRepository();
        var settings = new TradingSettings { Mode = TradingMode.Live, EnableLiveTrading = false, LiveTradingAcknowledgement = "" };
        var engine = Engine(settings, broker, repo);

        var result = await engine.SubmitAsync(LimitOrder(quantity: 10, price: 100m));

        result.IsAccepted.Should().BeFalse();
        result.State.Should().Be(OrderState.Rejected);
        broker.PlaceOrderCallCount.Should().Be(0);

        var stored = await repo.GetByIdAsync(result.OrderId);
        stored!.RejectionReason.Should().Contain("EnableLiveTrading");
    }

    [Fact]
    public async Task LiveMode_FullyGated_TransmitsToBroker()
    {
        var broker = new FakeBroker { PlaceResult = new PlaceOrderResult(true, BrokerOrderId: "KITE-777") };
        var repo = new InMemoryOrderRepository();
        var engine = Engine(FullyGatedLive(), broker, repo);

        var result = await engine.SubmitAsync(LimitOrder(quantity: 10, price: 100m));

        result.IsAccepted.Should().BeTrue();
        result.State.Should().Be(OrderState.Submitted);
        result.BrokerOrderId.Should().Be("KITE-777");
        broker.PlaceOrderCallCount.Should().Be(1);

        var stored = await repo.GetByIdAsync(result.OrderId);
        stored!.BrokerOrderId.Should().Be("KITE-777");
        stored.State.Should().Be(OrderState.Submitted);
    }

    [Fact]
    public async Task LiveMode_BrokerBusinessRejection_MarksOrderRejected()
    {
        var broker = new FakeBroker { PlaceResult = new PlaceOrderResult(false, ErrorMessage: "Insufficient margin") };
        var engine = Engine(FullyGatedLive(), broker, new InMemoryOrderRepository());

        var result = await engine.SubmitAsync(LimitOrder(quantity: 10, price: 100m));

        result.IsAccepted.Should().BeFalse();
        result.State.Should().Be(OrderState.Rejected);
        result.Message.Should().Contain("Insufficient margin");
        broker.PlaceOrderCallCount.Should().Be(1);
    }

    // ---- Cancellation -----------------------------------------------------

    [Fact]
    public async Task Cancel_SimulatedRestingOrder_TransitionsToCancelled()
    {
        var broker = new FakeBroker();
        var repo = new InMemoryOrderRepository();
        var engine = Engine(Paper(), broker, repo);
        var open = await engine.SubmitAsync(MarketOrder(quantity: 10)); // rests as Open

        var result = await engine.CancelAsync(open.OrderId);

        result.IsAccepted.Should().BeTrue();
        result.State.Should().Be(OrderState.Cancelled);
        broker.CancelCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Cancel_LiveBrokerOrder_RequestsBrokerCancelAndMarksCancelPending()
    {
        var broker = new FakeBroker { PlaceResult = new PlaceOrderResult(true, BrokerOrderId: "KITE-1") };
        var repo = new InMemoryOrderRepository();
        var engine = Engine(FullyGatedLive(), broker, repo);
        var live = await engine.SubmitAsync(LimitOrder(quantity: 10, price: 100m)); // Submitted with broker id

        var result = await engine.CancelAsync(live.OrderId);

        result.IsAccepted.Should().BeTrue();
        result.State.Should().Be(OrderState.CancelPending);
        broker.CancelCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Cancel_UnknownOrder_IsNoOp()
    {
        var engine = Engine(Paper(), new FakeBroker(), new InMemoryOrderRepository());

        var result = await engine.CancelAsync(orderId: 999);

        result.IsAccepted.Should().BeFalse();
        result.Message.Should().Contain("not found");
    }

    // ---- Async broker reconciliation (§25, §26) ---------------------------

    [Fact]
    public async Task ApplyBrokerUpdate_Fill_MarksLiveOrderFilled()
    {
        var (engine, repo, live) = await SubmittedLiveOrder("KITE-9");

        var result = await engine.ApplyBrokerUpdateAsync(Update("KITE-9", OrderState.Filled, filled: 10, avg: 100.25m));

        result.IsAccepted.Should().BeTrue();
        result.State.Should().Be(OrderState.Filled);
        var stored = await repo.GetByIdAsync(live.OrderId);
        stored!.FilledQuantity.Should().Be(10);
        stored.AverageFillPrice.Should().Be(100.25m);
        stored.FilledAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task ApplyBrokerUpdate_PartialThenFull_TracksFillProgress()
    {
        var (engine, repo, live) = await SubmittedLiveOrder("KITE-2");

        var partial = await engine.ApplyBrokerUpdateAsync(Update("KITE-2", OrderState.PartiallyFilled, filled: 4, avg: 100m));
        partial.State.Should().Be(OrderState.PartiallyFilled);
        (await repo.GetByIdAsync(live.OrderId))!.FilledQuantity.Should().Be(4);

        var full = await engine.ApplyBrokerUpdateAsync(Update("KITE-2", OrderState.Filled, filled: 10, avg: 100.5m));
        full.State.Should().Be(OrderState.Filled);
        var stored = await repo.GetByIdAsync(live.OrderId);
        stored!.FilledQuantity.Should().Be(10);
        stored.AverageFillPrice.Should().Be(100.5m);
    }

    [Fact]
    public async Task ApplyBrokerUpdate_ForUntrackedBrokerId_IsIgnored()
    {
        var engine = Engine(Paper(), new FakeBroker(), new InMemoryOrderRepository());

        var result = await engine.ApplyBrokerUpdateAsync(Update("UNKNOWN", OrderState.Filled, filled: 1, avg: 10m));

        result.IsAccepted.Should().BeFalse();
    }

    [Fact]
    public async Task ApplyBrokerUpdate_DuplicateTerminal_IsIgnoredIdempotently()
    {
        var (engine, repo, live) = await SubmittedLiveOrder("KITE-3");
        await engine.ApplyBrokerUpdateAsync(Update("KITE-3", OrderState.Filled, filled: 10, avg: 100m));

        // A second Filled update cannot transition Filled->Filled: ignored, state unchanged.
        var again = await engine.ApplyBrokerUpdateAsync(Update("KITE-3", OrderState.Filled, filled: 10, avg: 100m));

        again.IsAccepted.Should().BeFalse();
        (await repo.GetByIdAsync(live.OrderId))!.State.Should().Be(OrderState.Filled);
    }

    [Fact]
    public async Task ApplyBrokerUpdate_CancelConfirmation_CompletesCancellation()
    {
        var broker = new FakeBroker { PlaceResult = new PlaceOrderResult(true, BrokerOrderId: "KITE-4") };
        var repo = new InMemoryOrderRepository();
        var engine = Engine(FullyGatedLive(), broker, repo);
        var live = await engine.SubmitAsync(LimitOrder(quantity: 10, price: 100m));
        await engine.CancelAsync(live.OrderId); // -> CancelPending

        var result = await engine.ApplyBrokerUpdateAsync(Update("KITE-4", OrderState.Cancelled, filled: 0, avg: null));

        result.State.Should().Be(OrderState.Cancelled);
    }

    [Fact]
    public async Task ApplyBrokerUpdate_Rejection_RecordsReason()
    {
        var (engine, repo, live) = await SubmittedLiveOrder("KITE-5");

        await engine.ApplyBrokerUpdateAsync(new BrokerOrderUpdate("KITE-5", OrderState.Rejected, 0, null, Clock, "RMS: limit breached"));

        var stored = await repo.GetByIdAsync(live.OrderId);
        stored!.State.Should().Be(OrderState.Rejected);
        stored.RejectionReason.Should().Contain("RMS");
    }

    // ---- Price-fed paper fill --------------------------------------------

    [Fact]
    public async Task ApplyPaperFill_FillsRestingMarketOrder_AtObservedPrice()
    {
        var repo = new InMemoryOrderRepository();
        var engine = Engine(Paper(), new FakeBroker(), repo);
        var resting = await engine.SubmitAsync(MarketOrder(quantity: 10)); // Open

        var result = await engine.ApplyPaperFillAsync(resting.OrderId, fillPrice: 251.25m);

        result.IsAccepted.Should().BeTrue();
        result.State.Should().Be(OrderState.Filled);
        var stored = await repo.GetByIdAsync(resting.OrderId);
        stored!.FilledQuantity.Should().Be(10);
        stored.AverageFillPrice.Should().Be(251.25m);
    }

    [Fact]
    public async Task ApplyPaperFill_OnNonRestingOrder_IsNoOp()
    {
        var engine = Engine(Paper(), new FakeBroker(), new InMemoryOrderRepository());
        var filled = await engine.SubmitAsync(LimitOrder(quantity: 10, price: 100m)); // already Filled

        var result = await engine.ApplyPaperFillAsync(filled.OrderId, fillPrice: 105m);

        result.IsAccepted.Should().BeFalse();
    }

    [Fact]
    public async Task ApplyPaperFill_UnknownOrder_IsNotFound()
    {
        var engine = Engine(Paper(), new FakeBroker(), new InMemoryOrderRepository());

        var result = await engine.ApplyPaperFillAsync(orderId: 404, fillPrice: 100m);

        result.IsAccepted.Should().BeFalse();
        result.Message.Should().Contain("not found");
    }

    [Fact]
    public async Task ApplyPaperFill_WithNonPositivePrice_Throws()
    {
        var engine = Engine(Paper(), new FakeBroker(), new InMemoryOrderRepository());
        var resting = await engine.SubmitAsync(MarketOrder(quantity: 10));

        var act = async () => await engine.ApplyPaperFillAsync(resting.OrderId, fillPrice: 0m);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    // ---- Guards -----------------------------------------------------------

    [Fact]
    public async Task Submit_WithNonPositiveQuantity_Throws()
    {
        var engine = Engine(Paper(), new FakeBroker(), new InMemoryOrderRepository());
        var order = new OrderRequest(738561, "INFY", "NSE", OrderSide.Buy, OrderType.Limit, 0, ProductType.Delivery, Price: 100m);

        var act = async () => await engine.SubmitAsync(order);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    // ---- Helpers ----------------------------------------------------------

    private static readonly DateTimeOffset Clock = new(2026, 8, 24, 4, 30, 0, TimeSpan.Zero);

    private static OrderExecutionEngine Engine(TradingSettings settings, FakeBroker broker, InMemoryOrderRepository repo) =>
        new(settings, broker, new LiveTradingSafetyValidator(), repo,
            new FixedClock(Clock), NullLogger<OrderExecutionEngine>.Instance);

    /// <summary>Submits a fully-gated live limit order that the broker accepts, leaving it Submitted with the given broker id.</summary>
    private static async Task<(OrderExecutionEngine Engine, InMemoryOrderRepository Repo, ExecutionResult Order)> SubmittedLiveOrder(string brokerOrderId)
    {
        var broker = new FakeBroker { PlaceResult = new PlaceOrderResult(true, BrokerOrderId: brokerOrderId) };
        var repo = new InMemoryOrderRepository();
        var engine = Engine(FullyGatedLive(), broker, repo);
        var order = await engine.SubmitAsync(LimitOrder(quantity: 10, price: 100m));
        return (engine, repo, order);
    }

    private static BrokerOrderUpdate Update(string brokerOrderId, OrderState state, int filled, decimal? avg) =>
        new(brokerOrderId, state, filled, avg, Clock);


    private static TradingSettings Paper() => new() { Mode = TradingMode.Paper };
    private static TradingSettings Mode(TradingMode mode) => new() { Mode = mode };

    private static TradingSettings FullyGatedLive() => new()
    {
        Mode = TradingMode.Live,
        EnableLiveTrading = true,
        LiveTradingAcknowledgement = TradingSettings.RequiredLiveAcknowledgement
    };

    private static OrderRequest LimitOrder(int quantity, decimal price) =>
        new(738561, "INFY", "NSE", OrderSide.Buy, OrderType.Limit, quantity, ProductType.Delivery, Price: price);

    private static OrderRequest MarketOrder(int quantity) =>
        new(738561, "INFY", "NSE", OrderSide.Buy, OrderType.Market, quantity, ProductType.Delivery);

    private sealed class FixedClock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    /// <summary>Records broker interaction so tests can assert the safety gate never leaks a real order.</summary>
    private sealed class FakeBroker : ITradingBroker
    {
        public PlaceOrderResult PlaceResult { get; set; } = new(true, BrokerOrderId: "BROKER-1");
        public int PlaceOrderCallCount { get; private set; }
        public int CancelCallCount { get; private set; }

        public string ProviderName => "Fake";
        public bool IsAuthenticated => true;

        public Task<PlaceOrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default)
        {
            PlaceOrderCallCount++;
            return Task.FromResult(PlaceResult);
        }

        public Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default)
        {
            CancelCallCount++;
            return Task.CompletedTask;
        }

        public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<BrokerProfile> GetProfileAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrokerFunds> GetFundsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BrokerHolding>> GetHoldingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BrokerPositionSummary>> GetPositionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BrokerOrderInfo>> GetOrdersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrokerOrderInfo> GetOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ModifyOrderResult> ModifyOrderAsync(string brokerOrderId, OrderModification modification, CancellationToken cancellationToken = default) => throw new NotSupportedException();

#pragma warning disable CS0067 // Not raised in these tests; async fill reconciliation is a separate concern.
        public event EventHandler<BrokerOrderUpdate>? OrderUpdated;
#pragma warning restore CS0067
    }

    /// <summary>Minimal in-memory order store: assigns ids and holds instances by reference.</summary>
    private sealed class InMemoryOrderRepository : IOrderRepository
    {
        private readonly Dictionary<long, Order> _orders = new();
        private long _nextId;

        public Task<Order> AddAsync(Order order, CancellationToken cancellationToken = default)
        {
            order.Id = ++_nextId;
            _orders[order.Id] = order;
            return Task.FromResult(order);
        }

        public Task UpdateAsync(Order order, CancellationToken cancellationToken = default)
        {
            _orders[order.Id] = order;
            return Task.CompletedTask;
        }

        public Task<Order?> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_orders.GetValueOrDefault(id));

        public Task<Order?> GetByBrokerIdAsync(string brokerOrderId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_orders.Values.FirstOrDefault(o => o.BrokerOrderId == brokerOrderId));

        public Task<IReadOnlyList<Order>> GetByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Order>>(_orders.Values.Where(o => o.CorrelationId == correlationId).ToList());

        public Task<IReadOnlyList<Order>> GetOpenOrdersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Order>>(_orders.Values.Where(o => !o.IsTerminal).ToList());

        public Task<IReadOnlyList<Order>> GetOrdersAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Order>>(_orders.Values.Where(o => o.CreatedAtUtc >= fromUtc && o.CreatedAtUtc <= toUtc).ToList());
    }
}
