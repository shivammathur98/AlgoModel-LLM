namespace AlgoTrader.Trading;

using AlgoTrader.Domain.Costing;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Portfolio;

/// <summary>
/// Default in-memory <see cref="IPaperPortfolio"/>. Cash accounting mirrors the backtester: an entry deducts
/// <c>price × qty + entry charges</c>; an exit credits <c>price × qty − exit charges</c>, so realized P&amp;L is
/// net of the full round-trip (§18). All mutating and reading operations take a single lock, because the
/// trading loop drives entries from candle-close handling and fills from the tick stream concurrently.
/// </summary>
public sealed class PaperPortfolio : IPaperPortfolio
{
    /// <summary>IST is UTC+05:30; session-day rollover for the daily counters is evaluated in IST.</summary>
    private static readonly TimeSpan IndiaStandardTimeOffset = TimeSpan.FromHours(5.5);

    private readonly ITradingCostCalculator _costs;
    private readonly object _lock = new();
    private readonly Dictionary<int, Lot> _lots = new();

    private decimal _cash;
    private decimal _realizedPnlToday;
    private int _tradesToday;
    private DateOnly? _sessionDateIst;

    public PaperPortfolio(decimal startingCapital, ITradingCostCalculator costs)
    {
        if (startingCapital <= 0m)
            throw new ArgumentOutOfRangeException(nameof(startingCapital), startingCapital, "Starting capital must be positive.");

        _costs = costs ?? throw new ArgumentNullException(nameof(costs));
        _cash = startingCapital;
    }

    /// <inheritdoc />
    public bool RecordEntryFill(PaperEntryFill fill)
    {
        ArgumentNullException.ThrowIfNull(fill);
        if (fill.Quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(fill), fill.Quantity, "Fill quantity must be positive.");
        if (fill.FillPrice <= 0m)
            throw new ArgumentOutOfRangeException(nameof(fill), fill.FillPrice, "Fill price must be positive.");

        lock (_lock)
        {
            RollSession(fill.FilledAtUtc);

            // Long-only: one open lot per instrument. A second entry is a caller/risk bug, not an add.
            if (_lots.ContainsKey(fill.InstrumentToken))
                return false;

            var entryCost = _costs.Calculate(new CostCalculationContext(
                fill.Exchange, fill.Product, OrderSide.Buy, fill.Quantity, fill.FillPrice)).Total;
            _cash -= fill.FillPrice * fill.Quantity + entryCost;

            var position = new OpenPosition(
                fill.InstrumentToken, fill.Symbol, fill.StrategyName, fill.Quantity, fill.FillPrice,
                fill.FilledAtUtc, fill.StopPrice, fill.TargetPrice, fill.CorrelationId);
            _lots[fill.InstrumentToken] = new Lot(position, fill.Exchange, fill.Product, entryCost);
            _tradesToday++;
            return true;
        }
    }

    /// <inheritdoc />
    public bool RecordExitFill(int instrumentToken, decimal fillPrice, DateTimeOffset filledAtUtc)
    {
        if (fillPrice <= 0m)
            throw new ArgumentOutOfRangeException(nameof(fillPrice), fillPrice, "Fill price must be positive.");

        lock (_lock)
        {
            RollSession(filledAtUtc);

            if (!_lots.Remove(instrumentToken, out var lot))
                return false;

            var quantity = lot.Position.Quantity;
            var exitCost = _costs.Calculate(new CostCalculationContext(
                lot.Exchange, lot.Product, OrderSide.Sell, quantity, fillPrice)).Total;

            _cash += fillPrice * quantity - exitCost;
            _realizedPnlToday += (fillPrice - lot.Position.AveragePrice) * quantity - lot.EntryCost - exitCost;
            return true;
        }
    }

    /// <inheritdoc />
    public OpenPosition? GetOpenPosition(int instrumentToken)
    {
        lock (_lock)
            return _lots.TryGetValue(instrumentToken, out var lot) ? lot.Position : null;
    }

    /// <inheritdoc />
    public PaperPortfolioSnapshot Snapshot(DateTimeOffset asOfUtc)
    {
        lock (_lock)
        {
            RollSession(asOfUtc);

            var positions = _lots.Values.Select(lot => lot.Position).ToList();
            var symbols = positions.Select(position => position.Symbol).ToHashSet(StringComparer.Ordinal);
            return new PaperPortfolioSnapshot(_cash, _realizedPnlToday, _tradesToday, positions, symbols);
        }
    }

    /// <summary>
    /// Resets the per-session counters when the IST trading date advances. Realized P&amp;L and trade counts are
    /// "today" figures for the daily-loss and trades-per-day gates (§15) and reset each session; open positions
    /// and cash carry across sessions, since a swing position may be held overnight. Must be called under the lock.
    /// </summary>
    private void RollSession(DateTimeOffset asOfUtc)
    {
        var dateIst = DateOnly.FromDateTime(asOfUtc.ToOffset(IndiaStandardTimeOffset).DateTime);
        if (_sessionDateIst == dateIst)
            return;

        // First observation seeds the session date without wiping already-zero counters.
        if (_sessionDateIst is not null)
        {
            _realizedPnlToday = 0m;
            _tradesToday = 0;
        }

        _sessionDateIst = dateIst;
    }

    /// <summary>An open lot plus the leg metadata needed to price its exit and its realized P&amp;L.</summary>
    private sealed record Lot(OpenPosition Position, string Exchange, ProductType Product, decimal EntryCost);
}
