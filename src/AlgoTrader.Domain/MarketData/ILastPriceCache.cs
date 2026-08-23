namespace AlgoTrader.Domain.MarketData;

/// <summary>
/// Thread-safe snapshot of the most recently observed price per instrument (§7, §9). The live feed
/// writes each tick's last-traded price here; consumers read it without touching the feed — the paper
/// trading loop uses it to fill resting market orders (§8), and risk evaluation uses
/// <see cref="LastPrice.AsOfUtc"/> to judge market-data staleness (§14).
/// <para>
/// The cache stores only what has actually ticked: an instrument that has not been observed has no
/// entry and no price is ever fabricated. It does not expire entries itself — staleness is a policy the
/// caller applies against <see cref="LastPrice.AsOfUtc"/> (e.g. <c>RiskSettings.MarketDataStaleAfterSeconds</c>).
/// </para>
/// </summary>
public interface ILastPriceCache
{
    /// <summary>Records the latest observed price for an instrument from a tick. Last write wins.</summary>
    void Update(Tick tick);

    /// <summary>Returns the latest observed price for an instrument, or <c>null</c> if none has been seen.</summary>
    LastPrice? Get(int instrumentToken);

    /// <summary>Attempts to read the latest observed price for an instrument.</summary>
    bool TryGet(int instrumentToken, out LastPrice price);
}

/// <summary>
/// A single last-observed price and the timestamp it was observed at (§7). <paramref name="AsOfUtc"/>
/// is the feed's observation time, used by callers to reason about staleness.
/// </summary>
public readonly record struct LastPrice(int InstrumentToken, decimal Price, DateTimeOffset AsOfUtc);
