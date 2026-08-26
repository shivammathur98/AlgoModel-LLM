namespace AlgoTrader.Trading;

using AlgoTrader.Application.Repositories;
using AlgoTrader.Domain.Broker;
using AlgoTrader.Domain.Costing;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Orders;
using AlgoTrader.Domain.Portfolio;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="ILiveAccountView"/>. Reads positions and funds from the (authenticated) broker and
/// recovers each open position's provenance — open time, strategy name, correlation id — from the most recent
/// filled buy order in the local store, since the broker's position report carries none of those.
/// <para>
/// It also derives the session-day risk figures (<see cref="LiveAccountSnapshot.RealizedPnlToday"/>,
/// <see cref="LiveAccountSnapshot.TradesToday"/>) from today's filled orders in the local store, net of
/// round-trip charges via <see cref="ITradingCostCalculator"/> — the same net-of-charges accounting the paper
/// ledger applies — so the daily-loss and trades-per-day gates are effective in Live mode.
/// </para>
/// </summary>
public sealed class LiveAccountView : ILiveAccountView
{
    /// <summary>
    /// How far back to look for the order that opened a currently-held position. Generous enough to cover an
    /// overnight swing hold (1–2 sessions) while keeping the query to this system's own small order set.
    /// </summary>
    private static readonly TimeSpan ProvenanceLookback = TimeSpan.FromDays(7);

    /// <summary>IST is UTC+05:30; the session-day boundary for the daily figures is evaluated in IST, matching the paper ledger.</summary>
    private static readonly TimeSpan IndiaStandardTimeOffset = TimeSpan.FromHours(5.5);

    private readonly ITradingCostCalculator _costs;
    private readonly ILogger<LiveAccountView> _logger;

