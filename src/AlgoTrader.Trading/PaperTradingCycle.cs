namespace AlgoTrader.Trading;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Application.Observability;
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
/// Default <see cref="IPaperTradingCycle"/>. Wires the live tick stream through the full paper decision cycle
/// (§8, §11, §13, §14): tick-fed fills of resting simulated orders, tick → candle aggregation, and on each
/// closed decision candle the active strategy → risk engine → position sizer → execution engine.
/// <para>
/// <b>Honest, no look-ahead.</b> A decision uses only candles closed at or before the decision bar; entries are
/// submitted as market orders that rest (<see cref="OrderState.Open"/>) and fill on the <i>next</i> observed
/// tick — no price is ever fabricated (§16, §37). Cash and realized P&amp;L are booked in <see cref="IPaperPortfolio"/>
/// net of round-trip charges only when a fill actually occurs.
/// </para>
/// <para>
/// <b>Serialized.</b> Ticks and candle closes for all instruments funnel through a single gate, so the
/// check-then-act guards (one open position and at most one in-flight order per instrument) hold without races.
/// The execution engine is scoped, so it is resolved per operation via <see cref="IServiceScopeFactory"/> — the
/// resting order is persisted, so a later scope fills it by id.
/// </para>
/// </summary>
public sealed class PaperTradingCycle : IPaperTradingCycle, IDisposable
{
    /// <summary>Bounds per-instrument candle history so a long session cannot grow it without limit; ample for EMA/ATR lookbacks.</summary>
    private const int MaxHistoryCandles = 500;

    private readonly IStrategy _strategy;
    private readonly IRiskEngine _risk;
    private readonly IPositionSizer _sizer;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IPaperPortfolio _portfolio;
    private readonly ICandleAggregator _aggregator;
    private readonly RiskSettings _riskSettings;
    private readonly Timeframe _timeframe;
    private readonly string _exchange;
    private readonly ProductType _product;
    private readonly ILogger<PaperTradingCycle> _logger;
    private readonly ITradingMetrics _metrics;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<int, List<Candle>> _history = new();
    private readonly Dictionary<int, PendingOrder> _pending = new();

    public PaperTradingCycle(
        IStrategy strategy,
        IRiskEngine risk,
        IPositionSizer sizer,
        IServiceScopeFactory scopeFactory,
        IPaperPortfolio portfolio,
        ICandleAggregator aggregator,
        IOptions<StrategySettings> strategySettings,
        IOptions<RiskSettings> riskSettings,
        IOptions<MarketDataSettings> marketData,
        ProductType product,
        ILogger<PaperTradingCycle> logger,
        ITradingMetrics? metrics = null)
    {
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _risk = risk ?? throw new ArgumentNullException(nameof(risk));
        _sizer = sizer ?? throw new ArgumentNullException(nameof(sizer));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _portfolio = portfolio ?? throw new ArgumentNullException(nameof(portfolio));
        _aggregator = aggregator ?? throw new ArgumentNullException(nameof(aggregator));
        _riskSettings = riskSettings?.Value ?? throw new ArgumentNullException(nameof(riskSettings));
        _timeframe = strategySettings?.Value.Timeframe ?? throw new ArgumentNullException(nameof(strategySettings));
        _exchange = marketData?.Value.Exchange ?? throw new ArgumentNullException(nameof(marketData));
        _product = product;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics ?? NullTradingMetrics.Instance;
    }

