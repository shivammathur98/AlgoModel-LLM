namespace AlgoTrader.Execution;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Application.Execution;
using AlgoTrader.Application.Observability;
using AlgoTrader.Application.Repositories;
using AlgoTrader.Application.Safety;
using AlgoTrader.Domain.Broker;
using AlgoTrader.Domain.Common;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Execution;
using AlgoTrader.Domain.Orders;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IExecutionEngine"/> (Â§8, Â§11, Â§25). Turns a risk-approved <see cref="OrderRequest"/>
/// into a persisted <see cref="Order"/> and drives it through the Â§25 state machine.
/// <para>
/// <b>Safety is the invariant.</b> A real broker order is transmitted only when
/// <see cref="TradingMode.Live"/> is active AND <see cref="LiveTradingSafetyValidator"/> confirms all three
/// live gates (Â§6, Â§36) AND the <see cref="IKillSwitch"/> is disengaged â€” all re-checked at the moment of
/// transmission. If the mode is Live but any gate fails or the kill switch is engaged, the order is rejected
/// â€” never sent. Every simulated mode (Backtest / Paper) fills locally without ever calling the broker;
/// Research permits no execution at all. The engine depends only on the <see cref="ITradingBroker"/>
/// abstraction, so no broker-specific type leaks into execution.
/// </para>
/// <para>
/// Simulated fills price honestly: a limit-priced order (Limit / StopLossLimit) fills at its own limit
/// price; a market order (Market / StopLoss) carries no price, so it is accepted as a resting
/// <see cref="OrderState.Open"/> order and later filled by <see cref="ApplyPaperFillAsync"/> at a
/// caller-supplied observed price. Asynchronous broker fill/cancel updates
/// (<see cref="ITradingBroker.OrderUpdated"/>) are reconciled by <see cref="ApplyBrokerUpdateAsync"/>,
/// which the live trading loop wires to the broker event; the engine owns submission, the safety gate,
/// and the local Â§25 lifecycle throughout.
/// </para>
/// </summary>
public sealed class OrderExecutionEngine : IExecutionEngine
{
    private const int DefaultReconcileResolveAttempts = 20;

    private readonly TradingSettings _trading;
    private readonly ITradingBroker _broker;
    private readonly LiveTradingSafetyValidator _safety;
    private readonly IKillSwitch _killSwitch;
    private readonly IOrderRepository _orders;
    private readonly IOrderMutationGate _mutationGate;
    private readonly ISystemClock _clock;
    private readonly ILogger<OrderExecutionEngine> _logger;
    private readonly ITradingMetrics _metrics;
    private readonly int _reconcileResolveAttempts;
    private readonly TimeSpan _reconcileResolveDelay;

    public OrderExecutionEngine(
        TradingSettings trading,
        ITradingBroker broker,
        LiveTradingSafetyValidator safety,
        IKillSwitch killSwitch,
        IOrderRepository orders,
        IOrderMutationGate mutationGate,
        ISystemClock clock,
        ILogger<OrderExecutionEngine> logger,
        ITradingMetrics? metrics = null,
        int reconcileResolveAttempts = DefaultReconcileResolveAttempts,
        TimeSpan? reconcileResolveDelay = null)
    {
        _trading = trading ?? throw new ArgumentNullException(nameof(trading));
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _safety = safety ?? throw new ArgumentNullException(nameof(safety));
        _killSwitch = killSwitch ?? throw new ArgumentNullException(nameof(killSwitch));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _mutationGate = mutationGate ?? throw new ArgumentNullException(nameof(mutationGate));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics ?? NullTradingMetrics.Instance;
        _reconcileResolveAttempts = reconcileResolveAttempts >= 0
            ? reconcileResolveAttempts
            : throw new ArgumentOutOfRangeException(nameof(reconcileResolveAttempts));
        _reconcileResolveDelay = reconcileResolveDelay ?? TimeSpan.FromMilliseconds(25);
    }

