namespace AlgoTrader.Risk;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Application.Safety;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.MarketData;
using AlgoTrader.Domain.Orders;
using AlgoTrader.Domain.Risk;
using AlgoTrader.Domain.Trading;
using Microsoft.Extensions.Logging;

/// <summary>
/// The pre-trade risk authority (§14, §15). Every strategy signal and every concrete order must pass
/// through here before the execution engine may act; only an <see cref="RiskDecision.Approved"/> verdict
/// lets an item proceed. The engine is deterministic and stateless — the caller supplies a
/// <see cref="RiskEvaluationContext"/> snapshot, so this class never queries portfolio state itself.
/// <para>
/// Two ideas shape which rule applies to which item:
/// </para>
/// <list type="number">
/// <item>
/// <b>System-integrity gates</b> (kill switch engaged, broker disconnected, a malformed request) are hard
/// blocks for <i>everything</i>, including exits — if the platform is halted or cannot transmit, nothing
/// may be sent through the normal path (emergency square-off is a separate, dedicated flow).
/// </item>
/// <item>
/// <b>Risk-budget / market-condition gates</b> (daily-loss halt, trades-per-day, simultaneous positions,
/// symbol already open, capital, funds, session hours, stale data) apply only to <i>risk-increasing</i>
/// actions — long entries and buy orders. A long exit / sell reduces exposure and must not be trapped by
/// a budget limit; otherwise a daily-loss halt could prevent closing the very position causing the loss.
/// This platform is long-only, so entry = <see cref="SignalDirection.LongEntry"/> / <see cref="OrderSide.Buy"/>.
/// </item>
/// </list>
/// <para>
/// The engine enforces exactly the rules its inputs can support honestly. Checks that require data not in
/// <see cref="RiskEvaluationContext"/> — duplicate-order detection (needs the live order book),
/// broker/local position reconciliation (<see cref="RiskRejectionReason.PositionMismatch"/>), and exact
/// per-symbol exposure or entry-to-stop risk sizing — are owned elsewhere (the position sizer enforces
/// per-trade risk and exposure during sizing) and are deliberately not faked here.
/// </para>
/// </summary>
public sealed class RiskEngine : IRiskEngine
{
    /// <summary>IST is UTC+05:30; session-hours gating is evaluated in IST.</summary>
    private static readonly TimeSpan IndiaStandardTimeOffset = TimeSpan.FromHours(5.5);

    private readonly RiskSettings _settings;
    private readonly IKillSwitch _killSwitch;
    private readonly ILastPriceCache _lastPrices;
    private readonly ILogger<RiskEngine> _logger;

