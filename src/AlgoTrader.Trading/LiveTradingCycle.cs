namespace AlgoTrader.Trading;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Application.Observability;
using AlgoTrader.Application.Repositories;
using AlgoTrader.Domain.Broker;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Execution;
using AlgoTrader.Domain.MarketData;
using AlgoTrader.Domain.Orders;
using AlgoTrader.Domain.Portfolio;
using AlgoTrader.Domain.Risk;
using AlgoTrader.Domain.Sizing;
using AlgoTrader.Domain.Strategy;
using AlgoTrader.Domain.Trading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Default <see cref="ILiveTradingCycle"/>. On each closed decision candle it captures broker-truth account state
/// (<see cref="ILiveAccountView"/>), runs the active strategy → risk engine → position sizer, and submits real
/// orders via the session-scope <see cref="IExecutionEngine"/> — which alone decides whether to transmit to the
/// broker (only when the live triple-gate is satisfied) or simulate/refuse.
/// <para>
/// <b>No fabricated fills, no look-ahead.</b> A decision uses only candles closed at or before the decision bar.
/// The cycle never books a fill itself: a submitted order becomes broker/local truth and its fill arrives through
/// the loop's <see cref="ITradingBroker.OrderUpdated"/> → <see cref="IExecutionEngine.ApplyBrokerUpdateAsync"/>
/// bridge. Between submission and fill the persisted working order guards against re-entry (§25, §26).
/// </para>
/// <para>
/// <b>Serialized on the session scope.</b> Ticks and candle closes funnel through one gate, so broker reads,
/// strategy evaluation and submission for all instruments run one at a time against the single authenticated
/// session (its <see cref="Microsoft.EntityFrameworkCore.DbContext"/> is not concurrency-safe).
/// </para>
/// </summary>
public sealed class LiveTradingCycle : ILiveTradingCycle, IDisposable
{
    /// <summary>Bounds per-instrument candle history so a long session cannot grow it without limit; ample for EMA/ATR lookbacks.</summary>
    private const int MaxHistoryCandles = 500;

    private readonly IStrategy _strategy;
    private readonly IRiskEngine _risk;
    private readonly IPositionSizer _sizer;
    private readonly ILiveAccountView _accountView;
    private readonly ICandleAggregator _aggregator;
    private readonly RiskSettings _riskSettings;
    private readonly Timeframe _timeframe;
    private readonly string _exchange;
    private readonly ProductType _product;
    private readonly ILogger<LiveTradingCycle> _logger;
    private readonly ITradingMetrics _metrics;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, List<Candle>> _history = new();
    private volatile IServiceProvider? _session;

    public LiveTradingCycle(
        IStrategy strategy,
        IRiskEngine risk,
        IPositionSizer sizer,
        ILiveAccountView accountView,
        ICandleAggregator aggregator,
        IOptions<StrategySettings> strategySettings,
        IOptions<RiskSettings> riskSettings,
        IOptions<MarketDataSettings> marketData,
        ProductType product,
        ILogger<LiveTradingCycle> logger,
        ITradingMetrics? metrics = null)
    {
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _risk = risk ?? throw new ArgumentNullException(nameof(risk));
        _sizer = sizer ?? throw new ArgumentNullException(nameof(sizer));
        _accountView = accountView ?? throw new ArgumentNullException(nameof(accountView));
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _riskSettings = riskSettings?.Value ?? throw new ArgumentNullException(nameof(riskSettings));
        _timeframe = strategySettings?.Value.Timeframe ?? throw new ArgumentNullException(nameof(strategySettings));
        _exchange = marketData?.Value.Exchange ?? throw new ArgumentNullException(nameof(marketData));
        _product = product;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics ?? NullTradingMetrics.Instance;
    }

    /// <inheritdoc />
    public void Attach(IServiceProvider sessionServices) =>
        _session = sessionServices ?? throw new ArgumentNullException(nameof(sessionServices));

    /// <inheritdoc />
    public void Detach() => _session = null;

    /// <inheritdoc />
    public async Task OnTickAsync(Tick tick, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tick);

        var session = _session;
        if (session is null)
            return; // Not bound to a session yet (or already detached): ignore.