    /// <inheritdoc />
    public async Task OnTickAsync(Tick tick, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tick);
        _metrics.TickProcessed(TradingMode.Paper);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 1) A resting order from a previous tick/candle fills at THIS tick's observed price.
            await TryFillPendingAsync(tick, cancellationToken).ConfigureAwait(false);

            // 2) Feed the tick to the aggregator; a returned candle is one that just closed → decision time.
            var closed = _aggregator.OnTick(tick, _timeframe);
            if (closed is not null)
                await OnCandleClosedAsync(closed, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Fills the instrument's resting simulated order (if any) at the observed price and books it in the ledger.</summary>
    private async Task TryFillPendingAsync(Tick tick, CancellationToken cancellationToken)
    {
        if (!_pending.TryGetValue(tick.InstrumentToken, out var pending))
            return;

        var fillPrice = tick.LastPrice;
        if (fillPrice <= 0m)
            return; // Never fabricate or coerce a price; wait for a valid tick.

        var result = await PaperFillAsync(pending.OrderId, fillPrice, cancellationToken).ConfigureAwait(false);
        if (result.State != OrderState.Filled)
        {
            // A resting market order fills on the first valid price. Any other outcome means our tracking is
            // stale (already terminal); drop it so the instrument is not stuck permanently in-flight.
            _pending.Remove(tick.InstrumentToken);
            _logger.LogWarning(
                "Paper fill for order {OrderId} ({Symbol}) returned state {State}; clearing pending tracking. {Message}",
                pending.OrderId, pending.Symbol, result.State, result.Message);
            return;
        }

        _pending.Remove(tick.InstrumentToken);

        if (pending.Side == OrderSide.Buy)
        {
            _portfolio.RecordEntryFill(new PaperEntryFill(
                pending.InstrumentToken, pending.Symbol, _exchange, pending.StrategyName, _product,
                pending.Quantity, fillPrice, tick.TimestampUtc, pending.StopPrice, pending.TargetPrice, pending.CorrelationId));
            _logger.LogInformation(
                "Paper ENTRY filled: {Qty} {Symbol} @ {Price} ({CorrelationId}).",
                pending.Quantity, pending.Symbol, fillPrice, pending.CorrelationId);
        }
        else
        {
            _portfolio.RecordExitFill(pending.InstrumentToken, fillPrice, tick.TimestampUtc);
            _logger.LogInformation(
                "Paper EXIT filled: {Qty} {Symbol} @ {Price} ({CorrelationId}).",
                pending.Quantity, pending.Symbol, fillPrice, pending.CorrelationId);
        }
    }

    /// <summary>Runs the strategy on the decision candle and acts on each resulting signal.</summary>
    private async Task OnCandleClosedAsync(Candle closed, CancellationToken cancellationToken)
    {
        var token = closed.InstrumentToken;
        _metrics.CandleClosed(TradingMode.Paper);
        var history = AppendHistory(token, closed);

        // Decision time is the bar's close (its start plus one interval) — the instant the information is available.
        var decisionTimeUtc = closed.TimestampUtc.AddMinutes(_timeframe.Minutes());
        var snapshot = _portfolio.Snapshot(decisionTimeUtc);
        var openPosition = _portfolio.GetOpenPosition(token);

        var context = new StrategyContext
        {
            Symbol = closed.Symbol,
            InstrumentToken = token,
            Timeframe = _timeframe,
            Candles = history,
            OpenPosition = openPosition,
            CurrentTimestampUtc = closed.TimestampUtc,
            AvailableCapital = snapshot.Cash
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

        foreach (var signal in signals)
            await HandleSignalAsync(signal, closed, snapshot, openPosition, decisionTimeUtc, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSignalAsync(
        Signal signal, Candle closed, PaperPortfolioSnapshot snapshot, OpenPosition? openPosition,
        DateTimeOffset decisionTimeUtc, CancellationToken cancellationToken)
    {
        var token = signal.InstrumentToken;

        // Thread the signal's correlation id (and instrument) through every log emitted while acting on it.
        using var logScope = _logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = signal.CorrelationId,
            ["Symbol"] = signal.Symbol,
            ["Strategy"] = signal.StrategyName
        });
        _metrics.SignalGenerated(TradingMode.Paper, signal.StrategyName, signal.Direction);

        // At most one in-flight order per instrument: never stack orders while a fill is pending.
        if (_pending.ContainsKey(token))
        {
            _logger.LogDebug("Skipping {Direction} for {Symbol}: an order is already in flight.", signal.Direction, signal.Symbol);
            return;
        }

        var riskContext = new RiskEvaluationContext(
            TimestampUtc: decisionTimeUtc,
            AvailableCapital: snapshot.Cash,
            RealizedPnlToday: snapshot.RealizedPnlToday,
            TradesToday: snapshot.TradesToday,
            OpenPositionCount: snapshot.OpenPositions.Count,
            OpenOrderCount: _pending.Count,
            SymbolsWithOpenPositions: snapshot.SymbolsWithOpenPositions,
            IsMarketDataStale: false,   // the decision is driven by a candle that just closed from a fresh tick
            IsBrokerConnected: true);   // paper: the simulator is always available

        if (signal.Direction == SignalDirection.LongEntry)
            await HandleEntryAsync(signal, closed, snapshot, openPosition, riskContext, cancellationToken).ConfigureAwait(false);
        else
            await HandleExitAsync(signal, openPosition, riskContext, cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleEntryAsync(
        Signal signal, Candle closed, PaperPortfolioSnapshot snapshot, OpenPosition? openPosition,
        RiskEvaluationContext riskContext, CancellationToken cancellationToken)
    {
        if (openPosition is not null)
        {
            _logger.LogDebug("Skipping entry for {Symbol}: a position is already open.", signal.Symbol);
            return;
        }

        var decision = await _risk.EvaluateSignalAsync(signal, riskContext, cancellationToken).ConfigureAwait(false);
        if (!decision.IsApproved)
        {
            _metrics.RiskRejected(TradingMode.Paper, decision.Reason);
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
            AvailableCapital: snapshot.Cash,
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

        var result = await SubmitAsync(request, cancellationToken).ConfigureAwait(false);
        if (!TryTrackResting(result, signal, OrderSide.Buy, size.Quantity, stop, signal.TargetPrice))
            return;

        _logger.LogInformation(
            "Paper ENTRY resting: {Qty} {Symbol} (stop {Stop}, target {Target}); fills on next tick.",
            size.Quantity, signal.Symbol, stop, signal.TargetPrice);
    }

    private async Task HandleExitAsync(
        Signal signal, OpenPosition? openPosition, RiskEvaluationContext riskContext, CancellationToken cancellationToken)
    {
        if (openPosition is null)
        {
            _logger.LogDebug("Skipping exit for {Symbol}: no open position.", signal.Symbol);
            return;
        }

        var decision = await _risk.EvaluateSignalAsync(signal, riskContext, cancellationToken).ConfigureAwait(false);
        if (!decision.IsApproved)
        {
            // Exits are only blocked by a system-integrity failure (kill switch / broker down); emergency
            // square-off is a separate flow. Surface it and leave the position for that flow to handle.
            _metrics.RiskRejected(TradingMode.Paper, decision.Reason);
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

        var result = await SubmitAsync(request, cancellationToken).ConfigureAwait(false);
        if (!TryTrackResting(result, signal, OrderSide.Sell, openPosition.Quantity, stopPrice: null, targetPrice: null))
            return;

        _logger.LogInformation("Paper EXIT resting: {Qty} {Symbol}; fills on next tick.", openPosition.Quantity, signal.Symbol);
    }

    /// <summary>
    /// Records a just-submitted paper order as the instrument's single in-flight order when the engine accepted it
    /// and it is resting (<see cref="OrderState.Open"/>). Any other accepted state is unexpected for a paper market
    /// order and is not tracked (it cannot be filled by price), so it is logged and dropped.
    /// </summary>
    private bool TryTrackResting(
        ExecutionResult result, Signal signal, OrderSide side, int quantity, decimal? stopPrice, decimal? targetPrice)
    {
        if (!result.IsAccepted)
        {
            _logger.LogWarning("Execution rejected {Side} for {Symbol}: {Message}.", side, signal.Symbol, result.Message);
            return false;
        }

        if (result.State != OrderState.Open)
        {
            _logger.LogWarning(
                "Paper {Side} order for {Symbol} is in state {State}, not resting; not tracking for a price-fed fill.",
                side, signal.Symbol, result.State);
            return false;
        }

        _pending[signal.InstrumentToken] = new PendingOrder(
            result.OrderId, signal.InstrumentToken, side, quantity, signal.Symbol,
            signal.StrategyName, stopPrice, targetPrice, signal.CorrelationId);
        return true;
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

    private async Task<ExecutionResult> SubmitAsync(OrderRequest request, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IExecutionEngine>();
        return await engine.SubmitAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ExecutionResult> PaperFillAsync(long orderId, decimal fillPrice, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var engine = scope.ServiceProvider.GetRequiredService<IExecutionEngine>();
        return await engine.ApplyPaperFillAsync(orderId, fillPrice, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _gate.Dispose();

    /// <summary>An accepted-but-not-yet-filled simulated order and the metadata needed to book its fill.</summary>
    private sealed record PendingOrder(
        long OrderId,
        int InstrumentToken,
        OrderSide Side,
        int Quantity,
        string Symbol,
        string StrategyName,
        decimal? StopPrice,
        decimal? TargetPrice,
        string CorrelationId);
}
