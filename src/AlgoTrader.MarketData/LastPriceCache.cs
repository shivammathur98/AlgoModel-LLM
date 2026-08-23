namespace AlgoTrader.MarketData;

using System.Collections.Concurrent;
using AlgoTrader.Domain.MarketData;

/// <summary>
/// Default <see cref="ILastPriceCache"/>: a lock-free, last-writer-wins snapshot of the newest price per
/// instrument, backed by a <see cref="ConcurrentDictionary{TKey,TValue}"/>. Registered as a singleton so
/// the live feed and every consumer share one view. It stores only instruments that have actually ticked
/// (§7) — it never invents a price and never expires entries; staleness is a policy the caller applies
/// against <see cref="LastPrice.AsOfUtc"/>.
/// </summary>
public sealed class LastPriceCache : ILastPriceCache
{
    private readonly ConcurrentDictionary<int, LastPrice> _prices = new();

    /// <inheritdoc />
    public void Update(Tick tick)
    {
        ArgumentNullException.ThrowIfNull(tick);

        // Ticks from a single feed arrive sequentially, so last-writer-wins is correct; the indexer set
        // is atomic on ConcurrentDictionary, so no lock is needed even if writes ever interleave.
        _prices[tick.InstrumentToken] = new LastPrice(tick.InstrumentToken, tick.LastPrice, tick.TimestampUtc);
    }

    /// <inheritdoc />
    public LastPrice? Get(int instrumentToken) =>
        _prices.TryGetValue(instrumentToken, out var price) ? price : null;

    /// <inheritdoc />
    public bool TryGet(int instrumentToken, out LastPrice price) =>
        _prices.TryGetValue(instrumentToken, out price);
}
