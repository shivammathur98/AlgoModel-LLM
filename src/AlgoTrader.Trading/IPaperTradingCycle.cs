namespace AlgoTrader.Trading;

using AlgoTrader.Domain.MarketData;

/// <summary>
/// The paper-mode decision cycle (§8, §11): the component the trading loop drives with the live tick stream.
/// On each tick it (1) fills any resting simulated order for that instrument at the observed price and updates
/// the paper ledger, then (2) aggregates the tick into a candle and, when a decision candle closes, runs the
/// active strategy → risk engine → position sizer → execution engine to place (resting) paper orders.
/// <para>
/// Deliberately paper-only: it reads and writes the in-memory <see cref="IPaperPortfolio"/> and simulates fills
/// from prices. The live decision path trusts the broker as the source of truth for positions and fills and is
/// wired separately in a later phase, so the loop only invokes this cycle in <c>Paper</c> mode.
/// </para>
/// </summary>
public interface IPaperTradingCycle
{
    /// <summary>
    /// Processes one live tick. Idempotent with respect to state the loop owns: it never throws for a normal
    /// market condition (an unfillable order, a rejected signal), only surfacing genuinely unexpected faults.
    /// </summary>
    Task OnTickAsync(Tick tick, CancellationToken cancellationToken = default);
}
