namespace AlgoTrader.Execution;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Application.Repositories;
using AlgoTrader.Application.Safety;
using AlgoTrader.Domain.Broker;
using AlgoTrader.Domain.Common;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Execution;
using AlgoTrader.Domain.Orders;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IExecutionEngine"/> (§8, §11, §25). Turns a risk-approved <see cref="OrderRequest"/>
/// into a persisted <see cref="Order"/> and drives it through the §25 state machine.
/// <para>
/// <b>Safety is the invariant.</b> A real broker order is transmitted only when
/// <see cref="TradingMode.Live"/> is active AND <see cref="LiveTradingSafetyValidator"/> confirms all three
/// live gates (§6, §36). If the mode is Live but any gate fails, the order is rejected — never sent. Every
/// simulated mode (Backtest / Paper) fills locally without ever calling the broker; Research permits no
/// execution at all. The engine depends only on the <see cref="ITradingBroker"/> abstraction, so no
/// broker-specific type leaks into execution.
/// </para>
/// <para>
/// Simulated fills price honestly: a limit-priced order (Limit / StopLossLimit) fills at its own limit
/// price; a market order (Market / StopLoss) carries no price, so it is accepted as a resting
/// <see cref="OrderState.Open"/> order and later filled by <see cref="ApplyPaperFillAsync"/> at a
/// caller-supplied observed price. Asynchronous broker fill/cancel updates
/// (<see cref="ITradingBroker.OrderUpdated"/>) are reconciled by <see cref="ApplyBrokerUpdateAsync"/>,
/// which the live trading loop wires to the broker event; the engine owns submission, the safety gate,
/// and the local §25 lifecycle throughout.
/// </para>
/// </summary>
public sealed class OrderExecutionEngine : IExecutionEngine
{
    private readonly TradingSettings _trading;
    private readonly ITradingBroker _broker;
    private readonly LiveTradingSafetyValidator _safety;
    private readonly IOrderRepository _orders;
    private readonly ISystemClock _clock;
    private readonly ILogger<OrderExecutionEngine> _logger;

    public OrderExecutionEngine(
        TradingSettings trading,
        ITradingBroker broker,
        LiveTradingSafetyValidator safety,
        IOrderRepository orders,
        ISystemClock clock,
        ILogger<OrderExecutionEngine> logger)
    {
        _trading = trading ?? throw new ArgumentNullException(nameof(trading));
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _safety = safety ?? throw new ArgumentNullException(nameof(safety));
        _orders = orders ?? throw new ArgumentNullException(nameof(orders));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<ExecutionResult> SubmitAsync(OrderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (request.Quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(request), request.Quantity, "Order quantity must be positive.");

        var order = await PersistNewAsync(request, cancellationToken).ConfigureAwait(false);

        // Research does no execution at all (§6). Fail closed.
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

        // A broker order id exists only for a fully-gated live order, so this path needs no re-gating.
        if (order.BrokerOrderId is { } brokerOrderId)
        {
            await _broker.CancelOrderAsync(brokerOrderId, cancellationToken).ConfigureAwait(false);
            Transition(order, OrderState.CancelPending);
            await UpdateAsync(order, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Cancel requested {CorrelationId}: broker order {BrokerOrderId}", order.CorrelationId, brokerOrderId);
            return Result(order, accepted: true, "Cancellation requested from broker.");
        }

        // Simulated order: cancel locally, respecting the §25 machine (Open must pass through CancelPending).
        if (order.State == OrderState.Open)
            Transition(order, OrderState.CancelPending);
        Transition(order, OrderState.Cancelled);
        await UpdateAsync(order, cancellationToken).ConfigureAwait(false);
        return Result(order, accepted: true, "Simulated order cancelled.");
    }

    /// <inheritdoc />
    public async Task<ExecutionResult> ApplyBrokerUpdateAsync(BrokerOrderUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        cancellationToken.ThrowIfCancellationRequested();

        var order = await _orders.GetByBrokerIdAsync(update.BrokerOrderId, cancellationToken).ConfigureAwait(false);
        if (order is null)
        {
            _logger.LogInformation("Ignoring broker update for untracked order {BrokerOrderId}", update.BrokerOrderId);
            return new ExecutionResult(false, 0, OrderState.New, update.BrokerOrderId, "No local order for that broker id.");
        }

        // External updates may arrive stale, duplicated, or out of sequence: never throw, just skip.
        if (!order.State.IsValidTransition(update.State))
        {
            _logger.LogInformation("Ignoring out-of-sequence broker update {From}->{To} for {CorrelationId}",
                order.State, update.State, order.CorrelationId);
            return Result(order, accepted: false, $"Ignored update to {update.State} from {order.State}.");
        }

        order.TryTransitionTo(update.State);

        // The broker is authoritative for fill progress.
        order.FilledQuantity = update.FilledQuantity;
        if (update.AverageFillPrice is { } avg)
            order.AverageFillPrice = avg;
        if (update.State == OrderState.Filled)
            order.FilledAtUtc = update.TimestampUtc;
        if (update.State is OrderState.Rejected or OrderState.Failed)
            order.RejectionReason = update.StatusMessage;

        await UpdateAsync(order, cancellationToken).ConfigureAwait(false);
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
        // Final, authoritative live-safety check at the moment of transmission (§6, §36).
        var validation = _safety.ValidateForLiveTrading(_trading);
        if (!validation.IsValid)
        {
            var reason = "Live safety validation failed: " + string.Join("; ", validation.Failures);
            _logger.LogError("Refusing to transmit live order {CorrelationId}: {Reason}", order.CorrelationId, reason);
            return await RejectAsync(order, reason, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogWarning("Transmitting LIVE order {CorrelationId}: {Side} {Quantity} {Symbol}",
            order.CorrelationId, request.Side, request.Quantity, request.Symbol);

        var placement = await _broker.PlaceOrderAsync(request, cancellationToken).ConfigureAwait(false);
        if (!placement.IsSuccess)
            return await RejectAsync(order, placement.ErrorMessage ?? "Broker rejected the order.", cancellationToken).ConfigureAwait(false);

        order.BrokerOrderId = placement.BrokerOrderId;
        Transition(order, OrderState.Submitted);
        await UpdateAsync(order, cancellationToken).ConfigureAwait(false);
        return Result(order, accepted: true, "Order submitted to broker.");
    }

    private async Task<ExecutionResult> SimulateAsync(Order order, OrderRequest request, CancellationToken cancellationToken)
    {
        Transition(order, OrderState.Submitted);

        // Fill only at an honest price: a limit order fills at its limit. A market order has no price here,
        // so it rests as Open until a price-fed paper fill closes it (separate concern).
        if (request.Price is { } limitPrice && limitPrice > 0m)
        {
            Transition(order, OrderState.Filled);
            order.FilledQuantity = order.Quantity;
            order.AverageFillPrice = limitPrice;
            order.FilledAtUtc = _clock.UtcNow;
            await UpdateAsync(order, cancellationToken).ConfigureAwait(false);
            return Result(order, accepted: true, "Simulated fill at limit price.");
        }

        Transition(order, OrderState.Open);
        await UpdateAsync(order, cancellationToken).ConfigureAwait(false);
        return Result(order, accepted: true, "Simulated market order resting (awaiting price-fed fill).");
    }

    private async Task<ExecutionResult> RejectAsync(Order order, string reason, CancellationToken cancellationToken)
    {
        Transition(order, OrderState.Rejected);
        order.RejectionReason = reason;
        await UpdateAsync(order, cancellationToken).ConfigureAwait(false);
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