    public RiskEngine(RiskSettings settings, IKillSwitch killSwitch, ILastPriceCache lastPrices, ILogger<RiskEngine> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _killSwitch = killSwitch ?? throw new ArgumentNullException(nameof(killSwitch));
        _lastPrices = lastPrices ?? throw new ArgumentNullException(nameof(lastPrices));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public Task<RiskDecision> EvaluateSignalAsync(Signal signal, RiskEvaluationContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (signal is null)
            return Task.FromResult(Reject(RiskRejectionReason.InvalidRequest, "Signal was null.", correlationId: null));
        ArgumentNullException.ThrowIfNull(context);

        var isEntry = signal.Direction == SignalDirection.LongEntry;

        // System-integrity gates (apply to entries and exits alike).
        if (EvaluateSystemIntegrity(context, signal.CorrelationId) is { } hardBlock)
            return Task.FromResult(hardBlock);

        // Risk-budget / market-condition gates apply only to risk-increasing entries.
        if (isEntry && EvaluateEntryBudget(context, signal.Symbol, signal.CorrelationId) is { } budgetBlock)
            return Task.FromResult(budgetBlock);

        return Task.FromResult(RiskDecision.Approved());
    }

    /// <inheritdoc />
    public Task<RiskDecision> EvaluateOrderAsync(OrderRequest order, RiskEvaluationContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (order is null)
            return Task.FromResult(Reject(RiskRejectionReason.InvalidRequest, "Order was null.", correlationId: null));
        ArgumentNullException.ThrowIfNull(context);

        if (order.Quantity <= 0)
            return Task.FromResult(Reject(RiskRejectionReason.InvalidRequest, "Order quantity must be positive.", order.CorrelationId));

        // System-integrity gates (apply to buys and sells alike).
        if (EvaluateSystemIntegrity(context, order.CorrelationId) is { } hardBlock)
            return Task.FromResult(hardBlock);

        // Only buys increase risk; sells reduce exposure and pass once system integrity holds.
        if (order.Side != OrderSide.Buy)
            return Task.FromResult(RiskDecision.Approved());

        if (EvaluateEntryBudget(context, order.Symbol, order.CorrelationId) is { } budgetBlock)
            return Task.FromResult(budgetBlock);

        var price = order.Price ?? _lastPrices.Get(order.InstrumentToken)?.Price ?? 0m;
        
        if (price > 0m)
        {
            var notional = price * order.Quantity;

            if (notional > _settings.MaxCapitalPerTrade)
                return Task.FromResult(Reject(RiskRejectionReason.MaxCapitalUtilizationBreached,
                    $"Order notional {notional} exceeds MaxCapitalPerTrade {_settings.MaxCapitalPerTrade}.", order.CorrelationId));

            if (notional > context.AvailableCapital)
                return Task.FromResult(Reject(RiskRejectionReason.InsufficientFunds,
                    $"Order notional {notional} exceeds available capital {context.AvailableCapital}.", order.CorrelationId));
        }

        return Task.FromResult(RiskDecision.Approved());
    }

    /// <summary>Hard blocks that stop all traffic regardless of direction. Returns null when clear.</summary>
    private RiskDecision? EvaluateSystemIntegrity(RiskEvaluationContext context, string? correlationId)
    {
        if (_killSwitch.IsEngaged)
            return Reject(RiskRejectionReason.KillSwitchActive, _killSwitch.Reason ?? "Kill switch engaged.", correlationId);

        if (!context.IsBrokerConnected)
            return Reject(RiskRejectionReason.BrokerDisconnected, "Broker connection is down.", correlationId);

        return null;
    }

    /// <summary>Risk-budget and market-condition gates for entries / buys. Returns null when clear.</summary>
    private RiskDecision? EvaluateEntryBudget(RiskEvaluationContext context, string symbol, string? correlationId)
    {
        if (context.IsMarketDataStale)
            return Reject(RiskRejectionReason.MarketDataStale, "Market data is stale; refusing to open new risk.", correlationId);

        if (IsOutsideTradingHours(context.TimestampUtc))
            return Reject(RiskRejectionReason.OutsideTradingHours,
                $"Timestamp {context.TimestampUtc:o} is outside the trading session.", correlationId);

        // Total P&L (Realized + Unrealized) is negative for a loss; a breach is a loss at or beyond the configured magnitude.
        var totalPnl = context.RealizedPnlToday + context.UnrealizedPnl;
        if (totalPnl <= -_settings.MaxDailyLoss)
            return Reject(RiskRejectionReason.MaxDailyLossBreached,
                $"Total daily loss {totalPnl} (Realized: {context.RealizedPnlToday}, Unrealized: {context.UnrealizedPnl}) breached limit {_settings.MaxDailyLoss}.", correlationId);

        var totalTrades = context.TradesToday + context.OpenOrderCount;
        if (totalTrades >= _settings.MaxTradesPerDay)
            return Reject(RiskRejectionReason.MaxTradesPerDayBreached,
                $"Trades today ({context.TradesToday} filled + {context.OpenOrderCount} pending) reached limit {_settings.MaxTradesPerDay}.", correlationId);

        var totalPositions = context.OpenPositionCount + context.OpenOrderCount;
        if (totalPositions >= _settings.MaxSimultaneousPositions)
            return Reject(RiskRejectionReason.MaxSimultaneousPositionsBreached,
                $"Open positions ({context.OpenPositionCount} filled + {context.OpenOrderCount} pending) reached limit {_settings.MaxSimultaneousPositions}.", correlationId);

        if (context.SymbolsWithOpenPositions.Contains(symbol))
            return Reject(RiskRejectionReason.SymbolAlreadyOpen, $"A position in {symbol} is already open.", correlationId);

        return null;
    }

    private bool IsOutsideTradingHours(DateTimeOffset timestampUtc)
    {
        var ist = TimeOnly.FromDateTime(timestampUtc.ToOffset(IndiaStandardTimeOffset).DateTime);
        var start = _settings.GetTradingSessionStartIst();
        var end = _settings.GetTradingSessionEndIst();
        return ist < start || ist >= end;
    }

    private RiskDecision Reject(RiskRejectionReason reason, string detail, string? correlationId)
    {
        // Risk telemetry only — no credentials or tokens are ever logged (§security).
        _logger.LogWarning("Risk rejected {CorrelationId}: {Reason} — {Detail}", correlationId ?? "n/a", reason, detail);
        return RiskDecision.Rejected(reason, detail);
    }
}
