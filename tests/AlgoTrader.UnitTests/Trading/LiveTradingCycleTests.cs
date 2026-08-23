namespace AlgoTrader.UnitTests.Trading;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Application.Repositories;
using AlgoTrader.Domain.Broker;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Execution;
using AlgoTrader.Domain.MarketData;
using AlgoTrader.Domain.Orders;
using AlgoTrader.Domain.Portfolio;
using AlgoTrader.Domain.Risk;
using AlgoTrader.Domain.Strategy;
using AlgoTrader.Domain.Trading;
using AlgoTrader.Risk;
using AlgoTrader.Trading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Verifies the live decision cycle (§8, §11, §26): on a closed decision candle it runs strategy → risk → sizing
/// against <b>broker-truth</b> account state (an <see cref="ILiveAccountView"/> snapshot) and submits <b>real</b>
/// market orders through the execution engine — which owns the live triple-gate. Unlike the paper cycle it never
/// fabricates a fill: the position/in-flight state comes from the snapshot, not a local ledger, and fills arrive
/// later via the broker-update bridge. The same guards hold: one position and at most one working order per
/// instrument, a stop is required to bound risk, and a thrown strategy or broker read is contained.
/// </summary>
public sealed class LiveTradingCycleTests
{
    private const int Token = 111;
    private const string Symbol = "INFY";
    private static readonly DateTimeOffset T0 = new(2026, 8, 24, 4, 30, 0, TimeSpan.Zero);

    // ---- Entry ------------------------------------------------------------

    [Fact]
    public async Task ClosedCandle_ApprovedEntry_SubmitsRealMarketOrder_AgainstBrokerTruth()
    {
        var h = new Harness();
        h.Strategy.OnClose = _ => new[] { Entry(entry: 100m, stop: 95m, target: 110m) };
        h.Aggregator.Enqueue(CandleAt(T0, close: 100m));

        await h.Cycle.OnTickAsync(TickAt(T0, 100m));

        h.Execution.Submitted.Should().HaveCount(1);
        var order = h.Execution.Submitted[0];
        order.Side.Should().Be(OrderSide.Buy);
        order.Type.Should().Be(OrderType.Market);
        order.Quantity.Should().Be(300);          // risk budget 1500 / (100-95) = 300
        order.Product.Should().Be(ProductType.Intraday);
        order.Exchange.Should().Be("NSE");
        order.Price.Should().BeNull();             // market order; no fabricated price
        order.StrategyName.Should().Be("TestStrategy");
    }

    [Fact]
    public async Task Entry_SizesAgainstBrokerReportedCash()
    {
        // Buying power is broker truth from the snapshot, not a local ledger. With cash low enough that the
        // capital cap (not the fixed 1500 risk budget) binds, the size follows the reported cash: 20000 / 100 = 200.
        var h = new Harness();
        h.Account.Cash = 20_000m;
        h.Strategy.OnClose = _ => new[] { Entry(entry: 100m, stop: 95m, target: 110m) };
        h.Aggregator.Enqueue(CandleAt(T0, close: 100m));

        await h.Cycle.OnTickAsync(TickAt(T0, 100m));

        h.Execution.Submitted.Should().ContainSingle();
        h.Execution.Submitted[0].Quantity.Should().Be(200);
    }

    [Fact]
    public async Task Entry_WhenRiskRejects_SubmitsNothing()
    {
        var h = new Harness();
        h.Risk.Approve = false;
        h.Risk.Reason = RiskRejectionReason.MaxTradesPerDayBreached;
        h.Strategy.OnClose = _ => new[] { Entry(entry: 100m, stop: 95m, target: 110m) };
        h.Aggregator.Enqueue(CandleAt(T0, close: 100m));

        await h.Cycle.OnTickAsync(TickAt(T0, 100m));

        h.Execution.Submitted.Should().BeEmpty();
    }

    [Fact]
    public async Task Entry_WithoutStop_IsSkipped_BecauseRiskCannotBeBounded()
    {
        var h = new Harness();
        h.Strategy.OnClose = _ => new[] { Entry(entry: 100m, stop: null, target: 110m) };
        h.Aggregator.Enqueue(CandleAt(T0, close: 100m));

        await h.Cycle.OnTickAsync(TickAt(T0, 100m));

        h.Execution.Submitted.Should().BeEmpty();
    }

