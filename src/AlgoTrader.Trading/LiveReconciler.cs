namespace AlgoTrader.Trading;

using AlgoTrader.Application.Repositories;
using AlgoTrader.Domain.Broker;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Orders;

/// <summary>
/// Default <see cref="ILiveReconciler"/>. Reuses <see cref="ILiveAccountView"/> for broker-truth positions,
/// cash and today's realized P&amp;L, then adds order-level reconciliation (local orders vs the broker's order
/// book, matched by broker id) and position-level reconciliation (broker-held longs vs the net of the window's
/// local fills). It reads only; it never mutates state.
/// </summary>
public sealed class LiveReconciler : ILiveReconciler
{
    private readonly ILiveAccountView _accountView;

    public LiveReconciler(ILiveAccountView accountView) =>
        _accountView = accountView ?? throw new ArgumentNullException(nameof(accountView));

    /// <inheritdoc />
    public async Task<ReconciliationReport> ReconcileAsync(
        ITradingBroker broker,
        IOrderRepository orders,
        ProductType product,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(orders);

        // Broker truth for positions, cash and realized P&L (the account view already derives all three).
        var snapshot = await _accountView.CaptureAsync(broker, orders, product, toUtc, cancellationToken).ConfigureAwait(false);
        var brokerOrders = await broker.GetOrdersAsync(cancellationToken).ConfigureAwait(false);
        var localOrders = await orders.GetOrdersAsync(fromUtc, toUtc, cancellationToken).ConfigureAwait(false);

        var discrepancies = new List<ReconciliationDiscrepancy>();
        var localTransmitted = ReconcileOrders(localOrders, brokerOrders, discrepancies);
        ReconcilePositions(localOrders, snapshot.OpenPositions, product, discrepancies);

        // Critical first, so the most dangerous drift is at the top of the report.
        var ordered = discrepancies
            .OrderByDescending(d => (int)d.Severity)
            .ThenBy(d => d.InstrumentToken)
            .ToList();

        return new ReconciliationReport(
            fromUtc, toUtc, product,
            LocalTransmittedOrderCount: localTransmitted,
            BrokerOrderCount: brokerOrders.Count,
            BrokerOpenPositionCount: snapshot.OpenPositions.Count,
            BrokerAvailableCash: snapshot.AvailableCash,
            RealizedPnlToday: snapshot.RealizedPnlToday,
            Discrepancies: ordered);
    }

    /// <summary>Matches transmitted local orders against the broker book by broker id; returns the transmitted count.</summary>
    private static int ReconcileOrders(
        IReadOnlyList<Order> localOrders,
        IReadOnlyList<BrokerOrderInfo> brokerOrders,
        List<ReconciliationDiscrepancy> discrepancies)
    {
        var brokerById = IndexByBrokerId(brokerOrders, o => o.BrokerOrderId);
        var localById = new HashSet<string>(StringComparer.Ordinal);
        var transmittedCount = 0;

        foreach (var local in localOrders)
        {
            // Only orders that actually reached the broker (carry a broker id) are comparable to the broker book.
            if (string.IsNullOrEmpty(local.BrokerOrderId))
                continue;

            transmittedCount++;
            localById.Add(local.BrokerOrderId);

            if (!brokerById.TryGetValue(local.BrokerOrderId, out var brokerOrder))
            {
                discrepancies.Add(new ReconciliationDiscrepancy(
                    ReconciliationIssue.OrderMissingAtBroker, ReconciliationSeverity.Critical,
                    local.InstrumentToken, local.Symbol,
                    $"Local order {local.Id} carries broker id {local.BrokerOrderId} but the broker's order book has no such order.",
                    local.BrokerOrderId));
                continue;
            }

            // A quantity divergence is the concrete symptom of a missed fill/postback; report that rather than a
            // (likely consequent) state mismatch. Otherwise a state disagreement is a lagging, lower-severity drift.
            if (local.FilledQuantity != brokerOrder.FilledQuantity)
            {
                discrepancies.Add(new ReconciliationDiscrepancy(
                    ReconciliationIssue.FilledQuantityMismatch, ReconciliationSeverity.Critical,
                    local.InstrumentToken, local.Symbol,
                    $"Filled quantity disagrees: local {local.FilledQuantity} vs broker {brokerOrder.FilledQuantity}.",
                    local.BrokerOrderId));
            }
            else if (local.State != brokerOrder.State)
            {
                discrepancies.Add(new ReconciliationDiscrepancy(
                    ReconciliationIssue.OrderStateMismatch, ReconciliationSeverity.Warning,
                    local.InstrumentToken, local.Symbol,
                    $"Order state disagrees: local {local.State} vs broker {brokerOrder.State}.",
                    local.BrokerOrderId));
            }
        }

        foreach (var brokerOrder in brokerOrders)
        {
            if (!localById.Contains(brokerOrder.BrokerOrderId))
            {
                discrepancies.Add(new ReconciliationDiscrepancy(
                    ReconciliationIssue.OrderMissingLocally, ReconciliationSeverity.Warning,
                    brokerOrder.InstrumentToken, brokerOrder.Symbol,
                    $"Broker order {brokerOrder.BrokerOrderId} ({brokerOrder.Side} {brokerOrder.Quantity}) has no matching local record.",
                    brokerOrder.BrokerOrderId));
            }
        }

        return transmittedCount;
    }

