namespace AlgoTrader.UnitTests.Trading;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Domain.Costing;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Execution;
using AlgoTrader.Domain.MarketData;
using AlgoTrader.Domain.Orders;
using AlgoTrader.Domain.Risk;
using AlgoTrader.Domain.Strategy;
using AlgoTrader.Domain.Trading;
using AlgoTrader.Risk;
using AlgoTrader.Trading;
using AlgoTrader.UnitTests.Observability;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Verifies the paper decision cycle (§8, §11, §13, §14): on a closed decision candle it runs strategy → risk →
/// sizing → execution, submits resting market orders, and books honest fills to the ledger on the next tick.
/// Entries only open a position once a price-fed fill occurs (no fabricated price, no look-ahead); guards prevent
/// stacking orders or double entries. A fake aggregator gives precise control over when a candle "closes".
/// </summary>
public sealed class PaperTradingCycleTests
{
    private const int Token = 111;
    private const string Symbol = "INFY";
    private static readonly DateTimeOffset T0 = new(2026, 8, 24, 4, 30, 0, TimeSpan.Zero);

    // ---- Entry ------------------------------------------------------------

    [Fact]
    public async Task ClosedCandle_ApprovedEntry_SubmitsRestingMarketOrder_WithoutOpeningPositionYet()
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
        order.Price.Should().BeNull();             // market order rests; no fabricated price