    public LiveAccountView(ITradingCostCalculator costs, ILogger<LiveAccountView> logger)
    {
        _costs = costs ?? throw new ArgumentNullException(nameof(costs));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<LiveAccountSnapshot> CaptureAsync(
        ITradingBroker broker,
        IOrderRepository orders,
        ProductType product,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(broker);
        ArgumentNullException.ThrowIfNull(orders);

        var funds = await broker.GetFundsAsync(cancellationToken).ConfigureAwait(false);
        var positions = await broker.GetPositionsAsync(cancellationToken).ConfigureAwait(false);
        var openOrders = await orders.GetOpenOrdersAsync(cancellationToken).ConfigureAwait(false);
        var recentOrders = await orders
            .GetOrdersAsync(asOfUtc - ProvenanceLookback, asOfUtc, cancellationToken).ConfigureAwait(false);

        var entryByToken = BuildEntryProvenanceIndex(recentOrders);
        var inFlight = openOrders.Select(o => o.InstrumentToken).ToHashSet();
        var (realizedPnlToday, tradesToday) = ComputeSessionCounters(recentOrders, product, asOfUtc);

        var open = new Dictionary<int, OpenPosition>();
        foreach (var position in positions)
        {
            // Long-only: direction lives in Side (the broker reports Quantity as an unsigned magnitude), so a
            // net-long position is Side == Buy — testing Quantity <= 0 would be dead. Match the product the cycle
            // trades so an unrelated holding (e.g. a CNC delivery position) is never mistaken for this position.
            if (position.Product != product || position.Side != OrderSide.Buy || position.Quantity <= 0)
                continue;

            open[position.InstrumentToken] = MapPosition(position, entryByToken, asOfUtc);
        }

        return new LiveAccountSnapshot(funds.AvailableCash, realizedPnlToday, tradesToday, open, inFlight, asOfUtc);
    }

    /// <summary>
    /// Derives the session-day risk figures the daily-loss and trades-per-day gates read (§15), from the local
    /// store's filled orders for the current IST trading date. It mirrors the paper ledger's accounting exactly:
    /// a filled buy opens one long lot per instrument (and counts as a "trade today"); a filled sell realizes
    /// <c>(exit − average entry) × quantity</c> against that lot, net of both legs' charges. Broker fills are
    /// reflected here because the reconciliation bridge writes them back to the local order store, so this stays
    /// correct across a mid-session restart — unlike an in-memory counter, which would reset the day's loss.
    /// </summary>
    private (decimal RealizedPnlToday, int TradesToday) ComputeSessionCounters(
        IReadOnlyList<Order> recentOrders, ProductType product, DateTimeOffset asOfUtc)
    {
        var sessionDateIst = DateOnly.FromDateTime(asOfUtc.ToOffset(IndiaStandardTimeOffset).DateTime);

        // Process all recent fills chronologically to reconstruct open lots correctly,
        // including overnight positions. We only accrue realized P&L and trade counts
        // for fills that occurred during today's session.
        var allFills = recentOrders
            .Where(o => o.Product == product && o.FilledQuantity > 0 && o.FilledAtUtc is not null)
            .OrderBy(o => o.FilledAtUtc!.Value)
            .ToList();

        var realizedPnl = 0m;
        var trades = 0;
        var openLots = new Dictionary<int, (decimal AvgPrice, int Quantity, decimal EntryCost)>();

        foreach (var order in allFills)
        {
            var price = order.AverageFillPrice ?? 0m;
            if (price <= 0m)
                continue;

            var isToday = DateOnly.FromDateTime(order.FilledAtUtc!.Value.ToOffset(IndiaStandardTimeOffset).DateTime) == sessionDateIst;

            if (order.Side == OrderSide.Buy)
            {
                if (openLots.ContainsKey(order.InstrumentToken))
                    continue; // Second entry without exit ignored (just like paper ledger)

                var entryCost = _costs.Calculate(new CostCalculationContext(
                    order.Exchange, product, OrderSide.Buy, order.FilledQuantity, price)).Total;
                
                openLots[order.InstrumentToken] = (price, order.FilledQuantity, entryCost);
                
                if (isToday)
                    trades++; // Only count today's entries against max trades per day
            }
            else if (openLots.Remove(order.InstrumentToken, out var lot))
            {
                var exitQty = Math.Min(order.FilledQuantity, lot.Quantity);
                var closedEntryCost = lot.Quantity == 0 ? 0m : lot.EntryCost * exitQty / lot.Quantity;
                var exitCost = _costs.Calculate(new CostCalculationContext(
                    order.Exchange, product, OrderSide.Sell, exitQty, price)).Total;
                
                if (isToday)
                {
                    realizedPnl += (price - lot.AvgPrice) * exitQty - closedEntryCost - exitCost;
                }

                var remaining = lot.Quantity - exitQty;
                if (remaining > 0)
                    openLots[order.InstrumentToken] = (lot.AvgPrice, remaining, lot.EntryCost - closedEntryCost);
            }
        }

        return (realizedPnl, trades);
    }

    private OpenPosition MapPosition(
        BrokerPositionSummary position, IReadOnlyDictionary<int, Order> entryByToken, DateTimeOffset asOfUtc)
    {
        // Broker truth for size and average price; local provenance for when/why we opened it. Stop/target are
        // deliberately null — both bundled strategies decide exits from candles and ignore those fields on the
        // open position (the fixed stop/target ride on the entry order and are enforced intrabar).
        if (entryByToken.TryGetValue(position.InstrumentToken, out var entry))
        {
            return new OpenPosition(
                position.InstrumentToken, position.Symbol, entry.StrategyName ?? string.Empty,
                position.Quantity, position.AveragePrice, entry.FilledAtUtc ?? entry.CreatedAtUtc,
                StopPrice: null, TargetPrice: null, entry.CorrelationId);
        }

        // A held position with no local entry order (opened outside the platform, or state lost across a restart).
        // Report it so the cycle still treats the instrument as non-flat, but with unknown provenance: open time
        // falls back to the observation instant so a time-based exit does not fire on a position we cannot date.
        _logger.LogWarning(
            "Live position {Symbol} ({Token}) qty {Qty} has no local entry order; reporting with unknown provenance.",
            position.Symbol, position.InstrumentToken, position.Quantity);
        return new OpenPosition(
            position.InstrumentToken, position.Symbol, StrategyName: string.Empty,
            position.Quantity, position.AveragePrice, asOfUtc,
            StopPrice: null, TargetPrice: null, CorrelationId: string.Empty);
    }

    /// <summary>Indexes the latest filled buy order per instrument — the order that opened the current long position.</summary>
    private static IReadOnlyDictionary<int, Order> BuildEntryProvenanceIndex(IReadOnlyList<Order> recentOrders)
    {
        var index = new Dictionary<int, Order>();
        foreach (var order in recentOrders)
        {
            if (order.Side != OrderSide.Buy || order.FilledQuantity <= 0)
                continue;

            var when = order.FilledAtUtc ?? order.CreatedAtUtc;
            if (!index.TryGetValue(order.InstrumentToken, out var existing) ||
                when > (existing.FilledAtUtc ?? existing.CreatedAtUtc))
            {
                index[order.InstrumentToken] = order;
            }
        }

        return index;
    }
}
