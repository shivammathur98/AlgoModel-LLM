namespace AlgoTrader.Trading;

using AlgoTrader.Domain.MarketData;

/// <summary>
/// The live-mode decision cycle (§8, §11): on each closed decision candle it runs the active strategy against
/// broker-truth account state and submits <b>real</b> orders through the execution engine (which enforces the
/// live triple-gate at transmit time). Unlike the paper cycle it never simulates a fill — live fills arrive
/// asynchronously from the broker and are reconciled by the trading loop's broker-update bridge.
/// <para>
/// Because every broker call requires the authenticated session (a freshly-scoped broker is not authenticated),
/// the loop binds the cycle to its session service provider via <see cref="Attach"/> before forwarding ticks and
/// releases it via <see cref="Detach"/> at shutdown. Ticks received while unattached are ignored.
/// </para>
/// </summary>
public interface ILiveTradingCycle
{
    /// <summary>Binds the cycle to the loop's authenticated session scope. Must be called before ticks flow.</summary>
    void Attach(IServiceProvider sessionServices);

    /// <summary>Releases the session binding at shutdown; subsequent ticks are ignored until re-attached.</summary>
    void Detach();

    /// <summary>Processes one live tick: aggregates it and, on a closed candle, runs the decision cycle.</summary>
    Task OnTickAsync(Tick tick, CancellationToken cancellationToken = default);
}
