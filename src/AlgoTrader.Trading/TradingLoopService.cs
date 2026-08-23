namespace AlgoTrader.Trading;

using AlgoTrader.Application.Configuration;
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
/// <b>Batch 1 (this class) does ingestion only.</b> It never places an order. In Live mode it also wires
/// asynchronous broker fill/cancel reconciliation (<see cref="ITradingBroker.OrderUpdated"/> →
/// <see cref="IExecutionEngine.ApplyBrokerUpdateAsync"/>) so those updates flow to the execution engine
/// once broker postbacks are enabled (a later phase). The strategy → risk → execution decision cycle and
/// price-fed paper fills are added in batch 2.
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
    private readonly TradingSettings _trading;
    private readonly BrokerSettings _brokerSettings;
    private readonly MarketDataSettings _marketData;
    private readonly IKillSwitch _killSwitch;
    private readonly ILogger<TradingLoopService> _logger;

    // Session state, established only once the loop actually connects.
    private IServiceScope? _sessionScope;
    private ITradingBroker? _sessionBroker;
    private CancellationToken _stoppingToken;

    public TradingLoopService(
        IServiceScopeFactory scopeFactory,
        ILiveMarketDataProvider liveFeed,
        ILastPriceCache lastPrices,
        IOptions<TradingSettings> trading,
        IOptions<BrokerSettings> brokerSettings,
        IOptions<MarketDataSettings> marketData,
        IKillSwitch killSwitch,
        ILogger<TradingLoopService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _liveFeed = liveFeed ?? throw new ArgumentNullException(nameof(liveFeed));
        _lastPrices = lastPrices ?? throw new ArgumentNullException(nameof(lastPrices));
        _trading = trading?.Value ?? throw new ArgumentNullException(nameof(trading));
        _brokerSettings = brokerSettings?.Value ?? throw new ArgumentNullException(nameof(brokerSettings));
        _marketData = marketData?.Value ?? throw new ArgumentNullException(nameof(marketData));
        _killSwitch = killSwitch ?? throw new ArgumentNullException(nameof(killSwitch));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
            // Live only: wire async broker reconciliation. Dormant until broker postbacks land (a later
            // phase); in Paper, fills arrive from the tick stream via the execution engine's paper fill.
            if (_trading.Mode == TradingMode.Live)
                await WireBrokerReconciliationAsync(stoppingToken).ConfigureAwait(false);

            await _liveFeed.ConnectAsync(stoppingToken).ConfigureAwait(false);
            await _liveFeed.SubscribeAsync(tokens, stoppingToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Trading loop connected in {Mode} mode; subscribed to {Count} instrument(s).", _trading.Mode, tokens.Length);

            // Batch 1 owns ingestion only: hold the session open until shutdown. The decision cycle and
            // price-fed paper fills are wired in batch 2.
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
    }

    private void OnTickReceived(object? sender, TickEventArgs e) => _lastPrices.Update(e.Tick);

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

    private async Task ShutdownAsync(IReadOnlyList<int> tokens)
    {
        _liveFeed.TickReceived -= OnTickReceived;
        _liveFeed.Disconnected -= OnDisconnected;
        if (_sessionBroker is not null)
            _sessionBroker.OrderUpdated -= OnBrokerOrderUpdate;

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