    [Fact]
    public async Task Entry_WhenPositionAlreadyOpenAtBroker_IsSkippedBeforeRisk()
    {
        var h = new Harness();
        h.Account.Position = HeldPosition(qty: 10);
        h.Strategy.OnClose = _ => new[] { Entry(entry: 100m, stop: 95m, target: 110m) };
        h.Aggregator.Enqueue(CandleAt(T0, close: 100m));

        await h.Cycle.OnTickAsync(TickAt(T0, 100m));

        h.Execution.Submitted.Should().BeEmpty();
        h.Risk.SignalCalls.Should().Be(0); // short-circuited before hitting the risk engine
    }

    [Fact]
    public async Task Entry_WhenAnOrderIsWorkingAtBroker_IsNotStacked()
    {
        var h = new Harness();
        h.Account.InFlight.Add(Token); // a persisted, still-open order at the broker
        h.Strategy.OnClose = _ => new[] { Entry(entry: 100m, stop: 95m, target: 110m) };
        h.Aggregator.Enqueue(CandleAt(T0, close: 100m));

        await h.Cycle.OnTickAsync(TickAt(T0, 100m));

        h.Execution.Submitted.Should().BeEmpty();
        h.Risk.SignalCalls.Should().Be(0); // in-flight guard precedes risk
    }

    [Fact]
    public async Task Entry_FeedsBrokerDerivedDayFiguresIntoRisk()
    {
        // The daily-loss and trades-per-day gates are only effective if the cycle passes the snapshot's
        // session-day figures (realized P&L, trade count) into the risk context rather than zeros.
        var h = new Harness();
        h.Account.RealizedPnl = -1_234.50m;
        h.Account.Trades = 3;
        h.Strategy.OnClose = _ => new[] { Entry(entry: 100m, stop: 95m, target: 110m) };
        h.Aggregator.Enqueue(CandleAt(T0, close: 100m));

        await h.Cycle.OnTickAsync(TickAt(T0, 100m));

        h.Risk.LastContext.Should().NotBeNull();
        h.Risk.LastContext!.RealizedPnlToday.Should().Be(-1_234.50m);
        h.Risk.LastContext.TradesToday.Should().Be(3);
    }

    // ---- Exit -------------------------------------------------------------
    [Fact]
    public async Task ExitSignal_SubmitsSellForTheFullHeldQuantity()
    {
        var h = new Harness();
        h.Account.Position = HeldPosition(qty: 300);
        h.Strategy.OnClose = ctx => ctx.OpenPosition is not null ? new[] { Exit() } : Array.Empty<Signal>();
        h.Aggregator.Enqueue(CandleAt(T0.AddMinutes(1), close: 110m));

        await h.Cycle.OnTickAsync(TickAt(T0.AddMinutes(1), 110m));

        var sell = h.Execution.Submitted.Should().ContainSingle().Subject;
        sell.Side.Should().Be(OrderSide.Sell);
        sell.Type.Should().Be(OrderType.Market);
        sell.Quantity.Should().Be(300); // full broker-reported size, not a strategy-provided number
    }

    [Fact]
    public async Task ExitSignal_WhenFlat_SubmitsNothing()
    {
        var h = new Harness();
        h.Strategy.OnClose = _ => new[] { Exit() };
        h.Aggregator.Enqueue(CandleAt(T0, close: 100m));

        await h.Cycle.OnTickAsync(TickAt(T0, 100m));

        h.Execution.Submitted.Should().BeEmpty();
    }

    // ---- Lifecycle & resilience ------------------------------------------

