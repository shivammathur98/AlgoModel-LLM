namespace AlgoTrader.Trading;

using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Portfolio;

/// <summary>
/// Read model of the live account at a decision instant, assembled from broker truth (positions, funds) plus
/// local order provenance. This is the Live-mode analogue of <see cref="PaperPortfolioSnapshot"/> — but unlike
/// the paper ledger it owns no state: the broker is authoritative for what is actually held, and the local
/// order store supplies the annotations the broker does not carry (when <i>our</i> strategy opened a position,
/// under which name and correlation id).
/// <para>
/// <see cref="RealizedPnlToday"/> and <see cref="TradesToday"/> are the session-day figures the daily-loss and
/// trades-per-day risk gates (§15) read. They are <b>derived</b> — recomputed from today's filled orders in the
/// local store on every capture, net of round-trip charges, exactly as the paper ledger accrues them — rather
/// than held in memory, so a mid-session restart cannot silently reset the day's realized loss.
/// </para>
/// </summary>
public sealed record LiveAccountSnapshot(
    decimal AvailableCash,
    decimal RealizedPnlToday,
    int TradesToday,
    IReadOnlyDictionary<int, OpenPosition> OpenPositions,
    IReadOnlySet<int> InFlightOrderTokens,
    DateTimeOffset AsOfUtc)
{
    /// <summary>The instrument's open long position, or null when flat.</summary>
    public OpenPosition? GetOpenPosition(int instrumentToken) =>
        OpenPositions.TryGetValue(instrumentToken, out var position) ? position : null;

    /// <summary>True when a non-terminal order for the instrument is already working at the broker.</summary>
    public bool HasInFlightOrder(int instrumentToken) => InFlightOrderTokens.Contains(instrumentToken);

    /// <summary>Distinct symbols currently holding an open position (for the risk snapshot).</summary>
    public IReadOnlySet<string> SymbolsWithOpenPositions =>
        OpenPositions.Values.Select(p => p.Symbol).ToHashSet(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Assembles a <see cref="LiveAccountSnapshot"/> for the live decision cycle (§8, §11, §26). Broker truth
/// (via <see cref="Domain.Broker.ITradingBroker"/>) is the source of what is held and how much buying power is
/// available; the local order store fills in the provenance the broker cannot know.
/// <para>
/// It is a pure assembler: the caller resolves the <b>authenticated</b> session-scope broker and order
/// repository and passes them in. It never fabricates a fill or a position — it only reports what the broker
/// and our own persisted orders already state.
/// </para>
/// </summary>
public interface ILiveAccountView
{
    /// <summary>
    /// Captures the account state for long positions in the given <paramref name="product"/>. Positions are
    /// annotated with open-time/strategy/correlation recovered from the most recent filled buy order for each
    /// instrument; a position with no local entry order is reported (so the cycle still treats the instrument as
    /// non-flat) but flagged with unknown provenance.
    /// </summary>
    Task<LiveAccountSnapshot> CaptureAsync(
        Domain.Broker.ITradingBroker broker,
        Application.Repositories.IOrderRepository orders,
        ProductType product,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken = default);
}
