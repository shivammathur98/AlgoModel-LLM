namespace AlgoTrader.Domain.Execution;

using AlgoTrader.Domain.Broker;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Orders;

/// <summary>
/// Outcome of submitting one order to the execution engine (§8, §11, §25).
/// </summary>
/// <param name="IsAccepted">
/// True when the order entered the pipeline — routed to the broker (Live) or simulated (Paper/Backtest).
/// False means the engine refused it or could not confirm it: a safety gate, a disallowed mode, or a
/// <b>definitive</b> broker business rejection leaves the local order terminal <see cref="OrderState.Rejected"/>;
/// an <b>uncertain</b> live submission (transport failure, timeout, 5xx, or an unreadable success) leaves it
/// terminal <see cref="OrderState.Failed"/> and engages the kill switch (§20, Safety Rules #5/#8/#9). The reason
/// is in <paramref name="Message"/>.
/// </param>
/// <param name="OrderId">Local persisted order id (always assigned, even for rejections, for auditability §28).</param>
/// <param name="State">The order's state after this submission attempt.</param>
/// <param name="BrokerOrderId">Broker-assigned id, present only once a real order was accepted by the broker.</param>
/// <param name="Message">Human-readable detail — the rejection reason, or a note (e.g. simulated fill).</param>
public sealed record ExecutionResult(
    bool IsAccepted,
    long OrderId,
    OrderState State,
    string? BrokerOrderId = null,
    string? Message = null);

/// <summary>
/// The order execution engine (§8, §11). It is the single point that turns a risk-approved
/// <see cref="OrderRequest"/> into a tracked <see cref="Order"/>, and the only component permitted to
/// transmit a real broker order — and then only when the platform is fully gated for live trading (§6, §36).
/// In every non-live mode it simulates execution and never touches the broker. Callers must have obtained
/// risk approval (<see cref="AlgoTrader.Domain.Risk.IRiskEngine"/>) before submitting; the execution engine
/// enforces safety and lifecycle, not trading-risk limits.
/// </summary>
public interface IExecutionEngine
{
    /// <summary>
    /// Records the order locally and either routes it to the broker (fully-gated Live only) or simulates it.
    /// Never throws for a business rejection — those surface as an unaccepted <see cref="ExecutionResult"/>.
    /// </summary>
    Task<ExecutionResult> SubmitAsync(OrderRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Requests cancellation of a non-terminal local order. For a live order that reached the broker this
    /// asks the broker to cancel (final <see cref="OrderState.Cancelled"/> arrives via a later update);
    /// for a simulated order it cancels locally. A terminal or unknown order is a no-op.
    /// </summary>
    Task<ExecutionResult> CancelAsync(long orderId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reconciles an asynchronous broker order update (§25, §26) — a fill, partial fill, cancel or reject
    /// pushed via <see cref="ITradingBroker.OrderUpdated"/> — into the tracked local order. The live trading
    /// loop wires the broker event to this method. It is resilient by design: an update for an untracked
    /// broker id, or one whose target state is not reachable from the current state (a stale, duplicate or
    /// out-of-sequence message), is logged and ignored rather than throwing — external events are not under
    /// our control. <see cref="ExecutionResult.IsAccepted"/> is true only when the update was applied.
    /// </summary>
    Task<ExecutionResult> ApplyBrokerUpdateAsync(BrokerOrderUpdate update, CancellationToken cancellationToken = default);

    /// <summary>
    /// Fills a resting simulated (Paper) market order at an observed price supplied by the caller — the paper
    /// trading loop feeds the last traded price from the live tick stream. No price is ever fabricated by the
    /// engine. A no-op for an unknown, terminal, or non-resting order.
    /// </summary>
    Task<ExecutionResult> ApplyPaperFillAsync(long orderId, decimal fillPrice, CancellationToken cancellationToken = default);
}
