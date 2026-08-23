namespace AlgoTrader.Trading;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Application.Observability;
using AlgoTrader.Application.Repositories;
using AlgoTrader.Application.Safety;
using AlgoTrader.Domain.Broker;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Execution;
using AlgoTrader.Domain.MarketData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// The paper/live trading loop (§8, §11) — the first and only <see cref="BackgroundService"/> in the
/// solution. It owns the live-market-data session for the trading day: it connects the feed, subscribes
/// the configured instrument universe (§9), and records every tick's last price in the shared
/// <see cref="ILastPriceCache"/> so downstream components can fill and evaluate against current prices.
/// <para>
/// <b>What it does.</b> In Paper mode it drives the full decision cycle from the tick stream via
/// <see cref="IPaperTradingCycle"/> — strategy → risk → sizing → simulated execution, with price-fed fills
/// booked to the in-memory ledger. In Live mode it drives the decision cycle against broker-truth positions
/// and funds via <see cref="ILiveTradingCycle"/> (bound to the authenticated session), and reconciles the
/// resulting asynchronous broker fill/cancel updates
/// (<see cref="ITradingBroker.OrderUpdated"/> → <see cref="IExecutionEngine.ApplyBrokerUpdateAsync"/>). At
/// session close it runs an end-of-day reconciliation (<see cref="ILiveReconciler"/>) against broker truth and
/// engages the kill switch if a critical discrepancy is found.
/// </para>
/// <para>
/// It only activates in <see cref="TradingMode.Paper"/> or <see cref="TradingMode.Live"/>; in Research/
/// Backtest it idles. It fails soft: missing credentials, an empty universe, an engaged kill switch, or a
/// feed connection error are logged and leave the API host running rather than crashing it. The loop is a
/// singleton, so it resolves scoped services (<see cref="ITradingBroker"/>, repositories,
/// <see cref="IExecutionEngine"/>) through an <see cref="IServiceScopeFactory"/> rather than capturing them.
/// </para>
/// </summary>
public sealed class TradingLoopService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILiveMarketDataProvider _liveFeed;
    private readonly ILastPriceCache _lastPrices;
    private readonly IPaperTradingCycle _paperCycle;
    private readonly ILiveTradingCycle _liveCycle;
    private readonly ILiveReconciler _reconciler;
    private readonly TradingSettings _trading;
    private readonly BrokerSettings _brokerSettings;
    private readonly MarketDataSettings _marketData;
    private readonly IKillSwitch _killSwitch;
    private readonly ILogger<TradingLoopService> _logger;
    private readonly ITradingMetrics _metrics;

    /// <summary>IST is UTC+5:30; used to bound the end-of-day reconciliation window to the current trading day.</summary>
    private static readonly TimeSpan IndiaStandardTimeOffset = TimeSpan.FromHours(5.5);

    // Session state, established only once the loop actually connects.
    private IServiceScope? _sessionScope;
    private ITradingBroker? _sessionBroker;
    private bool _runPaperCycle;
    private bool _runLiveCycle;
    private CancellationToken _stoppingToken;

    // In-flight decision-cycle tasks. Ticks launch cycles fire-and-forget; shutdown drains these before the
    // session scope is disposed, so no cycle can reach for a disposed provider or submit an order mid-teardown.
    private readonly object _cycleLock = new();
    private readonly List<Task> _inFlightCycles = new();

    public TradingLoopService(
        IServiceScopeFactory scopeFactory,
        ILiveMarketDataProvider liveFeed,
        ILastPriceCache lastPrices,
        IPaperTradingCycle paperCycle,
        ILiveTradingCycle liveCycle,
        ILiveReconciler reconciler,
        IOptions<TradingSettings> trading,
        IOptions<BrokerSettings> brokerSettings,
        IOptions<MarketDataSettings> marketData,
        IKillSwitch killSwitch,
        ILogger<TradingLoopService> logger,
        ITradingMetrics? metrics = null)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _liveFeed = liveFeed ?? throw new ArgumentNullException(nameof(liveFeed));
        _lastPrices = lastPrices ?? throw new ArgumentNullException(nameof(lastPrices));
        _paperCycle = paperCycle ?? throw new ArgumentNullException(nameof(paperCycle));
        _liveCycle = liveCycle ?? throw new ArgumentNullException(nameof(liveCycle));
        _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
        _trading = trading?.Value ?? throw new ArgumentNullException(nameof(trading));
        _brokerSettings = brokerSettings?.Value ?? throw new ArgumentNullException(nameof(brokerSettings));
        _marketData = marketData?.Value ?? throw new ArgumentNullException(nameof(marketData));
        _killSwitch = killSwitch ?? throw new ArgumentNullException(nameof(killSwitch));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics ?? NullTradingMetrics.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        // Live market data (and therefore this loop) only makes sense in Paper/Live. Fail closed elsewhere.
        if (_trading.Mode is not (TradingMode.Paper or TradingMode.Live))
        {
            _logger.LogInformation(
                "Trading loop idle: the live feed runs only in Paper or Live mode (current mode {Mode}).", _trading.Mode);
            return;
        }

        if (_killSwitch.IsEngaged)
        {
            _logger.LogWarning(
                "Trading loop will not start: kill switch is engaged ({Reason}).", _killSwitch.Reason ?? "no reason given");
            return;
        }

        // Never fabricate credentials (§5). Without them the WebSocket feed cannot authenticate.
        if (string.IsNullOrWhiteSpace(_brokerSettings.ApiKey) || string.IsNullOrWhiteSpace(_brokerSettings.AccessToken))
        {
            _logger.LogWarning(
                "Trading loop idle: live market data requires Broker:ApiKey and Broker:AccessToken, which are not configured.");
            return;
        }

        var tokens = (await ResolveUniverseAsync(stoppingToken).ConfigureAwait(false)).Distinct().ToArray();
        if (tokens.Length == 0)
        {
            _logger.LogWarning("Trading loop idle: no instruments resolved from MarketData:Universe:Symbols.");
            return;
        }

        _liveFeed.TickReceived += OnTickReceived;
        _liveFeed.Disconnected += OnDisconnected;

        try
        {
            // Paper mode runs the decision cycle off the in-memory ledger with price-fed fills. Live mode runs
            // its decision cycle against broker-truth state, enabled below only once the broker authenticates.
            _runPaperCycle = _trading.Mode == TradingMode.Paper;

            // Live only: authenticate the session broker, then wire async fill/cancel reconciliation and bind the
            // live decision cycle to that authenticated session. Both stay dormant if authentication fails.
            if (_trading.Mode == TradingMode.Live)
                await WireBrokerReconciliationAsync(stoppingToken).ConfigureAwait(false);

            await _liveFeed.ConnectAsync(stoppingToken).ConfigureAwait(false);
            await _liveFeed.SubscribeAsync(tokens, stoppingToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Trading loop connected in {Mode} mode; subscribed to {Count} instrument(s).", _trading.Mode, tokens.Length);

            // Ingestion, and in Paper mode the decision cycle, run from the tick-received handler. Hold the
            // session open until shutdown.
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — fall through to cleanup.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Trading loop feed error; the API host stays up but market data has stopped flowing.");
        }
        finally
        {
            await ShutdownAsync(tokens).ConfigureAwait(false);
        }
    }

    /// <summary>Resolves the configured symbol universe (§9) to instrument tokens, skipping any that don't resolve.</summary>
    private async Task<IReadOnlyList<int>> ResolveUniverseAsync(CancellationToken cancellationToken)
    {
        var symbols = _marketData.Universe.Symbols;
        if (symbols.Count == 0)
            return Array.Empty<int>();

        var tokens = new List<int>(symbols.Count);
        using var scope = _scopeFactory.CreateScope();
        var instruments = scope.ServiceProvider.GetRequiredService<IInstrumentRepository>();

        foreach (var symbol in symbols)
        {
            var instrument = await instruments.GetBySymbolAsync(symbol, _marketData.Exchange, cancellationToken).ConfigureAwait(false);
            if (instrument is null)
            {
                _logger.LogWarning(
                    "Universe symbol {Symbol} on {Exchange} did not resolve to an instrument; skipping.", symbol, _marketData.Exchange);
                continue;
            }

            tokens.Add(instrument.InstrumentToken);
        }

        return tokens;
    }

    /// <summary>Live only: authenticates a session broker and bridges its order updates to the execution engine.</summary>
    private async Task WireBrokerReconciliationAsync(CancellationToken cancellationToken)
    {
        _sessionScope = _scopeFactory.CreateScope();
        _sessionBroker = _sessionScope.ServiceProvider.GetRequiredService<ITradingBroker>();

        try
        {
            await _sessionBroker.AuthenticateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Market data can still flow without a trading session; just don't wire reconciliation.
            _logger.LogError(ex,
                "Broker authentication failed; live order reconciliation is not active. Market data continues.");
            return;
        }

        _sessionBroker.OrderUpdated += OnBrokerOrderUpdate;
        _logger.LogInformation(
            "Broker order reconciliation wired to the execution engine (provider {Provider}).", _sessionBroker.ProviderName);

        // Bind the live decision cycle to the authenticated session scope: its broker reads and order submissions
        // must run on this scope (a fresh scope's broker is unauthenticated). Reached only on successful auth, so
        // the live cycle never runs without a working broker session.
        _liveCycle.Attach(_sessionScope.ServiceProvider);
        _runLiveCycle = true;
        _logger.LogInformation("Live decision cycle attached to the authenticated broker session.");
    }

    private void OnTickReceived(object? sender, TickEventArgs e)
    {
        _lastPrices.Update(e.Tick);

        // Drive the active decision cycle from the tick stream. Fire-and-forget like broker reconciliation, so a
        // slow cycle never blocks feed ingestion; each cycle serializes its own work internally. At most one of
        // these flags is set (Paper vs Live), so a tick drives exactly one cycle. Tracked so shutdown can drain.
        if (_runPaperCycle)
            TrackCycle(RunPaperCycleAsync(e.Tick));
        if (_runLiveCycle)
            TrackCycle(RunLiveCycleAsync(e.Tick));
    }

    /// <summary>Registers an in-flight cycle task so shutdown can await it, self-removing on completion.</summary>
    private void TrackCycle(Task cycle)
    {
        lock (_cycleLock)
            _inFlightCycles.Add(cycle);

        cycle.ContinueWith(
            t => { lock (_cycleLock) _inFlightCycles.Remove(t); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task RunPaperCycleAsync(Tick tick)
    {
        try
        {
            await _paperCycle.OnTickAsync(tick, _stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Paper trading cycle failed processing a tick for {Token}.", tick.InstrumentToken);
        }
    }

    private async Task RunLiveCycleAsync(Tick tick)
    {
        try
        {
            await _liveCycle.OnTickAsync(tick, _stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Live trading cycle failed processing a tick for {Token}.", tick.InstrumentToken);
        }
    }

    private void OnDisconnected(object? sender, MarketDataDisconnectedEventArgs e) =>
        _logger.LogWarning(
            "Live market data feed disconnected: {Reason}. Automatic reconnection is a later phase.", e.Reason ?? "unknown");

    private void OnBrokerOrderUpdate(object? sender, BrokerOrderUpdate update) =>
        _ = ReconcileBrokerUpdateAsync(update);

    private async Task ReconcileBrokerUpdateAsync(BrokerOrderUpdate update)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var engine = scope.ServiceProvider.GetRequiredService<IExecutionEngine>();
            await engine.ApplyBrokerUpdateAsync(update, _stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to reconcile broker update for {BrokerOrderId}.", update.BrokerOrderId);
        }
    }

    /// <summary>
    /// Live only: at session close, reconciles the day's local order/position record against broker truth
    /// (§26, §28) and logs the outcome. A <b>critical</b> discrepancy — untracked risk or a wrong book — engages
    /// the kill switch, so the platform will not resume trading until an operator has investigated and reset it
    /// (§15). Runs on the still-authenticated session scope; it never throws, so shutdown always completes. A
    /// failure to reconcile (e.g. a transient broker read error at shutdown) is logged but does not engage the
    /// kill switch — only a confirmed critical drift does.
    /// </summary>
    private async Task ReconcileSessionAsync()
    {
        if (_trading.Mode != TradingMode.Live || _sessionScope is null || _sessionBroker is not { IsAuthenticated: true })
            return;

        try
        {
            var repo = _sessionScope.ServiceProvider.GetRequiredService<IOrderRepository>();
            var asOfUtc = DateTimeOffset.UtcNow;
            var report = await _reconciler.ReconcileAsync(
                _sessionBroker, repo, ProductType.Intraday, StartOfCurrentIstDay(asOfUtc), asOfUtc, CancellationToken.None)
                .ConfigureAwait(false);

            LogReconciliation(report);

            var criticalCount = report.Discrepancies.Count(d => d.Severity == ReconciliationSeverity.Critical);
            _metrics.ReconciliationCompleted(report.IsClean, criticalCount);

            if (report.HasCritical)
            {
                _killSwitch.Engage(
                    $"End-of-day reconciliation found {criticalCount} critical discrepancy(ies); resume requires operator review.",
                    initiatedBy: "reconciliation");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "End-of-day reconciliation failed; the day's book could not be verified against the broker.");
        }
    }

    /// <summary>Emits the reconciliation report as structured log lines — a headline plus one line per discrepancy.</summary>
    private void LogReconciliation(ReconciliationReport report)
    {
        if (report.IsClean)
        {
            _logger.LogInformation("EOD reconciliation: {Summary}", report.Summary);
            return;
        }

        if (report.HasCritical)
            _logger.LogError("EOD reconciliation: {Summary}", report.Summary);
        else
            _logger.LogWarning("EOD reconciliation: {Summary}", report.Summary);

        // One structured line per discrepancy. Only order ids/tokens/symbols — no sensitive account data (§5).
        foreach (var d in report.Discrepancies)
        {
            var level = d.Severity == ReconciliationSeverity.Critical ? LogLevel.Error : LogLevel.Warning;
            _logger.Log(level,
                "EOD reconciliation discrepancy: {Issue} [{Severity}] {Symbol} (token {Token}, brokerOrderId {BrokerOrderId}): {Detail}",
                d.Issue, d.Severity, d.Symbol, d.InstrumentToken, d.BrokerOrderId ?? "-", d.Detail);
        }
    }

    /// <summary>Start of the current IST trading day expressed in UTC — the intraday reconciliation window's lower bound.</summary>
    private static DateTimeOffset StartOfCurrentIstDay(DateTimeOffset asOfUtc)
    {
        var ist = asOfUtc.ToOffset(IndiaStandardTimeOffset);
        var istMidnight = new DateTimeOffset(ist.Year, ist.Month, ist.Day, 0, 0, 0, IndiaStandardTimeOffset);
        return istMidnight.ToUniversalTime();
    }

    private async Task ShutdownAsync(IReadOnlyList<int> tokens)
    {
        _runPaperCycle = false;
        _runLiveCycle = false;
        _liveFeed.TickReceived -= OnTickReceived;
        _liveFeed.Disconnected -= OnDisconnected;
        if (_sessionBroker is not null)
            _sessionBroker.OrderUpdated -= OnBrokerOrderUpdate;

        // Release the live cycle's session binding before the scope is disposed below, so no in-flight decision
        // reaches for a disposed provider (any that raced through are contained by RunLiveCycleAsync).
        _liveCycle.Detach();

        // Drain any cycle tasks already launched from the tick handler. The handler is now detached and the flags
        // are false, so no new cycles start; awaiting the outstanding ones guarantees none is still using the
        // session provider when it is disposed below. Per-cycle errors are already logged, so ignore them here.
        Task[] pending;
        lock (_cycleLock)
            pending = _inFlightCycles.ToArray();
        if (pending.Length > 0)
        {
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch { /* each cycle logs its own failure; we only need them finished before disposal. */ }
        }

        // End-of-day reconciliation (§26, §28): verify the day's book against broker truth while the authenticated
        // session scope is still alive. Live-only; never throws (shutdown must complete regardless).
        await ReconcileSessionAsync().ConfigureAwait(false);

        try
        {
            if (tokens.Count > 0)
                await _liveFeed.UnsubscribeAsync(tokens, CancellationToken.None).ConfigureAwait(false);
            await _liveFeed.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while disconnecting the live feed during shutdown.");
        }

        _sessionScope?.Dispose();
        _sessionScope = null;
        _sessionBroker = null;
        _logger.LogInformation("Trading loop stopped.");
    }
}