    /// <inheritdoc />
    public async Task<ExecutionResult> SubmitAsync(OrderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), request.Quantity, "Order quantity must be positive.");

        var order = await PersistNewAsync(request, cancellationToken).ConfigureAwait(false);
        _metrics.OrderSubmitted(_trading.Mode, order.Side, order.Type);

        // Research does no execution at all (Â§6). Fail closed.
        if (_trading.Mode == TradingMode.Research)
            return await RejectAsync(order, "Execution is not permitted in Research mode.", cancellationToken).ConfigureAwait(false);

        return _trading.Mode.AllowsRealOrders()
            ? await RouteLiveAsync(order, request, cancellationToken).ConfigureAwait(false)
            : await SimulateAsync(order, request, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<ExecutionResult> CancelAsync(long orderId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var order = await _orders.GetByIdAsync(orderId, cancellationToken).ConfigureAwait(false);
        if (order is null)
            return new ExecutionResult(false, orderId, OrderState.New, Message: "Order not found.");

        if (order.IsTerminal)
            return Result(order, accepted: false, "Order is already terminal; nothing to cancel.");

        // A broker order id exists only for a fully-gated live order, so this path needs no re-gating of the
        // three live-safety gates. It does, however, share the order row with asynchronous fill postbacks: send
        // the broker cancel (network I/O, outside the gate), then take the mutation gate and RE-READ the order
        // under it so a cancel cannot clobber a fill that completed first (AUDIT-0009). If the re-read shows the
        // order already reached a terminal state, leave it â€” the broker cancel is a harmless no-op against a
        // filled/cancelled order, but overwriting a real fill with CancelPending would blind risk accounting.
        if (order.BrokerOrderId is { } brokerOrderId)
        {
            try
            {
                await _broker.CancelOrderAsync(brokerOrderId, cancellationToken).ConfigureAwait(false);
            }
            catch (System.Net.Http.HttpRequestException ex)
            {
                _logger.LogWarning(ex, "Broker rejected cancel for {BrokerOrderId}; it may already be completed.", brokerOrderId);
            }
            using (await _mutationGate.AcquireAsync(cancellationToken).ConfigureAwait(false))
            {
                var current = await _orders.GetByBrokerIdAsync(brokerOrderId, cancellationToken).ConfigureAwait(false) ?? order;
                if (current.IsTerminal)
                {
                    _logger.LogInformation(
                        "Cancel for {CorrelationId}: order already {State}; broker cancel sent, local state left intact.",
                        current.CorrelationId, current.State);
                    return Result(current, accepted: false, $"Order already {current.State}; nothing to cancel locally.");
                }

                Transition(current, OrderState.CancelPending);
                await UpdateAsync(current, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Cancel requested {CorrelationId}: broker order {BrokerOrderId}", current.CorrelationId, brokerOrderId);
                return Result(current, accepted: true, "Cancellation requested from broker.");
            }
        }

        // Simulated order: cancel locally, respecting the Â§25 machine (Open must pass through CancelPending).
        if (order.State == OrderState.Open)
            Transition(order, OrderState.CancelPending);
        Transition(order, OrderState.Cancelled);
        await UpdateAsync(order, cancellationToken).ConfigureAwait(false);
        return Result(order, accepted: true, "Simulated order cancelled.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// Reconciliation runs concurrently with itself and with submission: the live loop dispatches each broker
    /// postback fire-and-forget on its own DI scope / <c>DbContext</c>, none of which hold the trading-cycle
    /// gate. Two hazards follow, both closed here via <see cref="IOrderMutationGate"/> (AUDIT-0009):
    /// <list type="bullet">
    /// <item><b>Last-writer-wins (Race 2).</b> The order is resolved and mutated entirely INSIDE the gate, so two
    /// postbacks for one order are serialized and the second validates its transition against the first's
    /// committed state â€” a stale <c>Open</c> arriving after a <c>Filled</c> re-reads <c>Filled</c> and is rejected
    /// as an illegal transition rather than reverting the fill.</item>
    /// <item><b>Dropped fill (Race 1).</b> A fast fill can arrive before <see cref="RouteLiveAsync"/> has
    /// committed the <c>BrokerOrderId</c>. Resolution is retried a bounded number of times â€” releasing the gate
    /// between attempts so the concurrent submission can commit the id â€” before the order is declared untracked,
    /// so an as-yet-unpersisted order's fill is never silently dropped.</item>
    /// </list>
    /// The gate is never held across broker network I/O.
    /// </remarks>
    public async Task<ExecutionResult> ApplyBrokerUpdateAsync(BrokerOrderUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();

        for (var attempt = 0; ; attempt++)
        {
            using (await _mutationGate.AcquireAsync(cancellationToken).ConfigureAwait(false))
            {
                // Re-read under the gate: the snapshot must reflect any write a concurrent postback just committed.
                var order = await _orders.GetByBrokerIdAsync(update.BrokerOrderId, cancellationToken).ConfigureAwait(false);
                if (order is not null)
                    return await ApplyResolvedUpdateAsync(order, update, cancellationToken).ConfigureAwait(false);
            }

            // Not found yet. The order may still be mid-submission (id not committed) â€” retry within a bounded
            // window, gate released, before concluding it is genuinely untracked (Race 1). Fail closed after that.
            if (attempt >= _reconcileResolveAttempts)
            {
                _logger.LogWarning(
                    "Ignoring broker update {State} for untracked order {BrokerOrderId} after {Attempts} resolve attempts",
                    update.State, update.BrokerOrderId, attempt + 1);
                return new ExecutionResult(false, 0, OrderState.New, update.BrokerOrderId, "No local order for that broker id.");
            }

            await Task.Delay(_reconcileResolveDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies a resolved broker update to <paramref name="order"/>. The caller MUST hold
    /// <see cref="IOrderMutationGate"/> and have read <paramref name="order"/> under it, so the transition is
    /// validated against fresh state and the write cannot be clobbered by a concurrent reconciliation.
    /// </summary>
    private async Task<ExecutionResult> ApplyResolvedUpdateAsync(Order order, BrokerOrderUpdate update, CancellationToken cancellationToken)
    {
        // External updates may arrive stale, duplicated, or out of sequence: never throw, just skip.
        if (!order.State.IsValidTransition(update.State))
        {
            _logger.LogInformation("Ignoring out-of-sequence broker update {From}->{To} for {CorrelationId}",
                order.State, update.State, order.CorrelationId);
            return Result(order, accepted: false, $"Ignored update to {update.State} from {order.State}.");
        }

        order.TryTransitionTo(update.State);

        // The broker is authoritative for fill progress, which is CUMULATIVE and monotonic per order (Kite reports
        // running totals, not per-fill deltas). Postbacks are best-effort and may arrive out of order or duplicated,
        // and PartiallyFilled->PartiallyFilled is a legal self-transition â€” so an unconditional overwrite could
        // REWIND a fill (e.g. a delayed "filled 3" landing after "filled 6"), dragging the blended average with it.
        // Advance fill progress, never regress it (Â§20); a stale cancel/reject reporting fewer fills likewise cannot
        // erase a real partial fill. Quantity and its blended average move together so they never disagree.
        if (update.FilledQuantity >= order.FilledQuantity)
        {
            order.FilledQuantity = update.FilledQuantity;
            if (update.AverageFillPrice is { } avg)
                order.AverageFillPrice = avg;
        }
        if (update.State == OrderState.Filled)
            order.FilledAtUtc = update.TimestampUtc;
        if (update.State is OrderState.Rejected or OrderState.Failed)
            order.RejectionReason = update.StatusMessage;

        await UpdateAsync(order, cancellationToken).ConfigureAwait(false);
        if (update.State == OrderState.Filled)
            _metrics.OrderFilled(_trading.Mode, order.Side);
        else if (update.State is OrderState.Rejected or OrderState.Failed)
            _metrics.OrderRejected(_trading.Mode, order.Side);
        _logger.LogInformation("Applied broker update {State} to {CorrelationId} (filled {Filled}/{Total})",
            update.State, order.CorrelationId, order.FilledQuantity, order.Quantity);
        return Result(order, accepted: true, update.StatusMessage);
    }

    /// <inheritdoc />
    public async Task<ExecutionResult> ApplyPaperFillAsync(long orderId, decimal fillPrice, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (fillPrice <= 0m)
            throw new ArgumentOutOfRangeException(nameof(fillPrice), fillPrice, "Fill price must be positive.");

        var order = await _orders.GetByIdAsync(orderId, cancellationToken).ConfigureAwait(false);
        if (order is null)
            return new ExecutionResult(false, orderId, OrderState.New, Message: "Order not found.");

        // Only a resting simulated market order (Open, no broker id) is eligible.
        if (order.State != OrderState.Open)
            return Result(order, accepted: false, $"Order is not resting (state {order.State}); nothing to fill.");

        Transition(order, OrderState.Filled);
        order.FilledQuantity = order.Quantity;
        order.AverageFillPrice = fillPrice;
        order.FilledAtUtc = _clock.UtcNow;
        await UpdateAsync(order, cancellationToken).ConfigureAwait(false);
        _metrics.OrderFilled(_trading.Mode, order.Side);
        return Result(order, accepted: true, "Simulated market fill at observed price.");
    }

    private async Task<Order> PersistNewAsync(OrderRequest request, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var order = new Order
        {
            InstrumentToken = request.InstrumentToken,
            Symbol = request.Symbol,
            Exchange = request.Exchange,
            Side = request.Side,
            Type = request.Type,
            Validity = request.Validity,
            Product = request.Product,
            Quantity = request.Quantity,
            Price = request.Price,
            TriggerPrice = request.TriggerPrice,
            State = OrderState.New,
            Tag = request.Tag,
            CorrelationId = request.CorrelationId,
            StrategyName = request.StrategyName,
            CreatedAtUtc = now,
            LastUpdatedAtUtc = now
        };
        return await _orders.AddAsync(order, cancellationToken).ConfigureAwait(false);
    }

    private async Task<ExecutionResult> RouteLiveAsync(Order order, OrderRequest request, CancellationToken cancellationToken)
    {
        // Emergency stop takes precedence over everything and is re-read HERE, at the transmit boundary â€” not
        // only at the caller's once-per-candle risk gate. The kill switch can be engaged out-of-band (an operator
        // or a monitor on another thread) after risk approved this order but before we transmit; without this
        // re-check that order would still reach the broker. This is the authoritative point where a real order is
        // accepted, so it is where "while engaged, no new orders may be accepted" must hold (Â§6, Â§15, Â§18).
        if (_killSwitch.IsEngaged)
        {
            var killReason = "Kill switch engaged: " + (_killSwitch.Reason ?? "no reason recorded");
            _logger.LogError("Refusing to transmit live order {CorrelationId}: {Reason}", order.CorrelationId, killReason);
            return await RejectAsync(order, killReason, cancellationToken).ConfigureAwait(false);
        }

        // Final, authoritative live-safety check at the moment of transmission (Â§6, Â§36).
        var validation = _safety.ValidateForLiveTrading(_trading);
        if (!validation.IsValid)
        {
            var reason = "Live safety validation failed: " + string.Join("; ", validation.Failures);
            _logger.LogError("Refusing to transmit live order {CorrelationId}: {Reason}", order.CorrelationId, reason);
            return await RejectAsync(order, reason, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning("Transmitting LIVE order {CorrelationId}: {Side} {Quantity} {Symbol}",
            order.CorrelationId, request.Side, request.Quantity, request.Symbol);

        PlaceOrderResult placement;
        try
        {
            placement = await _broker.PlaceOrderAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Genuine caller cancellation (host shutdown) â€” not an uncertain submission.
        }
        catch (Exception ex)
        {
            // Defense in depth: a broker adapter should surface an ambiguous failure as IsUncertain rather than
            // throw, but if it does throw we still must NOT assume the order was never placed (Â§20, Rules #8/#9).
            return await HandleUncertainAsync(order, "Order submission threw: " + ex.Message, ex, cancellationToken).ConfigureAwait(false);
        }

        // An UNCERTAIN outcome must never be treated as a definitive rejection: the order may be live at the
        // broker. Mark it Failed (not Rejected) and STOP NEW TRADING via the kill switch until an operator
        // reconciles (Rule #5 "prefer STOP over CONTINUE BLINDLY"; Rule #9 "never blindly retry an uncertain
        // submission"). Rejecting here would erase the in-flight record and let the next candle re-submit a
        // duplicate real order with no broker-status check.
        if (placement.IsUncertain)
            return await HandleUncertainAsync(order,
                placement.ErrorMessage ?? "Uncertain broker submission.", null, cancellationToken).ConfigureAwait(false);

        if (!placement.IsSuccess)
            return await RejectAsync(order, placement.ErrorMessage ?? "Broker rejected the order.", cancellationToken).ConfigureAwait(false);

        // Publish the broker id and mark Submitted UNDER the mutation gate so this commit is ordered against any
        // concurrent fill postback (AUDIT-0009 Race 1): reconciliation cannot resolve â€” and therefore cannot act
        // on â€” this order until the id is visible, and a postback that raced in first is retried until it is.
        using (await _mutationGate.AcquireAsync(cancellationToken).ConfigureAwait(false))
        {
            order.BrokerOrderId = placement.BrokerOrderId;
            Transition(order, OrderState.Submitted);
            await UpdateAsync(order, cancellationToken).ConfigureAwait(false);
        }

        return Result(order, accepted: true, "Order submitted to broker.");
    }

    /// <summary>
    /// Handles an ambiguous live submission (transport failure, timeout, 5xx, or a success with an unreadable
    /// order id): the order may be live at the broker, so it is moved to the terminal <see cref="OrderState.Failed"/>
    /// (connectivity/timeout state, Â§25) â€” <b>not</b> <see cref="OrderState.Rejected"/>, which would assert the
    /// broker refused it â€” and the kill switch is engaged so no further real orders transmit until an operator
    /// reconciles and resets (Safety Rules #5, #8, #9; Â§15, Â§20).
    /// </summary>
    private async Task<ExecutionResult> HandleUncertainAsync(Order order, string reason, Exception? ex, CancellationToken cancellationToken)
    {
        var detail = "Uncertain submission â€” the order may be live at the broker; reconcile before any retry. " + reason;
        if (ex != null)
        {
            _logger.LogError(ex, "UNCERTAIN live submission for {CorrelationId}: {Reason}. Engaging kill switch (STOP NEW TRADING).", order.CorrelationId, reason);
        }
        else
        {
            _logger.LogError("UNCERTAIN live submission for {CorrelationId}: {Reason}. Engaging kill switch (STOP NEW TRADING).", order.CorrelationId, reason);
        }

        Transition(order, OrderState.Failed);
        order.RejectionReason = detail;
        await UpdateAsync(order, cancellationToken).ConfigureAwait(false);
        _metrics.OrderRejected(_trading.Mode, order.Side);

        // A live order whose outcome we cannot determine must halt new trading until an operator reconciles it.
        _killSwitch.Engage(detail, initiatedBy: "execution");
        return Result(order, accepted: false, detail);
    }

    private async Task<ExecutionResult> SimulateAsync(Order order, OrderRequest request, CancellationToken cancellationToken)
    {
        Transition(order, OrderState.Submitted);

        // All simulated orders (market and limit) rest as Open until a price-fed paper fill closes them.
        // A limit order must wait for the market to cross its price; a market order waits for the very next tick.
        Transition(order, OrderState.Open);
        await UpdateAsync(order, cancellationToken).ConfigureAwait(false);
        return Result(order, accepted: true, "Simulated order resting (awaiting price-fed fill).");
    }

    private async Task<ExecutionResult> RejectAsync(Order order, string reason, CancellationToken cancellationToken)
    {
        Transition(order, OrderState.Rejected);
        order.RejectionReason = reason;
        await UpdateAsync(order, cancellationToken).ConfigureAwait(false);
        _metrics.OrderRejected(_trading.Mode, order.Side);
        return Result(order, accepted: false, reason);
    }

    private void Transition(Order order, OrderState newState)
    {
        if (!order.TryTransitionTo(newState))
        {
            // Should be unreachable given the paths above; guard so an illegal transition is loud, not silent.
            _logger.LogError("Illegal order transition {From}->{To} for {CorrelationId}", order.State, newState, order.CorrelationId);
            throw new InvalidOperationException($"Illegal order transition {order.State}->{newState}.");
        }

        order.LastUpdatedAtUtc = _clock.UtcNow;
    }

    private Task UpdateAsync(Order order, CancellationToken cancellationToken)
    {
        order.LastUpdatedAtUtc = _clock.UtcNow;
        return _orders.UpdateAsync(order, cancellationToken);
    }

    private static ExecutionResult Result(Order order, bool accepted, string? message) =>
        new(accepted, order.Id, order.State, order.BrokerOrderId, message);
}



