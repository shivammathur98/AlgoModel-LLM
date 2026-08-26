namespace AlgoTrader.Application.Execution;

/// <summary>
/// Process-wide serializer for local order-state mutations (§21, §23).
/// <para>
/// Order submission commit and asynchronous broker fill/cancel reconciliation run on independent DI scopes —
/// hence independent <c>DbContext</c> instances — so their read → validate → write sequences are otherwise
/// unsynchronized and interleave: two concurrent broker postbacks (or a submission commit racing a postback)
/// can both read the same stale snapshot and the last writer clobbers a real fill (AUDIT-0009). A single
/// registered instance shared by every execution engine makes those sequences mutually exclusive without
/// requiring a database concurrency token in this single-process modular monolith.
/// </para>
/// <para>
/// Hold the gate ONLY around the read → validate → write of local order state. Never hold it across broker
/// network I/O (order placement / cancellation), which could stall all reconciliation behind a slow broker call.
/// </para>
/// </summary>
public interface IOrderMutationGate
{
    /// <summary>
    /// Acquires exclusive access, waiting if another holder is active. Dispose the returned handle to release
    /// (a <c>using</c> block is the intended usage). Honours <paramref name="cancellationToken"/> while waiting.
    /// </summary>
    Task<IDisposable> AcquireAsync(CancellationToken cancellationToken = default);
}
