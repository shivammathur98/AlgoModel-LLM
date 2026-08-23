namespace AlgoTrader.Trading;

using AlgoTrader.Domain.Enums;

/// <summary>How serious a reconciliation discrepancy is for continuing to trade.</summary>
public enum ReconciliationSeverity
{
    /// <summary>Informational — a difference that is expected or benign.</summary>
    Info,

    /// <summary>A difference worth a human's attention but not an immediate danger.</summary>
    Warning,

    /// <summary>Our record and broker truth disagree in a way that means untracked risk or a wrong book.</summary>
    Critical,
}

/// <summary>The kinds of drift the daily reconciliation surfaces between our local record and broker truth (§26, §28).</summary>
public enum ReconciliationIssue
{
    /// <summary>A local order carries a broker id, but the broker's order book has no such order.</summary>
    OrderMissingAtBroker,

    /// <summary>The broker's order book has an order we hold no local record of (e.g. placed outside the platform).</summary>
    OrderMissingLocally,

    /// <summary>A matched order whose lifecycle state disagrees between our record and the broker.</summary>
    OrderStateMismatch,

    /// <summary>A matched order whose filled quantity disagrees — typically a postback we never reconciled.</summary>
    FilledQuantityMismatch,

    /// <summary>The broker holds a long position we have no local open position for — untracked risk.</summary>
    OrphanPosition,

    /// <summary>We believe a position is open that the broker does not actually hold.</summary>
    PhantomPosition,

    /// <summary>Both sides hold the position but the quantities disagree.</summary>
    PositionQuantityMismatch,
}

/// <summary>One divergence between our local record and broker truth.</summary>
public sealed record ReconciliationDiscrepancy(
    ReconciliationIssue Issue,
    ReconciliationSeverity Severity,
    int InstrumentToken,
    string Symbol,
    string Detail,
    string? BrokerOrderId = null);

/// <summary>
/// The result of reconciling our local order/position record against broker truth for a trading session (§26,
/// §28). It is a snapshot of agreement — a clean report means every transmitted order and every held position
/// matches the broker. Any <see cref="ReconciliationSeverity.Critical"/> discrepancy means we are either
/// carrying risk we are not tracking or acting on a book that is wrong, and Live trading should be paused.
/// </summary>
public sealed record ReconciliationReport(
    DateTimeOffset FromUtc,
    DateTimeOffset AsOfUtc,
    ProductType Product,
    int LocalTransmittedOrderCount,
    int BrokerOrderCount,
    int BrokerOpenPositionCount,
    decimal BrokerAvailableCash,
    decimal RealizedPnlToday,
    IReadOnlyList<ReconciliationDiscrepancy> Discrepancies)
{
    /// <summary>True when nothing diverged — local record and broker truth agree.</summary>
    public bool IsClean => Discrepancies.Count == 0;

    /// <summary>True when at least one discrepancy is <see cref="ReconciliationSeverity.Critical"/>.</summary>
    public bool HasCritical => Discrepancies.Any(d => d.Severity == ReconciliationSeverity.Critical);

    /// <summary>A one-line headline suitable for an end-of-day log entry.</summary>
    public string Summary => IsClean
        ? $"Reconciliation {AsOfUtc:yyyy-MM-dd}: CLEAN ({BrokerOrderCount} broker order(s), {BrokerOpenPositionCount} open position(s))."
        : $"Reconciliation {AsOfUtc:yyyy-MM-dd}: {Discrepancies.Count} discrepancy(ies), "
          + $"{Discrepancies.Count(d => d.Severity == ReconciliationSeverity.Critical)} critical.";
}

/// <summary>
/// Reconciles our local order and position record against broker truth for a trading session (§26, §28) and
/// produces a <see cref="ReconciliationReport"/>. Like <see cref="ILiveAccountView"/> it is a pure assembler:
/// the caller resolves the <b>authenticated</b> session-scope broker and order repository and passes them in.
/// It reads only — it never mutates orders or positions — so it is safe to run at any time.
/// <para>
/// Position reconciliation assumes intraday positions that open and close within the reconciled window (the
/// window must cover the opening fill). It compares broker-held long positions in the traded product against
/// the net of the window's local filled orders.
/// </para>
/// </summary>
public interface ILiveReconciler
{
    /// <summary>
    /// Reconciles the window <paramref name="fromUtc"/>..<paramref name="toUtc"/> for the given
    /// <paramref name="product"/>. Order-level checks match local orders (those carrying a broker id) against
    /// the broker's order book by broker id; position-level checks compare broker-held longs against the net of
    /// the window's local fills. Cash and realized P&amp;L are taken from broker truth for context.
    /// </summary>
    Task<ReconciliationReport> ReconcileAsync(
        Domain.Broker.ITradingBroker broker,
        Application.Repositories.IOrderRepository orders,
        ProductType product,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken = default);
}