        // Resting only — nothing is booked until a price-fed fill on a later tick.
        h.Portfolio.GetOpenPosition(Token).Should().BeNull();
    }

    [Fact]
    public async Task RestingEntry_FillsOnNextTick_AtObservedPrice_AndBooksToLedger()
    {
        var h = new Harness();
        h.Strategy.OnClose = _ => new[] { Entry(entry: 100m, stop: 95m, target: 110m) };
        h.Aggregator.Enqueue(CandleAt(T0, close: 100m));

        await h.Cycle.OnTickAsync(TickAt(T0, 100m));               // submits the resting entry
        await h.Cycle.OnTickAsync(TickAt(T0.AddSeconds(30), 100m)); // next tick fills it

        var position = h.Portfolio.GetOpenPosition(Token);
        position.Should().NotBeNull();
        position!.Quantity.Should().Be(300);
        position.AveragePrice.Should().Be(100m);
        position.StopPrice.Should().Be(95m);
        position.TargetPrice.Should().Be(110m);

        var snapshot = h.Portfolio.Snapshot(T0);
        snapshot.Cash.Should().Be(100_000m - (100m * 300 + 20m)); // deployed + one leg of flat charges
        snapshot.TradesToday.Should().Be(1);
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
        h.Portfolio.GetOpenPosition(Token).Should().BeNull();
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
    public async Task Entry_WhenPositionAlreadyOpen_IsSkippedBeforeRisk()
    {
        var h = new Harness();
        // Seed an open position directly in the ledger.
        h.Portfolio.RecordEntryFill(new PaperEntryFill(Token, Symbol, "NSE", "S", ProductType.Intraday, 10, 100m, T0, 95m, 110m, "seed"));
        h.Strategy.OnClose = _ => new[] { Entry(entry: 100m, stop: 95m, target: 110m) };
        h.Aggregator.Enqueue(CandleAt(T0, close: 100m));

        await h.Cycle.OnTickAsync(TickAt(T0, 100m));

        h.Execution.Submitted.Should().BeEmpty();
        h.Risk.SignalCalls.Should().Be(0); // short-circuited before hitting the risk engine
    }

    [Fact]
    public async Task Entry_WhileAnOrderIsInFlight_IsNotStacked()
    {
        var h = new Harness();
        // Two entries for the same instrument on one candle: the first submits and rests in _pending; the second
        // must be short-circuited by the in-flight guard before it reaches the risk engine — never a stacked order.
        h.Strategy.OnClose = _ => new[]
        {
            Entry(entry: 100m, stop: 95m, target: 110m),
            Entry(entry: 100m, stop: 95m, target: 110m),
        };
        h.Aggregator.Enqueue(CandleAt(T0, close: 100m));

        await h.Cycle.OnTickAsync(TickAt(T0, 100m));

        h.Execution.Submitted.Should().ContainSingle();  // the second entry was blocked while the first rested
        h.Risk.SignalCalls.Should().Be(1);               // and it short-circuited before the risk engine
    }

    // ---- Exit -------------------------------------------------------------

    [Fact]
    public async Task ExitSignal_ClosesOpenPosition_AndRealizesPnlOnFill()
    {
        var h = new Harness();
        h.Portfolio.RecordEntryFill(new PaperEntryFill(Token, Symbol, "NSE", "S", ProductType.Intraday, 300, 100m, T0, 95m, 110m, "seed"));
        h.Strategy.OnClose = ctx => ctx.OpenPosition is not null ? new[] { Exit() } : Array.Empty<Signal>();
        h.Aggregator.Enqueue(CandleAt(T0.AddMinutes(1), close: 110m));

        await h.Cycle.OnTickAsync(TickAt(T0.AddMinutes(1), 110m));            // submits resting sell
        await h.Cycle.OnTickAsync(TickAt(T0.AddMinutes(1).AddSeconds(30), 110m)); // fills it

        var sell = h.Execution.Submitted.Single();
        sell.Side.Should().Be(OrderSide.Sell);
        sell.Quantity.Should().Be(300);

        h.Portfolio.GetOpenPosition(Token).Should().BeNull();
        // (110-100)*300 = 3000 gross, minus 20 entry + 20 exit charges = 2960 net.
        h.Portfolio.Snapshot(T0).RealizedPnlToday.Should().Be(2960m);
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

    // ---- Resilience -------------------------------------------------------

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

    // ---- Metrics (observability) -----------------------------------------

    [Fact]
    public async Task ClosedCandle_EmitsTickCandleAndSignal_TaggedPaper()
    {
        var h = new Harness();
        h.Strategy.OnClose = _ => new[] { Entry(entry: 100m, stop: 95m, target: 110m) };
        h.Aggregator.Enqueue(CandleAt(T0, close: 100m));

        await h.Cycle.OnTickAsync(TickAt(T0, 100m));

        h.Metrics.Ticks.Should().ContainSingle().Which.Should().Be(TradingMode.Paper);
        h.Metrics.Candles.Should().ContainSingle().Which.Should().Be(TradingMode.Paper);
        h.Metrics.Signals.Should().ContainSingle()
            .Which.Should().Be((TradingMode.Paper, "TestStrategy", SignalDirection.LongEntry));
    }

    [Fact]
    public async Task Entry_WhenRiskRejects_EmitsRiskRejected_WithReason()
    {
        var h = new Harness();
        h.Risk.Approve = false;
        h.Risk.Reason = RiskRejectionReason.MaxTradesPerDayBreached;
        h.Strategy.OnClose = _ => new[] { Entry(entry: 100m, stop: 95m, target: 110m) };
        h.Aggregator.Enqueue(CandleAt(T0, close: 100m));

        await h.Cycle.OnTickAsync(TickAt(T0, 100m));

        h.Metrics.RiskRejections.Should().ContainSingle()
            .Which.Should().Be((TradingMode.Paper, RiskRejectionReason.MaxTradesPerDayBreached));
    }

    // ---- Signals / market data helpers -----------------------------------

    private static Signal Entry(decimal entry, decimal? stop, decimal? target) =>
        new("TestStrategy", "1.0.0", Token, Symbol, SignalDirection.LongEntry, T0, entry, stop, target);

    private static Signal Exit() =>
        new("TestStrategy", "1.0.0", Token, Symbol, SignalDirection.LongExit, T0);

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
        public FakePaperExecutionEngine Execution { get; } = new();
        public RecordingTradingMetrics Metrics { get; } = new();
        public PaperPortfolio Portfolio { get; }
        public PaperTradingCycle Cycle { get; }

        public Harness()
        {
            Portfolio = new PaperPortfolio(100_000m, new FlatCostCalculator(20m));

            var provider = new ServiceCollection()
                .AddSingleton<IExecutionEngine>(Execution)
                .BuildServiceProvider();

            Cycle = new PaperTradingCycle(
                Strategy,
                Risk,
                new RiskAwarePositionSizer(),
                provider.GetRequiredService<IServiceScopeFactory>(),
                Portfolio,
                Aggregator,
                Options.Create(new StrategySettings { Timeframe = Timeframe.Minute1 }),
                Options.Create(new RiskSettings()),
                Options.Create(new MarketDataSettings { Exchange = "NSE" }),
                ProductType.Intraday,
                NullLogger<PaperTradingCycle>.Instance,
                Metrics);
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

        public Task<RiskDecision> EvaluateSignalAsync(Signal signal, RiskEvaluationContext context, CancellationToken cancellationToken = default)
        {
            SignalCalls++;
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

    /// <summary>Mimics the execution engine's paper behaviour: a market order rests as Open, then fills by id.</summary>
    private sealed class FakePaperExecutionEngine : IExecutionEngine
    {
        private readonly Dictionary<long, OrderRequest> _resting = new();
        private long _nextId = 1;

        public List<OrderRequest> Submitted { get; } = new();

        public Task<ExecutionResult> SubmitAsync(OrderRequest request, CancellationToken cancellationToken = default)
        {
            Submitted.Add(request);
            var id = _nextId++;
            _resting[id] = request; // market order → rests Open (no price on the request)
            return Task.FromResult(new ExecutionResult(true, id, OrderState.Open, null, "resting"));
        }

        public Task<ExecutionResult> ApplyPaperFillAsync(long orderId, decimal fillPrice, CancellationToken cancellationToken = default)
        {
            if (!_resting.Remove(orderId))
                return Task.FromResult(new ExecutionResult(false, orderId, OrderState.New, null, "not resting"));
            return Task.FromResult(new ExecutionResult(true, orderId, OrderState.Filled, null, "filled"));
        }

        public Task<ExecutionResult> CancelAsync(long orderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExecutionResult> ApplyBrokerUpdateAsync(AlgoTrader.Domain.Broker.BrokerOrderUpdate update, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FlatCostCalculator : ITradingCostCalculator
    {
        private readonly decimal _perLeg;
        public FlatCostCalculator(decimal perLeg) => _perLeg = perLeg;
        public TradingCostBreakdown Calculate(CostCalculationContext context) =>
            new(Brokerage: _perLeg, Stt: 0m, ExchangeTransactionCharges: 0m, SebiCharges: 0m,
                StampDuty: 0m, Gst: 0m, DpCharges: 0m, OtherCharges: 0m);
    }
}
