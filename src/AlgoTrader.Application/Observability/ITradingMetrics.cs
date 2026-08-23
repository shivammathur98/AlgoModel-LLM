namespace AlgoTrader.Application.Observability;

using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Risk;
using AlgoTrader.Domain.Trading;

/// <summary>
/// Operational metrics for the trading loop (observability/ops). A thin, mode-tagged counter surface covering
/// the whole decision-to-fill lifecycle so an operator can see, at a glance, that the platform is alive and
/// behaving: ticks and candles flowing in, signals and risk vetoes, orders submitted/filled/rejected, the
/// end-of-day reconciliation outcome, and any kill-switch engagement.
/// <para>
/// It is a <b>sink</b>, never a decision input — nothing in the trading path may branch on a metric, and no
/// method here throws (a metrics failure must never break a trade). The production implementation
/// (<see cref="MeterTradingMetrics"/>) emits to <see cref="System.Diagnostics.Metrics"/>; tests and non-trading
/// modes use <see cref="NullTradingMetrics"/>. Labels are bounded (enums / a small set of strings) to keep
/// metric cardinality sane — never tag with a correlation id, order id, or symbol.
/// </para>
/// </summary>
public interface ITradingMetrics
{
    /// <summary>A market-data tick was accepted by a decision cycle.</summary>
    void TickProcessed(TradingMode mode);

    /// <summary>A decision candle closed (a strategy evaluation is about to run).</summary>
    void CandleClosed(TradingMode mode);

    /// <summary>The active strategy emitted a signal.</summary>
    void SignalGenerated(TradingMode mode, string strategyName, SignalDirection direction);

    /// <summary>The risk engine vetoed a signal before it could reach execution.</summary>
    void RiskRejected(TradingMode mode, RiskRejectionReason reason);

    /// <summary>An order was submitted to the execution engine (before the broker/simulator outcome is known).</summary>
    void OrderSubmitted(TradingMode mode, OrderSide side, OrderType type);

    /// <summary>An order reached the filled state (simulated, paper, or broker-confirmed).</summary>
    void OrderFilled(TradingMode mode, OrderSide side);

    /// <summary>An order was rejected (safety gate, broker rejection, or Research mode).</summary>
    void OrderRejected(TradingMode mode, OrderSide side);

    /// <summary>End-of-day reconciliation completed; records whether the book was clean and the critical count.</summary>
    void ReconciliationCompleted(bool isClean, int criticalCount);

    /// <summary>The kill switch was engaged (trading halted until an operator resets it).</summary>
    void KillSwitchEngaged(string initiatedBy);
}
