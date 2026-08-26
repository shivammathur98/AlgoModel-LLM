namespace AlgoTrader.UnitTests.Trading;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Application.Repositories;
using AlgoTrader.Application.Safety;
using AlgoTrader.Domain.Broker;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Execution;
using AlgoTrader.Domain.Instruments;
using AlgoTrader.Domain.MarketData;
using AlgoTrader.Domain.Orders;
using AlgoTrader.MarketData;
using AlgoTrader.Trading;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

/// <summary>
/// Verifies the trading loop (§8, §11): it ingests live ticks into the shared cache, idles safely outside
/// Paper/Live or when unconfigured, forwards ticks to the mode's decision cycle (paper in Paper, the
/// session-bound live cycle in Live), and bridges live broker order updates to the execution engine. The
/// loop itself places no orders — that is the cycles' job, exercised in their own tests.
/// </summary>
public sealed class TradingLoopServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 24, 4, 30, 0, TimeSpan.Zero);

    // ---- Mode / configuration gating -------------------------------------

    [Fact]
    public async Task BacktestMode_DoesNotConnectFeed()
    {
        var (service, feed, _) = Build(Trading(TradingMode.Backtest), Creds(), Universe("INFY"), new FakeKillSwitch(), Infy());

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        feed.ConnectCallCount.Should().Be(0);
    }

    [Fact]
    public async Task PaperMode_WithoutCredentials_DoesNotConnectFeed()
    {
        var (service, feed, _) = Build(Trading(TradingMode.Paper), new BrokerSettings(), Universe("INFY"), new FakeKillSwitch(), Infy());

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        feed.ConnectCallCount.Should().Be(0);
    }

    [Fact]
    public async Task PaperMode_WithEmptyUniverse_DoesNotConnectFeed()
    {
        var (service, feed, _) = Build(Trading(TradingMode.Paper), Creds(), Universe(), new FakeKillSwitch());

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        feed.ConnectCallCount.Should().Be(0);
    }

    [Fact]
    public async Task KillSwitchEngaged_DoesNotConnectFeed()
    {
        var killSwitch = new FakeKillSwitch();
        killSwitch.Engage("halted for test");
        var (service, feed, _) = Build(Trading(TradingMode.Paper), Creds(), Universe("INFY"), killSwitch, Infy());

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        feed.ConnectCallCount.Should().Be(0);
    }

    // ---- Ingestion happy path --------------------------------------------

    [Fact]
    public async Task PaperMode_Configured_ConnectsAndSubscribesResolvedTokens()
    {
        // GHOST does not resolve to an instrument and must be skipped, not fatal.
        var (service, feed, _) = Build(
            Trading(TradingMode.Paper), Creds(), Universe("INFY", "TCS", "GHOST"), new FakeKillSwitch(), Infy(), Tcs());

        await service.StartAsync(CancellationToken.None);

        feed.ConnectCallCount.Should().Be(1);
        feed.SubscribedTokens.Should().BeEquivalentTo(new[] { 111, 222 });

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task TickReceived_UpdatesLastPriceCache()
    {
        var (service, feed, cache) = Build(Trading(TradingMode.Paper), Creds(), Universe("INFY"), new FakeKillSwitch(), Infy());
        await service.StartAsync(CancellationToken.None);

        feed.RaiseTick(new Tick(111, T0, LastPrice: 253.25m, BidPrice: 253.20m, AskPrice: 253.30m, Volume: 5_000));

        cache.Get(111).Should().NotBeNull();
        cache.Get(111)!.Value.Price.Should().Be(253.25m);

        await service.StopAsync(CancellationToken.None);
    }

    // ---- Paper decision-cycle forwarding ---------------------------------

    [Fact]
    public async Task PaperMode_TickReceived_IsForwardedToDecisionCycle()
    {
        var cycle = new SpyPaperTradingCycle();
        var (service, feed) = BuildWithCycle(Trading(TradingMode.Paper), cycle, Infy());
        await service.StartAsync(CancellationToken.None);

        var tick = new Tick(111, T0, LastPrice: 253.25m, BidPrice: 253.20m, AskPrice: 253.30m, Volume: 5_000);
        feed.RaiseTick(tick);

        var forwarded = await cycle.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        forwarded.InstrumentToken.Should().Be(111);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LiveMode_TickReceived_IsNotForwardedToPaperCycle()
    {
        // Live fills come from the broker, not the paper cycle; the paper ledger is not the source of truth.
        var cycle = new SpyPaperTradingCycle();
        var (service, feed) = BuildWithCycle(LiveTrading(), cycle, Infy());
        await service.StartAsync(CancellationToken.None);

        feed.RaiseTick(new Tick(111, T0, LastPrice: 253.25m, BidPrice: 253.20m, AskPrice: 253.30m, Volume: 5_000));

        // Give any (erroneous) fire-and-forget forwarding a chance to run before asserting it did not.
        await Task.Delay(100);
        cycle.CallCount.Should().Be(0);

        await service.StopAsync(CancellationToken.None);
    }

    // ---- Live decision-cycle forwarding + lifecycle (§8, §11, §26) -------

    [Fact]
    public async Task LiveMode_TickReceived_IsForwardedToLiveCycle_WhichIsAttachedThenDetached()
    {
        var cycle = new SpyLiveTradingCycle();
        var (service, feed) = BuildWithLiveCycle(LiveTrading(), cycle, Infy());
        await service.StartAsync(CancellationToken.None);

        var tick = new Tick(111, T0, LastPrice: 253.25m, BidPrice: 253.20m, AskPrice: 253.30m, Volume: 5_000);
        feed.RaiseTick(tick);

        var forwarded = await cycle.Received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        forwarded.InstrumentToken.Should().Be(111);
        cycle.AttachCount.Should().Be(1); // bound to the authenticated session before ticks flowed

        await service.StopAsync(CancellationToken.None);
        cycle.DetachCount.Should().Be(1); // released at shutdown
    }

    [Fact]
    public async Task PaperMode_TickReceived_IsNotForwardedToLiveCycle()
    {
        // The live cycle acts on broker-truth state and must never run in Paper mode, nor be bound to a session.
        var cycle = new SpyLiveTradingCycle();
        var (service, feed) = BuildWithLiveCycle(Trading(TradingMode.Paper), cycle, Infy());
        await service.StartAsync(CancellationToken.None);

        feed.RaiseTick(new Tick(111, T0, LastPrice: 253.25m, BidPrice: 253.20m, AskPrice: 253.30m, Volume: 5_000));

        await Task.Delay(100);
        cycle.CallCount.Should().Be(0);
        cycle.AttachCount.Should().Be(0);

        await service.StopAsync(CancellationToken.None);
    }

    // ---- Live broker-update bridge (§25, §26) ----------------------------

    [Fact]
    public async Task LiveMode_BrokerOrderUpdate_IsForwardedToExecutionEngine()
    {
        var broker = new FakeBroker();
        var engine = new FakeExecutionEngine();
        var (service, _, _) = Build(
            LiveTrading(), Creds(), Universe("INFY"), new FakeKillSwitch(), new[] { Infy() },
            configureServices: services =>
            {
                // Singleton so the loop's session scope and per-event scope resolve these same instances.
                services.AddSingleton<ITradingBroker>(broker);
                services.AddSingleton<IExecutionEngine>(engine);
                services.AddSingleton<IOrderRepository>(new FakeOrderRepository());
            });

        await service.StartAsync(CancellationToken.None);

        var update = new BrokerOrderUpdate("KITE-1", OrderState.Filled, FilledQuantity: 10, AverageFillPrice: 100m, T0);
        broker.RaiseOrderUpdated(update);

        // The bridge is fire-and-forget; wait briefly for the reconciliation to run.
        var applied = await engine.Applied.Task.WaitAsync(TimeSpan.FromSeconds(5));
        applied.BrokerOrderId.Should().Be("KITE-1");
        applied.State.Should().Be(OrderState.Filled);

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LiveMode_MarketDataDisconnect_ReconnectsAndResubscribes()
    {
        var (service, feed, _) = Build(
            LiveTrading(), Creds(), Universe("INFY"), new FakeKillSwitch(), new[] { Infy() },
            configureServices: services =>
            {
                services.AddSingleton<ITradingBroker>(new FakeBroker());
                services.AddSingleton<IOrderRepository>(new FakeOrderRepository());
            });

        await service.StartAsync(CancellationToken.None);

        // Wait for initial connection
        while (feed.ConnectCallCount == 0) await Task.Delay(10);

        // Disconnect raises event, which signals the reconnect loop
        feed.RaiseDisconnect("test drop");

        // Wait for loop to wake up and connect + subscribe again
        while (feed.ConnectCallCount < 2) await Task.Delay(10);

        feed.ConnectCallCount.Should().BeGreaterThan(1);
        feed.SubscribedTokens.Should().Contain(111);

        await service.StopAsync(CancellationToken.None);
    }

    // ---- End-of-day reconciliation (§26, §28) ----------------------------

    [Fact]
    public async Task LiveMode_AtShutdown_RunsReconciliation_AndEngagesKillSwitchOnCritical()
    {
        // A critical discrepancy at session close means untracked risk or a wrong book: the platform must not
        // resume trading until an operator has reviewed it, so the kill switch is engaged.
        var killSwitch = new FakeKillSwitch();
        var reconciler = new SpyLiveReconciler(criticalReport: true);
        var (service, _, _) = Build(
            LiveTrading(), Creds(), Universe("INFY"), killSwitch, new[] { Infy() },
            configureServices: services =>
            {
                services.AddSingleton<ITradingBroker>(new FakeBroker());
                services.AddSingleton<IOrderRepository>(new FakeOrderRepository());
            },
            reconciler: reconciler);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        reconciler.CallCount.Should().Be(1);
        killSwitch.IsEngaged.Should().BeTrue();
        killSwitch.Reason.Should().Contain("reconciliation");
    }

    [Fact]
    public async Task LiveMode_AtShutdown_CleanReconciliation_DoesNotEngageKillSwitch()
    {
        var killSwitch = new FakeKillSwitch();
        var reconciler = new SpyLiveReconciler(criticalReport: false);
        var (service, _, _) = Build(
            LiveTrading(), Creds(), Universe("INFY"), killSwitch, new[] { Infy() },
            configureServices: services =>
            {
                services.AddSingleton<ITradingBroker>(new FakeBroker());
                services.AddSingleton<IOrderRepository>(new FakeOrderRepository());
            },
            reconciler: reconciler);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        reconciler.CallCount.Should().Be(1);
        killSwitch.IsEngaged.Should().BeFalse();
    }

    [Fact]
    public async Task PaperMode_AtShutdown_DoesNotRunReconciliation()
    {
        // Reconciliation compares against broker truth; it is a Live-only concern.
        var reconciler = new SpyLiveReconciler(criticalReport: false);
        var (service, _, _) = Build(
            Trading(TradingMode.Paper), Creds(), Universe("INFY"), new FakeKillSwitch(), new[] { Infy() },
            configureServices: null,
            reconciler: reconciler);

        await service.StartAsync(CancellationToken.None);
        await service.StopAsync(CancellationToken.None);

        reconciler.CallCount.Should().Be(0);
    }

    // ---- Builders ---------------------------------------------------------

    private static TradingSettings Trading(TradingMode mode) => new() { Mode = mode };

    private static TradingSettings LiveTrading() => new()
    {
        Mode = TradingMode.Live,
        EnableLiveTrading = true,
        LiveTradingAcknowledgement = TradingSettings.RequiredLiveAcknowledgement
    };

    private static BrokerSettings Creds() => new() { ApiKey = "test-key", AccessToken = "test-token" };

    private static MarketDataSettings Universe(params string[] symbols)
    {
        var settings = new MarketDataSettings { Exchange = "NSE" };
        settings.Universe.Symbols.AddRange(symbols);
        return settings;
    }

    private static Instrument Infy() => Instrument.NseEquity(111, "INFY", "Infosys");
    private static Instrument Tcs() => Instrument.NseEquity(222, "TCS", "Tata Consultancy");

    private static (TradingLoopService Service, FakeLiveFeed Feed, LastPriceCache Cache) Build(
        TradingSettings trading,
        BrokerSettings broker,
        MarketDataSettings marketData,
        IKillSwitch killSwitch,
        params Instrument[] instruments)
        => Build(trading, broker, marketData, killSwitch, instruments, configureServices: null);

    private static (TradingLoopService Service, FakeLiveFeed Feed, LastPriceCache Cache) Build(
        TradingSettings trading,
        BrokerSettings broker,
        MarketDataSettings marketData,
        IKillSwitch killSwitch,
        Instrument[] instruments,
        Action<IServiceCollection>? configureServices,
        ILiveReconciler? reconciler = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IInstrumentRepository>(_ => new FakeInstrumentRepository(instruments));
        configureServices?.Invoke(services);
        var provider = services.BuildServiceProvider();

        var feed = new FakeLiveFeed();
        var cache = new LastPriceCache();
        var service = new TradingLoopService(
            provider.GetRequiredService<IServiceScopeFactory>(), feed, cache, new NoopPaperTradingCycle(), new NoopLiveTradingCycle(),
            reconciler ?? new SpyLiveReconciler(criticalReport: false),
            Options.Create(trading), Options.Create(broker), Options.Create(marketData),
            killSwitch, NullLogger<TradingLoopService>.Instance);
        return (service, feed, cache);
    }

    /// <summary>Builds a loop with a caller-supplied decision cycle to assert tick forwarding. A fake broker keeps
    /// the Live path's reconciliation wiring from faulting so the loop stays connected.</summary>
    private static (TradingLoopService Service, FakeLiveFeed Feed) BuildWithCycle(
        TradingSettings trading, IPaperTradingCycle cycle, params Instrument[] instruments)
    {
        var services = new ServiceCollection();
        services.AddScoped<IInstrumentRepository>(_ => new FakeInstrumentRepository(instruments));
        services.AddSingleton<ITradingBroker>(new FakeBroker());
        services.AddSingleton<IOrderRepository>(new FakeOrderRepository());
        var provider = services.BuildServiceProvider();

        var feed = new FakeLiveFeed();
        var service = new TradingLoopService(
            provider.GetRequiredService<IServiceScopeFactory>(), feed, new LastPriceCache(), cycle, new NoopLiveTradingCycle(),
            new SpyLiveReconciler(criticalReport: false),
            Options.Create(trading), Options.Create(Creds()), Options.Create(Universe("INFY")),
            new FakeKillSwitch(), NullLogger<TradingLoopService>.Instance);
        return (service, feed);
    }

    /// <summary>Builds a loop with a caller-supplied <b>live</b> decision cycle to assert Live-mode tick forwarding
    /// and the attach/detach lifecycle. A fake broker authenticates so the live path binds the cycle.</summary>
    private static (TradingLoopService Service, FakeLiveFeed Feed) BuildWithLiveCycle(
        TradingSettings trading, ILiveTradingCycle liveCycle, params Instrument[] instruments)
    {
        var services = new ServiceCollection();
        services.AddScoped<IInstrumentRepository>(_ => new FakeInstrumentRepository(instruments));
        services.AddSingleton<ITradingBroker>(new FakeBroker());
        services.AddSingleton<IOrderRepository>(new FakeOrderRepository());
        var provider = services.BuildServiceProvider();

        var feed = new FakeLiveFeed();
        var service = new TradingLoopService(
            provider.GetRequiredService<IServiceScopeFactory>(), feed, new LastPriceCache(), new NoopPaperTradingCycle(), liveCycle,
            new SpyLiveReconciler(criticalReport: false),
            Options.Create(trading), Options.Create(Creds()), Options.Create(Universe("INFY")),
            new FakeKillSwitch(), NullLogger<TradingLoopService>.Instance);
        return (service, feed);
    }

    // ---- Fakes ------------------------------------------------------------

    private sealed class NoopPaperTradingCycle : IPaperTradingCycle
    {
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task OnTickAsync(Tick tick, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SpyPaperTradingCycle : IPaperTradingCycle
    {
        private int _callCount;
        public TaskCompletionSource<Tick> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount => Volatile.Read(ref _callCount);

        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task OnTickAsync(Tick tick, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            Received.TrySetResult(tick);
            return Task.CompletedTask;
        }
    }

    private sealed class NoopLiveTradingCycle : ILiveTradingCycle
    {
        public void Attach(IServiceProvider sessionServices) { }
        public void Detach() { }
        public Task OnTickAsync(Tick tick, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class SpyLiveTradingCycle : ILiveTradingCycle
    {
        private int _callCount;
        private int _attachCount;
        private int _detachCount;
        public TaskCompletionSource<Tick> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount => Volatile.Read(ref _callCount);
        public int AttachCount => Volatile.Read(ref _attachCount);
        public int DetachCount => Volatile.Read(ref _detachCount);

        public void Attach(IServiceProvider sessionServices) => Interlocked.Increment(ref _attachCount);
        public void Detach() => Interlocked.Increment(ref _detachCount);

        public Task OnTickAsync(Tick tick, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            Received.TrySetResult(tick);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLiveFeed : ILiveMarketDataProvider
    {
        public string ProviderName => "FakeFeed";
        public bool IsConnected { get; private set; }
        public int ConnectCallCount { get; private set; }
        public List<int> SubscribedTokens { get; } = new();

        public Task ConnectAsync(CancellationToken cancellationToken = default)
        {
            ConnectCallCount++;
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task SubscribeAsync(IEnumerable<int> instrumentTokens, CancellationToken cancellationToken = default)
        {
            SubscribedTokens.AddRange(instrumentTokens);
            return Task.CompletedTask;
        }

        public Task UnsubscribeAsync(IEnumerable<int> instrumentTokens, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return Task.CompletedTask;
        }

        public void RaiseTick(Tick tick) => TickReceived?.Invoke(this, new TickEventArgs { Tick = tick });
        
        public void RaiseDisconnect(string reason) 
        {
            IsConnected = false;
            Disconnected?.Invoke(this, new MarketDataDisconnectedEventArgs { Reason = reason });
        }

        public event EventHandler<TickEventArgs>? TickReceived;
#pragma warning disable CS0067 // Not exercised in these tests.
        public event EventHandler<MarketDepthEventArgs>? DepthReceived;
#pragma warning restore CS0067
        public event EventHandler<MarketDataDisconnectedEventArgs>? Disconnected;
    }

    private sealed class FakeInstrumentRepository : IInstrumentRepository
    {
        private readonly Dictionary<string, Instrument> _bySymbol;

        public FakeInstrumentRepository(IEnumerable<Instrument> instruments) =>
            _bySymbol = instruments.ToDictionary(i => i.Symbol, StringComparer.OrdinalIgnoreCase);

        public Task<Instrument?> GetBySymbolAsync(string symbol, string exchange, CancellationToken cancellationToken = default) =>
            Task.FromResult(_bySymbol.GetValueOrDefault(symbol));

        public Task<Instrument?> GetByTokenAsync(int instrumentToken, CancellationToken cancellationToken = default) =>
            Task.FromResult(_bySymbol.Values.FirstOrDefault(i => i.InstrumentToken == instrumentToken));

        public Task<IReadOnlyList<Instrument>> GetTradableAsync(string exchange, string segment, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Instrument>>(_bySymbol.Values.ToList());

        public Task<int> UpsertAsync(IReadOnlyList<Instrument> instruments, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed class FakeKillSwitch : IKillSwitch
    {
        public KillSwitchState State { get; private set; } = KillSwitchState.Disengaged;
        public bool IsEngaged => State == KillSwitchState.Engaged;
        public string? Reason { get; private set; }

        public void Engage(string reason, string initiatedBy = "system")
        {
            State = KillSwitchState.Engaged;
            Reason = reason;
        }

        public void Reset(string initiatedBy = "operator") => State = KillSwitchState.Disengaged;

#pragma warning disable CS0067 // Not raised in these tests.
        public event EventHandler<KillSwitchEventArgs>? StateChanged;
#pragma warning restore CS0067
    }

    private sealed class SpyLiveReconciler : ILiveReconciler
    {
        private readonly bool _critical;
        private int _callCount;

        public SpyLiveReconciler(bool criticalReport) => _critical = criticalReport;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<ReconciliationReport> ReconcileAsync(
            ITradingBroker broker, IOrderRepository orders, ProductType product,
            DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            var discrepancies = _critical
                ? new[]
                {
                    new ReconciliationDiscrepancy(
                        ReconciliationIssue.OrphanPosition, ReconciliationSeverity.Critical, 111, "INFY", "test discrepancy"),
                }
                : Array.Empty<ReconciliationDiscrepancy>();
            return Task.FromResult(new ReconciliationReport(
                fromUtc, toUtc, product, 0, 0, 0, 0m, 0m, discrepancies));
        }
    }

    private sealed class FakeOrderRepository : IOrderRepository
    {
        public Task<IReadOnlyList<Order>> GetOpenOrdersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Order>>(Array.Empty<Order>());

        public Task<IReadOnlyList<Order>> GetOrdersAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Order>>(Array.Empty<Order>());

        public Task<Order> AddAsync(Order order, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateAsync(Order order, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Order?> GetByIdAsync(long id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<Order?> GetByBrokerIdAsync(string brokerOrderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Order>> GetByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeExecutionEngine : IExecutionEngine
    {
        public TaskCompletionSource<BrokerOrderUpdate> Applied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ExecutionResult> ApplyBrokerUpdateAsync(BrokerOrderUpdate update, CancellationToken cancellationToken = default)
        {
            Applied.TrySetResult(update);
            return Task.FromResult(new ExecutionResult(true, 1, update.State, update.BrokerOrderId));
        }

        public Task<ExecutionResult> SubmitAsync(OrderRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExecutionResult> CancelAsync(long orderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ExecutionResult> ApplyPaperFillAsync(long orderId, decimal fillPrice, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeBroker : ITradingBroker
    {
        public string ProviderName => "FakeBroker";
        public bool IsAuthenticated { get; private set; }

        public Task AuthenticateAsync(CancellationToken cancellationToken = default)
        {
            IsAuthenticated = true;
            return Task.CompletedTask;
        }

        public void RaiseOrderUpdated(BrokerOrderUpdate update) => OrderUpdated?.Invoke(this, update);

        public event EventHandler<BrokerOrderUpdate>? OrderUpdated;
        public event EventHandler<EventArgs>? StreamDisconnected;
        public bool IsConnected => true;
        public Task<BrokerProfile> GetProfileAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrokerFunds> GetFundsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BrokerHolding>> GetHoldingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BrokerPositionSummary>> GetPositionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BrokerOrderInfo>> GetOrdersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrokerOrderInfo> GetOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PlaceOrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ModifyOrderResult> ModifyOrderAsync(string brokerOrderId, OrderModification modification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