    [Fact]
    public async Task WhenNotAttached_IgnoresTick_WithoutTouchingTheBroker()
    {
        var h = new Harness();
        h.Cycle.Detach(); // released (or never bound to a session)
        h.Strategy.OnClose = _ => new[] { Entry(entry: 100m, stop: 95m, target: 110m) };
        h.Aggregator.Enqueue(CandleAt(T0, close: 100m));

        await h.Cycle.OnTickAsync(TickAt(T0, 100m));

        h.Account.CaptureCalls.Should().Be(0); // never even read account state
        h.Execution.Submitted.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenBrokerCaptureThrows_TheCandleIsSkipped_AndNothingIsSubmitted()
    {
        var h = new Harness();
        h.Account.Throw = true; // e.g. broker auth/network failure mid-session
        h.Strategy.OnClose = _ => new[] { Entry(entry: 100m, stop: 95m, target: 110m) };
        h.Aggregator.Enqueue(CandleAt(T0, close: 100m));

        var act = async () => await h.Cycle.OnTickAsync(TickAt(T0, 100m));

        await act.Should().NotThrowAsync();
        h.Execution.Submitted.Should().BeEmpty();
    }

    [Fact]
    public async Task StrategyThatThrows_IsContained_AndDoesNotFaultTheCycle()
    {
        var h = new Harness();
        h.Strategy.OnClose = _ => throw new InvalidOperationException("boom");
        h.Aggregator.Enqueue(CandleAt(T0, close: 100m));

        var act = async () => await h.Cycle.OnTickAsync(TickAt(T0, 100m));

        await act.Should().NotThrowAsync();
        h.Execution.Submitted.Should().BeEmpty();
    }

    // ---- Signals / market data helpers -----------------------------------

    private static Signal Entry(decimal entry, decimal? stop, decimal? target) =>
        new("TestStrategy", "1.0.0", Token, Symbol, SignalDirection.LongEntry, T0, entry, stop, target);

    private static Signal Exit() =>
        new("TestStrategy", "1.0.0", Token, Symbol, SignalDirection.LongExit, T0);

    private static OpenPosition HeldPosition(int qty) =>
        new(Token, Symbol, "TestStrategy", qty, 100m, T0, StopPrice: null, TargetPrice: null, CorrelationId: "held");

    private static Tick TickAt(DateTimeOffset at, decimal price) =>
        new(Token, at, price, price - 0.05m, price + 0.05m, Volume: 1_000);

    private static Candle CandleAt(DateTimeOffset at, decimal close) =>
        new(Token, Symbol, "NSE", Timeframe.Minute1, at, close, close, close, close, Volume: 1_000);

    // ---- Harness ----------------------------------------------------------

    private sealed class Harness
    {
        public ScriptedStrategy Strategy { get; } = new();
        public ToggleRiskEngine Risk { get; } = new();
        public QueueAggregator Aggregator { get; } = new();
        public RecordingExecutionEngine Execution { get; } = new();
        public FakeLiveAccountView Account { get; } = new();
        public LiveTradingCycle Cycle { get; }

        public Harness()
        {
            // The session provider the loop would hand the cycle: the execution engine plus the broker/repo the
            // cycle resolves and passes to the account view (our fake view ignores them, but they must resolve).
            var provider = new ServiceCollection()
                .AddSingleton<IExecutionEngine>(Execution)
                .AddSingleton<ITradingBroker>(new StubBroker())
                .AddSingleton<IOrderRepository>(new StubOrderRepository())
                .BuildServiceProvider();

            Cycle = new LiveTradingCycle(
                Strategy,
                Risk,
                new RiskAwarePositionSizer(),
                Account,
                Aggregator,
                Options.Create(new StrategySettings { Timeframe = Timeframe.Minute1 }),
                Options.Create(new RiskSettings()),
                Options.Create(new MarketDataSettings { Exchange = "NSE" }),
                ProductType.Intraday,
                NullLogger<LiveTradingCycle>.Instance);

            Cycle.Attach(provider);
        }
    }

    private sealed class ScriptedStrategy : IStrategy
    {
        public string Name => "TestStrategy";
        public string Version => "1.0.0";
        public Func<StrategyContext, IReadOnlyList<Signal>> OnClose { get; set; } = _ => Array.Empty<Signal>();
        public IReadOnlyList<Signal> OnCandleClosed(StrategyContext context) => OnClose(context);
    }

    private sealed class ToggleRiskEngine : IRiskEngine
    {
        public bool Approve { get; set; } = true;
        public RiskRejectionReason Reason { get; set; } = RiskRejectionReason.None;
        public int SignalCalls { get; private set; }
        public RiskEvaluationContext? LastContext { get; private set; }

        public Task<RiskDecision> EvaluateSignalAsync(Signal signal, RiskEvaluationContext context, CancellationToken cancellationToken = default)
        {
            SignalCalls++;
            LastContext = context;
            return Task.FromResult(Approve ? RiskDecision.Approved() : RiskDecision.Rejected(Reason));
        }

        public Task<RiskDecision> EvaluateOrderAsync(OrderRequest order, RiskEvaluationContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(RiskDecision.Approved());
    }

    private sealed class QueueAggregator : ICandleAggregator
    {
        private readonly Queue<Candle?> _closes = new();
        public void Enqueue(Candle? candle) => _closes.Enqueue(candle);
        public Candle? OnTick(Tick tick, Timeframe timeframe) => _closes.Count > 0 ? _closes.Dequeue() : null;
        public void Reset(int instrumentToken) => _closes.Clear();
    }

    /// <summary>A scripted broker-truth snapshot. Unlike the paper ledger, position and in-flight state are set by
    /// the test, and the cycle books nothing back — it only submits orders.</summary>
    private sealed class FakeLiveAccountView : ILiveAccountView
    {
        public decimal Cash { get; set; } = 100_000m;
        public decimal RealizedPnl { get; set; }
        public int Trades { get; set; }
        public OpenPosition? Position { get; set; }
        public HashSet<int> InFlight { get; } = new();
        public bool Throw { get; set; }
        public int CaptureCalls { get; private set; }

        public Task<LiveAccountSnapshot> CaptureAsync(
            ITradingBroker broker, IOrderRepository orders, ProductType product, DateTimeOffset asOfUtc, CancellationToken cancellationToken = default)
        {
            CaptureCalls++;
            if (Throw)
                throw new InvalidOperationException("broker read failed");

            var open = new Dictionary<int, OpenPosition>();
            if (Position is not null)
                open[Position.InstrumentToken] = Position;
            return Task.FromResult(new LiveAccountSnapshot(Cash, RealizedPnl, Trades, open, InFlight, asOfUtc));
        }
    }

    /// <summary>Records submitted requests and returns a live-style "submitted to broker" result (no local fill).</summary>
    private sealed class RecordingExecutionEngine : IExecutionEngine
    {
        private long _nextId = 1;
        public List<OrderRequest> Submitted { get; } = new();
        public bool Accept { get; set; } = true;

        public Task<ExecutionResult> SubmitAsync(OrderRequest request, CancellationToken cancellationToken = default)
        {
            Submitted.Add(request);
            var id = _nextId++;
            return Task.FromResult(Accept
                ? new ExecutionResult(true, id, OrderState.Submitted, $"KITE-{id}", "submitted to broker")
                : new ExecutionResult(false, id, OrderState.Rejected, null, "rejected"));
        }

        public Task<ExecutionResult> CancelAsync(long orderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExecutionResult> ApplyPaperFillAsync(long orderId, decimal fillPrice, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExecutionResult> ApplyBrokerUpdateAsync(BrokerOrderUpdate update, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Resolved by the cycle and handed to the (faked) account view, which ignores it; never called here.</summary>
    private sealed class StubBroker : ITradingBroker
    {
        public string ProviderName => "StubBroker";
        public bool IsAuthenticated => true;
        public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<BrokerProfile> GetProfileAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrokerFunds> GetFundsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BrokerHolding>> GetHoldingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BrokerPositionSummary>> GetPositionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BrokerOrderInfo>> GetOrdersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrokerOrderInfo> GetOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PlaceOrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ModifyOrderResult> ModifyOrderAsync(string brokerOrderId, OrderModification modification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
#pragma warning disable CS0067 // Not raised in these tests.
        public event EventHandler<BrokerOrderUpdate>? OrderUpdated;
#pragma warning restore CS0067
    }

    /// <summary>Resolved by the cycle and handed to the (faked) account view, which ignores it; never called here.</summary>
    private sealed class StubOrderRepository : IOrderRepository
    {
        public Task<Order> AddAsync(Order order, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateAsync(Order order, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Order?> GetByIdAsync(long id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Order?> GetByBrokerIdAsync(string brokerOrderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Order>> GetByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Order>> GetOpenOrdersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Order>> GetOrdersAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
