namespace AlgoTrader.Trading;

using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Portfolio;

/// <summary>
/// In-memory portfolio ledger for the paper/live trading loop (§8, §10). It tracks open positions, a cash
/// balance seeded from configured starting capital, and per-session realized P&amp;L and trade counts — so the
/// loop can build honest <c>StrategyContext</c> and <c>RiskEvaluationContext</c> snapshots on each candle
/// without hitting the database. Realized P&amp;L is net of round-trip charges (§18), mirroring the backtester's
/// honest cash accounting.
/// <para>
/// Deliberately NOT durable: it models one running session's paper book. Persistent position/P&amp;L history is a
/// separate concern (the persistence layer), and live-broker position reconciliation is layered on later — the
/// live path must trust the broker as the source of truth, not this ledger.
/// </para>
/// <para>
/// <b>Trade counting:</b> <see cref="PaperPortfolioSnapshot.TradesToday"/> counts entries opened in the current
/// IST session. That is the figure the trades-per-day gate (§15) needs — it bounds how many new risk positions
/// may be taken per day — so it is incremented on entry, not on the later exit.
/// </para>
/// </summary>
public interface IPaperPortfolio
{
    /// <summary>
    /// Records an entry (buy) fill: opens a long position and deducts deployed cash plus entry charges.
    /// Returns <c>false</c> without mutating state if a position for the instrument is already open (the risk
    /// engine's symbol-already-open gate should prevent this; the ledger stays defensive).
    /// </summary>
    bool RecordEntryFill(PaperEntryFill fill);

    /// <summary>
    /// Records an exit (sell) fill: closes the open position, credits net proceeds and realizes P&amp;L for the
    /// session. Returns <c>false</c> if no position is open for the instrument.
    /// </summary>
    bool RecordExitFill(int instrumentToken, decimal fillPrice, DateTimeOffset filledAtUtc);

    /// <summary>The currently open position for an instrument, or null when flat.</summary>
    OpenPosition? GetOpenPosition(int instrumentToken);

    /// <summary>
    /// Point-in-time view for building risk/strategy contexts. Rolls the session day first, so realized-P&amp;L
    /// and trade counters reflect <paramref name="asOfUtc"/>'s IST session even if no fill has occurred yet today.
    /// </summary>
    PaperPortfolioSnapshot Snapshot(DateTimeOffset asOfUtc);
}

/// <summary>Inputs describing one entry (buy) fill for the paper ledger.</summary>
public sealed record PaperEntryFill(
    int InstrumentToken,
    string Symbol,
    string Exchange,
    string StrategyName,
    ProductType Product,
    int Quantity,
    decimal FillPrice,
    DateTimeOffset FilledAtUtc,
    decimal? StopPrice,
    decimal? TargetPrice,
    string CorrelationId);

/// <summary>Immutable snapshot of the paper book at one instant.</summary>
public sealed record PaperPortfolioSnapshot(
    decimal Cash,
    decimal RealizedPnlToday,
    int TradesToday,
    IReadOnlyList<OpenPosition> OpenPositions,
    IReadOnlySet<string> SymbolsWithOpenPositions);
