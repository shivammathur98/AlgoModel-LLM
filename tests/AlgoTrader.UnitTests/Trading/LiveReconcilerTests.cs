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
/// Verifies the daily reconciliation (§26, §28): our local order/position record is compared against broker
/// truth and every divergence is surfaced as a typed, severity-ranked discrepancy. Order-level checks match by
/// broker id (missing at broker, missing locally, state and filled-quantity drift); position-level checks
/// compare broker-held longs against the net of the window's local fills (orphan, phantom, size mismatch).
/// An orphan position or a filled-quantity divergence is <b>critical</b> — it means untracked risk or a wrong
/// book. The reconciler reads only; it never mutates state.
/// </summary>
public sealed class LiveReconcilerTests
{
    private const int Token = 111;
    private static readonly DateTimeOffset T0 = new(2026, 8, 24, 6, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset From = T0.AddHours(-8);

    private static LiveReconciler Reconciler() =>
        new(new LiveAccountView(new ZeroCostCalculator(), NullLogger<LiveAccountView>.Instance));

    private static BrokerFunds Funds(decimal cash) => new(cash, UsedMargin: 0m, AvailableMargin: cash);

    // ---- Clean -----------------------------------------------------------

    [Fact]
    public async Task CleanReport_WhenEveryOrderAndPositionAgrees()
    {
        // One local buy, filled at the broker, and the broker holds the matching long.
        var broker = new FakeBroker(Funds(50_000m),
            positions: new[] { LongPos(Token, 100) },
            orders: new[] { BrokerOrder("B1", Token, OrderSide.Buy, 100, OrderState.Filled, 100) });
        var repo = new FakeOrderRepository(
            Local(Token, OrderSide.Buy, 100, OrderState.Filled, filled: 100, brokerId: "B1"));

        var report = await Reconciler().ReconcileAsync(broker, repo, ProductType.Intraday, From, T0);

        report.IsClean.Should().BeTrue();
        report.HasCritical.Should().BeFalse();
        report.Discrepancies.Should().BeEmpty();
        report.LocalTransmittedOrderCount.Should().Be(1);
        report.BrokerOrderCount.Should().Be(1);
        report.BrokerOpenPositionCount.Should().Be(1);
        report.BrokerAvailableCash.Should().Be(50_000m);
        report.Summary.Should().Contain("CLEAN");
    }

    // ---- Order-level drift -----------------------------------------------

    [Fact]
    public async Task FilledQuantityMismatch_IsCritical()
    {
        // A round trip locally (net flat, so no position noise); the broker reports a different fill on the buy.
        var broker = new FakeBroker(Funds(50_000m), NoPositions,
            orders: new[]
            {
                BrokerOrder("B1", Token, OrderSide.Buy, 100, OrderState.Filled, 50), // broker filled only 50
                BrokerOrder("B2", Token, OrderSide.Sell, 100, OrderState.Filled, 100),
            });
        var repo = new FakeOrderRepository(
            Local(Token, OrderSide.Buy, 100, OrderState.Filled, filled: 100, brokerId: "B1"),
            Local(Token, OrderSide.Sell, 100, OrderState.Filled, filled: 100, brokerId: "B2"));

        var report = await Reconciler().ReconcileAsync(broker, repo, ProductType.Intraday, From, T0);

        var d = report.Discrepancies.Should().ContainSingle().Subject;
        d.Issue.Should().Be(ReconciliationIssue.FilledQuantityMismatch);
        d.Severity.Should().Be(ReconciliationSeverity.Critical);
        d.BrokerOrderId.Should().Be("B1");
        report.HasCritical.Should().BeTrue();
    }

    [Fact]
    public async Task OrderStateMismatch_WithMatchingFill_IsWarning()
    {
        // We believe the order is cancelled; the broker still shows it working. Neither filled → no position effect.
        var broker = new FakeBroker(Funds(50_000m), NoPositions,
            orders: new[] { BrokerOrder("B1", Token, OrderSide.Buy, 100, OrderState.Open, 0) });
        var repo = new FakeOrderRepository(
            Local(Token, OrderSide.Buy, 100, OrderState.Cancelled, filled: 0, brokerId: "B1"));

        var report = await Reconciler().ReconcileAsync(broker, repo, ProductType.Intraday, From, T0);

        var d = report.Discrepancies.Should().ContainSingle().Subject;
        d.Issue.Should().Be(ReconciliationIssue.OrderStateMismatch);
        d.Severity.Should().Be(ReconciliationSeverity.Warning);
        report.HasCritical.Should().BeFalse();
    }

    [Fact]
    public async Task OrderTransmittedLocally_ButAbsentFromBrokerBook_IsCritical()
    {
        // A working local order (filled 0 → no position effect) whose broker id the broker book does not contain.
        var broker = new FakeBroker(Funds(50_000m), NoPositions, orders: Array.Empty<BrokerOrderInfo>());
        var repo = new FakeOrderRepository(
            Local(Token, OrderSide.Buy, 100, OrderState.Open, filled: 0, brokerId: "GHOST"));

        var report = await Reconciler().ReconcileAsync(broker, repo, ProductType.Intraday, From, T0);

        var d = report.Discrepancies.Should().ContainSingle().Subject;
        d.Issue.Should().Be(ReconciliationIssue.OrderMissingAtBroker);
        d.Severity.Should().Be(ReconciliationSeverity.Critical);
        d.BrokerOrderId.Should().Be("GHOST");
    }

    [Fact]
    public async Task BrokerOrder_WithNoLocalRecord_IsWarning()
    {
        var broker = new FakeBroker(Funds(50_000m), NoPositions,
            orders: new[] { BrokerOrder("EXT", Token, OrderSide.Buy, 5, OrderState.Open, 0) });
        var repo = new FakeOrderRepository(); // we have nothing on our books

        var report = await Reconciler().ReconcileAsync(broker, repo, ProductType.Intraday, From, T0);

        var d = report.Discrepancies.Should().ContainSingle().Subject;
        d.Issue.Should().Be(ReconciliationIssue.OrderMissingLocally);
        d.Severity.Should().Be(ReconciliationSeverity.Warning);
        d.BrokerOrderId.Should().Be("EXT");
    }

    // ---- Position-level drift --------------------------------------------

    [Fact]
    public async Task OrphanPosition_HeldAtBrokerButNotLocally_IsCritical()
    {
        // The broker holds a long we have no local fills for — the most dangerous drift: untracked risk.
        var broker = new FakeBroker(Funds(50_000m),
            positions: new[] { LongPos(Token, 100) },
            orders: Array.Empty<BrokerOrderInfo>());
        var repo = new FakeOrderRepository();

        var report = await Reconciler().ReconcileAsync(broker, repo, ProductType.Intraday, From, T0);

        var d = report.Discrepancies.Should().ContainSingle().Subject;
        d.Issue.Should().Be(ReconciliationIssue.OrphanPosition);
        d.Severity.Should().Be(ReconciliationSeverity.Critical);
        d.InstrumentToken.Should().Be(Token);
    }

    [Fact]
    public async Task PhantomPosition_ImpliedLocallyButNotHeldAtBroker_IsWarning()
    {
        // Our fill record implies a long the broker's position endpoint does not report. Order matches cleanly.
        var broker = new FakeBroker(Funds(50_000m), NoPositions,
            orders: new[] { BrokerOrder("B1", Token, OrderSide.Buy, 100, OrderState.Filled, 100) });
        var repo = new FakeOrderRepository(
            Local(Token, OrderSide.Buy, 100, OrderState.Filled, filled: 100, brokerId: "B1"));

        var report = await Reconciler().ReconcileAsync(broker, repo, ProductType.Intraday, From, T0);

        var d = report.Discrepancies.Should().ContainSingle().Subject;
        d.Issue.Should().Be(ReconciliationIssue.PhantomPosition);
        d.Severity.Should().Be(ReconciliationSeverity.Warning);
    }

    [Fact]
    public async Task PositionQuantityMismatch_BrokerHoldsMore_IsCritical()
    {
        // Broker holds 100; our fills imply 60. The broker holding MORE than we track is untracked exposure —
        // as dangerous as an orphan, so it must be Critical (a mere warning would not engage the kill switch).
        var broker = new FakeBroker(Funds(50_000m),
            positions: new[] { LongPos(Token, 100) },
            orders: new[] { BrokerOrder("B1", Token, OrderSide.Buy, 100, OrderState.Open, 60) });
        var repo = new FakeOrderRepository(
            Local(Token, OrderSide.Buy, 100, OrderState.Open, filled: 60, brokerId: "B1"));

        var report = await Reconciler().ReconcileAsync(broker, repo, ProductType.Intraday, From, T0);

        var d = report.Discrepancies.Should().ContainSingle().Subject;
        d.Issue.Should().Be(ReconciliationIssue.PositionQuantityMismatch);
        d.Severity.Should().Be(ReconciliationSeverity.Critical);
        report.HasCritical.Should().BeTrue();
    }

    [Fact]
    public async Task PositionQuantityMismatch_BrokerHoldsLess_IsWarning()
    {
        // Broker holds 60; our fills imply 100. Over-stating our own position is the safer direction → Warning.
        var broker = new FakeBroker(Funds(50_000m),
            positions: new[] { LongPos(Token, 60) },
            orders: new[] { BrokerOrder("B1", Token, OrderSide.Buy, 100, OrderState.Filled, 100) });
        var repo = new FakeOrderRepository(
            Local(Token, OrderSide.Buy, 100, OrderState.Filled, filled: 100, brokerId: "B1"));

        var report = await Reconciler().ReconcileAsync(broker, repo, ProductType.Intraday, From, T0);

        var d = report.Discrepancies.Should().ContainSingle().Subject;
        d.Issue.Should().Be(ReconciliationIssue.PositionQuantityMismatch);
        d.Severity.Should().Be(ReconciliationSeverity.Warning);
        report.HasCritical.Should().BeFalse();
    }

    // ---- Ranking & guards ------------------------------------------------

    [Fact]
    public async Task Discrepancies_AreRankedCriticalFirst()
    {
        // An orphan (critical) at one instrument and an unknown broker order (warning) at another.
        var broker = new FakeBroker(Funds(50_000m),
            positions: new[] { LongPos(Token, 100) },
            orders: new[] { BrokerOrder("EXT", 222, OrderSide.Buy, 5, OrderState.Open, 0) });
        var repo = new FakeOrderRepository();

        var report = await Reconciler().ReconcileAsync(broker, repo, ProductType.Intraday, From, T0);

        report.Discrepancies.Should().HaveCount(2);
        report.Discrepancies[0].Severity.Should().Be(ReconciliationSeverity.Critical);
        report.HasCritical.Should().BeTrue();
        report.Summary.Should().Contain("critical");
    }

    [Fact]
    public async Task ReconcileAsync_NullBroker_Throws()
    {
        var act = async () => await Reconciler().ReconcileAsync(
            null!, new FakeOrderRepository(), ProductType.Intraday, From, T0);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ---- Helpers ---------------------------------------------------------

    private static readonly BrokerPositionSummary[] NoPositions = Array.Empty<BrokerPositionSummary>();

    private static BrokerPositionSummary LongPos(int token, int qty) =>
        new("SYM", token, ProductType.Intraday, OrderSide.Buy, qty, 250m, UnrealizedPnl: 0m);

    private static BrokerOrderInfo BrokerOrder(
        string id, int token, OrderSide side, int qty, OrderState state, int filledQty) =>
        new(id, "SYM", token, side, OrderType.Market, qty, Price: null, state, filledQty,
            AverageFillPrice: filledQty > 0 ? 100m : null, StatusMessage: null);

    private static Order Local(
        int token, OrderSide side, int qty, OrderState state, int filled, string? brokerId,
        ProductType product = ProductType.Intraday) =>
        new()
        {
            InstrumentToken = token,
            Symbol = "SYM",
            Exchange = "NSE",
            Side = side,
            Type = OrderType.Market,
            Product = product,
            Quantity = qty,
            FilledQuantity = filled,
            AverageFillPrice = filled > 0 ? 100m : null,
            State = state,
            BrokerOrderId = brokerId,
            CorrelationId = "c",
            CreatedAtUtc = T0.AddHours(-1),
            FilledAtUtc = filled > 0 ? T0.AddHours(-1) : null,
            LastUpdatedAtUtc = T0.AddHours(-1)
        };

    private sealed class ZeroCostCalculator : ITradingCostCalculator
    {
        public TradingCostBreakdown Calculate(CostCalculationContext context) =>
            new(0m, 0m, 0m, 0m, 0m, 0m, 0m, 0m);
    }

    private sealed class FakeBroker : ITradingBroker
    {
        private readonly BrokerFunds _funds;
        private readonly IReadOnlyList<BrokerPositionSummary> _positions;
        private readonly IReadOnlyList<BrokerOrderInfo> _orders;

        public FakeBroker(
            BrokerFunds funds,
            IReadOnlyList<BrokerPositionSummary> positions,
            IReadOnlyList<BrokerOrderInfo> orders)
        {
            _funds = funds;
            _positions = positions;
            _orders = orders;
        }

        public string ProviderName => "FakeBroker";
        public bool IsAuthenticated => true;

        public Task<BrokerFunds> GetFundsAsync(CancellationToken cancellationToken = default) => Task.FromResult(_funds);
        public Task<IReadOnlyList<BrokerPositionSummary>> GetPositionsAsync(CancellationToken cancellationToken = default) => Task.FromResult(_positions);
        public Task<IReadOnlyList<BrokerOrderInfo>> GetOrdersAsync(CancellationToken cancellationToken = default) => Task.FromResult(_orders);

        public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<BrokerProfile> GetProfileAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BrokerHolding>> GetHoldingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrokerOrderInfo> GetOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PlaceOrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ModifyOrderResult> ModifyOrderAsync(string brokerOrderId, OrderModification modification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

#pragma warning disable CS0067 // Not raised in these tests.
        public event EventHandler<BrokerOrderUpdate>? OrderUpdated;
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