        _metrics.TickProcessed(TradingMode.Live);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // No simulated fill here (contrast the paper cycle): live fills arrive via the broker-update bridge.
            var closed = _aggregator.OnTick(tick, _timeframe);
            if (closed is not null)
                await OnCandleClosedAsync(session, closed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task OnCandleClosedAsync(IServiceProvider session, Candle closed, CancellationToken cancellationToken)
    {
        var token = closed.InstrumentToken;
        _metrics.CandleClosed(TradingMode.Live);
        var history = AppendHistory(token, closed);

        // Decision time is the bar's close (its start plus one interval) — the instant the information is available.
        var decisionTimeUtc = closed.TimestampUtc.AddMinutes(_timeframe.Minutes());

        var broker = session.GetRequiredService<ITradingBroker>();
        var orders = session.GetRequiredService<IOrderRepository>();

        LiveAccountSnapshot snapshot;
        try
        {
            snapshot = await _accountView.CaptureAsync(broker, orders, _product, decisionTimeUtc, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Broker read failed (auth/network): we cannot make a safe decision without truth. Skip this candle.
            _logger.LogError(ex, "Live account capture failed for {Symbol}; skipping this candle.", closed.Symbol);
            return;
        }

        var openPosition = snapshot.GetOpenPosition(token);
        var context = new StrategyContext
        {
            Symbol = closed.Symbol,
            InstrumentToken = token,
            Timeframe = _timeframe,
            Candles = history,
            OpenPosition = openPosition,
            CurrentTimestampUtc = closed.TimestampUtc,
            AvailableCapital = snapshot.AvailableCash
        };

        IReadOnlyList<Signal> signals;
        try
        {
            signals = _strategy.OnCandleClosed(context);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Strategy {Strategy} threw evaluating {Symbol}; skipping this candle.", _strategy.Name, closed.Symbol);
            return;
        }

        // Guard against stacking two orders for the same instrument within a single candle: the snapshot is taken
        // once per cycle, so it cannot yet reflect an order this same batch just submitted. This set closes that gap.
        var actedTokens = new HashSet<int>();
        foreach (var signal in signals)
            await HandleSignalAsync(session, signal, closed, snapshot, openPosition, actedTokens, decisionTimeUtc, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSignalAsync(
        IServiceProvider session, Signal signal, Candle closed, LiveAccountSnapshot snapshot,
        OpenPosition? openPosition, HashSet<int> actedTokens, DateTimeOffset decisionTimeUtc, CancellationToken cancellationToken)
    {
        var token = signal.InstrumentToken;

        // Thread the signal's correlation id (and instrument) through every log emitted while acting on it.
        using var logScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = signal.CorrelationId,
            ["Symbol"] = signal.Symbol,
            ["Strategy"] = signal.StrategyName
        });
        _metrics.SignalGenerated(TradingMode.Live, signal.StrategyName, signal.Direction);

        // At most one working order per instrument: a persisted, still-open order means one is already at the
        // broker. Never stack a second while the first is unfilled — including a second order this same cycle
        // (actedTokens), which the once-per-cycle snapshot cannot yet see.
        if (snapshot.HasInFlightOrder(token) || actedTokens.Contains(token))
        {
            _logger.LogDebug("Skipping {Direction} for {Symbol}: an order is already working at the broker.", signal.Direction, signal.Symbol);
            return;
        }

        actedTokens.Add(token);

        // Every gate is now fed from broker-derived truth: realized P&L and trade count for the day come from the
        // snapshot (recomputed from the local store's filled orders, net of charges), alongside the kill-switch,
        // max-positions, symbol-already-open, session-hours, capital, funds and broker-connectivity gates.
        var riskContext = new RiskEvaluationContext(
            TimestampUtc: decisionTimeUtc,
            AvailableCapital: snapshot.AvailableCash,
            RealizedPnlToday: snapshot.RealizedPnlToday,
            TradesToday: snapshot.TradesToday,
            OpenPositionCount: snapshot.OpenPositions.Count,
            OpenOrderCount: snapshot.InFlightOrderTokens.Count,
            SymbolsWithOpenPositions: snapshot.SymbolsWithOpenPositions,
            IsMarketDataStale: false,
            IsBrokerConnected: true);

        if (signal.Direction == SignalDirection.LongEntry)
            await HandleEntryAsync(session, signal, closed, snapshot, openPosition, riskContext, cancellationToken).ConfigureAwait(false);
        else
            await HandleExitAsync(session, signal, openPosition, riskContext, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleEntryAsync(
        IServiceProvider session, Signal signal, Candle closed, LiveAccountSnapshot snapshot,
        OpenPosition? openPosition, RiskEvaluationContext riskContext, CancellationToken cancellationToken)
    {
        if (openPosition is not null)
        {
            _logger.LogDebug("Skipping entry for {Symbol}: a position is already open at the broker.", signal.Symbol);
            return;
        }

        var decision = await _risk.EvaluateSignalAsync(signal, riskContext, cancellationToken).ConfigureAwait(false);
        if (!decision.IsApproved)
        {
            _metrics.RiskRejected(TradingMode.Live, decision.Reason);
            _logger.LogInformation("Risk rejected entry for {Symbol}: {Reason} ({Detail}).", signal.Symbol, decision.Reason, decision.Detail);
            return;
        }

        // Risk-based sizing needs a stop below the entry reference. Both bundled strategies emit one; if a signal
        // omits it we cannot bound risk, so we decline rather than guess.
        var entryReference = signal.EntryPrice ?? closed.Close;
        if (signal.StopPrice is not { } stop || stop <= 0m || stop >= entryReference)
        {
            _logger.LogInformation(
                "Skipping entry for {Symbol}: a valid stop below entry ({Entry}) is required for sizing (stop {Stop}).",
                signal.Symbol, entryReference, signal.StopPrice);
            return;
        }

        var size = _sizer.Calculate(new PositionSizeRequest(
            EntryPrice: entryReference,
            StopPrice: stop,
            AvailableCapital: snapshot.AvailableCash,
            MaxCapitalPerTrade: _riskSettings.MaxCapitalPerTrade,
            MaxRiskPerTrade: _riskSettings.MaxRiskPerTrade,
            MaxExposurePerSymbol: _riskSettings.MaxExposurePerSymbol,
            CurrentSymbolExposure: 0m,
            Method: PositionSizingMethod.RiskBased));

        if (size.IsRejected || size.Quantity <= 0)
        {
            _logger.LogInformation("Sizing produced no position for {Symbol}: {Reason}.", signal.Symbol, size.RejectionReason ?? "zero quantity");
            return;
        }

        var request = new OrderRequest(
            InstrumentToken: signal.InstrumentToken,
            Symbol: signal.Symbol,
            Exchange: _exchange,
            Side: OrderSide.Buy,
            Type: OrderType.Market,
            Quantity: size.Quantity,
            Product: _product,
            StrategyName: signal.StrategyName)
        {
            CorrelationId = signal.CorrelationId
        };

        var result = await SubmitAsync(session, request, cancellationToken).ConfigureAwait(false);
        if (result.IsAccepted)
            _logger.LogInformation("Live ENTRY submitted: {Qty} {Symbol} (state {State}, {CorrelationId}).",
                size.Quantity, signal.Symbol, result.State, signal.CorrelationId);
        else
            _logger.LogWarning("Execution rejected entry for {Symbol}: {Message}.", signal.Symbol, result.Message);
    }

    private async Task HandleExitAsync(
        IServiceProvider session, Signal signal, OpenPosition? openPosition,
        RiskEvaluationContext riskContext, CancellationToken cancellationToken)
    {
        if (openPosition is null)
        {
            _logger.LogDebug("Skipping exit for {Symbol}: no open position at the broker.", signal.Symbol);
            return;
        }

        var decision = await _risk.EvaluateSignalAsync(signal, riskContext, cancellationToken).ConfigureAwait(false);
        if (!decision.IsApproved)
        {
            // Exits are only blocked by a system-integrity failure (kill switch / broker down); emergency
            // square-off is a separate flow. Surface it and leave the position for that flow to handle.
            _metrics.RiskRejected(TradingMode.Live, decision.Reason);
            _logger.LogWarning("Risk blocked exit for {Symbol}: {Reason} ({Detail}).", signal.Symbol, decision.Reason, decision.Detail);
            return;
        }

        var request = new OrderRequest(
            InstrumentToken: signal.InstrumentToken,
            Symbol: signal.Symbol,
            Exchange: _exchange,
            Side: OrderSide.Sell,
            Type: OrderType.Market,
            Quantity: openPosition.Quantity,
            Product: _product,
            StrategyName: signal.StrategyName)
        {
            CorrelationId = signal.CorrelationId
        };

        var result = await SubmitAsync(session, request, cancellationToken).ConfigureAwait(false);
        if (result.IsAccepted)
            _logger.LogInformation("Live EXIT submitted: {Qty} {Symbol} (state {State}).", openPosition.Quantity, signal.Symbol, result.State);
        else
            _logger.LogWarning("Execution rejected exit for {Symbol}: {Message}.", signal.Symbol, result.Message);
    }

    private List<Candle> AppendHistory(int token, Candle closed)
    {
        if (!_history.TryGetValue(token, out var candles))
        {
            candles = new List<Candle>();
            _history[token] = candles;
        }

        candles.Add(closed);
        if (candles.Count > MaxHistoryCandles)
            candles.RemoveRange(0, candles.Count - MaxHistoryCandles);
        return candles;
    }

    private static async Task<ExecutionResult> SubmitAsync(IServiceProvider session, OrderRequest request, CancellationToken cancellationToken)
    {
        // Resolve the execution engine from the authenticated session scope (a fresh scope's broker is not
        // authenticated). The engine owns the live triple-gate decision at transmit time.
        var engine = session.GetRequiredService<IExecutionEngine>();
        return await engine.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _gate.Dispose();
}