    /// <summary>Compares broker-held longs against the net long implied by the window's local fills.</summary>
    private static void ReconcilePositions(
        IReadOnlyList<Order> localOrders,
        IReadOnlyDictionary<int, Domain.Portfolio.OpenPosition> brokerPositions,
        ProductType product,
        List<ReconciliationDiscrepancy> discrepancies)
    {
        var localNetByToken = new Dictionary<int, int>();
        var symbolByToken = new Dictionary<int, string>();
        foreach (var order in localOrders)
        {
            if (order.Product != product || order.FilledQuantity <= 0)
                continue;

            var signed = order.Side == OrderSide.Buy ? order.FilledQuantity : -order.FilledQuantity;
            localNetByToken[order.InstrumentToken] = localNetByToken.GetValueOrDefault(order.InstrumentToken) + signed;
            symbolByToken.TryAdd(order.InstrumentToken, order.Symbol);
        }

        // Broker holds it → we must too, in the same size.
        foreach (var position in brokerPositions.Values)
        {
            var localNet = localNetByToken.GetValueOrDefault(position.InstrumentToken);
            if (localNet <= 0)
            {
                discrepancies.Add(new ReconciliationDiscrepancy(
                    ReconciliationIssue.OrphanPosition, ReconciliationSeverity.Critical,
                    position.InstrumentToken, position.Symbol,
                    $"Broker holds {position.Quantity} but local fills imply no long position — untracked risk."));
            }
            else if (localNet != position.Quantity)
            {
                // A size mismatch where the broker holds MORE than we track is untracked exposure — as dangerous
                // as an orphan (it won't engage the kill switch as a mere warning), so it is Critical. The broker
                // holding LESS than we track over-states our position and is the safer direction → Warning.
                var brokerHoldsMore = position.Quantity > localNet;
                discrepancies.Add(new ReconciliationDiscrepancy(
                    ReconciliationIssue.PositionQuantityMismatch,
                    brokerHoldsMore ? ReconciliationSeverity.Critical : ReconciliationSeverity.Warning,
                    position.InstrumentToken, position.Symbol,
                    $"Position size disagrees: local {localNet} vs broker {position.Quantity}."));
            }
        }

        // We imply a long the broker does not hold.
        foreach (var (token, net) in localNetByToken)
        {
            if (net > 0 && !brokerPositions.ContainsKey(token))
            {
                discrepancies.Add(new ReconciliationDiscrepancy(
                    ReconciliationIssue.PhantomPosition, ReconciliationSeverity.Warning,
                    token, symbolByToken.GetValueOrDefault(token, string.Empty),
                    $"Local fills imply a long of {net} but the broker holds no such position."));
            }
        }
    }

    private static Dictionary<string, T> IndexByBrokerId<T>(IEnumerable<T> items, Func<T, string> keySelector)
    {
        var index = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var key = keySelector(item);
            if (!string.IsNullOrEmpty(key))
                index[key] = item; // last-wins; broker ids are unique in practice.
        }

        return index;
    }
}
