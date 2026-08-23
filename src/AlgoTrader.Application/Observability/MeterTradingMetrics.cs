namespace AlgoTrader.Application.Observability;

using System.Diagnostics.Metrics;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Risk;
using AlgoTrader.Domain.Trading;

/// <summary>
/// Production <see cref="ITradingMetrics"/> backed by <see cref="System.Diagnostics.Metrics"/> (zero extra
/// dependencies — the API ships in the framework). Every event is a monotonic <see cref="Counter{T}"/> increment
/// tagged with a small, bounded set of labels so a metrics backend (OpenTelemetry, `dotnet-counters`, Prometheus
/// via the OTLP exporter) can slice by mode / side / reason without unbounded cardinality.
/// <para>
/// Registered as a singleton: the <see cref="Meter"/> lives for the process lifetime. This type is a pure sink —
/// it holds no trading state and no method throws.
/// </para>
/// </summary>
public sealed class MeterTradingMetrics : ITradingMetrics, IDisposable
{
    /// <summary>The meter name a listener subscribes to (e.g. <c>dotnet-counters --meters AlgoTrader.Trading</c>).</summary>
    public const string MeterName = "AlgoTrader.Trading";

    private readonly Meter _meter;
    private readonly Counter<long> _ticksProcessed;
    private readonly Counter<long> _candlesClosed;
    private readonly Counter<long> _signalsGenerated;
    private readonly Counter<long> _riskRejections;
    private readonly Counter<long> _ordersSubmitted;
    private readonly Counter<long> _ordersFilled;
    private readonly Counter<long> _ordersRejected;
    private readonly Counter<long> _reconciliations;
    private readonly Counter<long> _killSwitchEngagements;

    public MeterTradingMetrics()
    {
        _meter = new Meter(MeterName);
        _ticksProcessed = _meter.CreateCounter<long>("algotrader.ticks_processed", "{tick}", "Market-data ticks accepted by a decision cycle.");
        _candlesClosed = _meter.CreateCounter<long>("algotrader.candles_closed", "{candle}", "Decision candles closed.");
        _signalsGenerated = _meter.CreateCounter<long>("algotrader.signals_generated", "{signal}", "Signals emitted by the active strategy.");
        _riskRejections = _meter.CreateCounter<long>("algotrader.risk_rejections", "{rejection}", "Signals vetoed by the risk engine.");
        _ordersSubmitted = _meter.CreateCounter<long>("algotrader.orders_submitted", "{order}", "Orders submitted to the execution engine.");
        _ordersFilled = _meter.CreateCounter<long>("algotrader.orders_filled", "{order}", "Orders that reached the filled state.");
        _ordersRejected = _meter.CreateCounter<long>("algotrader.orders_rejected", "{order}", "Orders rejected by the safety gate, broker, or Research mode.");
        _reconciliations = _meter.CreateCounter<long>("algotrader.reconciliations", "{reconciliation}", "End-of-day reconciliation runs.");
        _killSwitchEngagements = _meter.CreateCounter<long>("algotrader.kill_switch_engagements", "{engagement}", "Kill-switch engagements.");
    }

    public void TickProcessed(TradingMode mode) =>
        _ticksProcessed.Add(1, new KeyValuePair<string, object?>("mode", mode.ToString()));

    public void CandleClosed(TradingMode mode) =>
        _candlesClosed.Add(1, new KeyValuePair<string, object?>("mode", mode.ToString()));

    public void SignalGenerated(TradingMode mode, string strategyName, SignalDirection direction) =>
        _signalsGenerated.Add(
            1,
            new KeyValuePair<string, object?>("mode", mode.ToString()),
            new KeyValuePair<string, object?>("strategy", strategyName),
            new KeyValuePair<string, object?>("direction", direction.ToString()));

    public void RiskRejected(TradingMode mode, RiskRejectionReason reason) =>
        _riskRejections.Add(
            1,
            new KeyValuePair<string, object?>("mode", mode.ToString()),
            new KeyValuePair<string, object?>("reason", reason.ToString()));

    public void OrderSubmitted(TradingMode mode, OrderSide side, OrderType type) =>
        _ordersSubmitted.Add(
            1,
            new KeyValuePair<string, object?>("mode", mode.ToString()),
            new KeyValuePair<string, object?>("side", side.ToString()),
            new KeyValuePair<string, object?>("type", type.ToString()));

    public void OrderFilled(TradingMode mode, OrderSide side) =>
        _ordersFilled.Add(
            1,
            new KeyValuePair<string, object?>("mode", mode.ToString()),
            new KeyValuePair<string, object?>("side", side.ToString()));

    public void OrderRejected(TradingMode mode, OrderSide side) =>
        _ordersRejected.Add(
            1,
            new KeyValuePair<string, object?>("mode", mode.ToString()),
            new KeyValuePair<string, object?>("side", side.ToString()));

    public void ReconciliationCompleted(bool isClean, int criticalCount) =>
        _reconciliations.Add(
            1,
            new KeyValuePair<string, object?>("clean", isClean),
            new KeyValuePair<string, object?>("has_critical", criticalCount > 0));

    public void KillSwitchEngaged(string initiatedBy) =>
        _killSwitchEngagements.Add(1, new KeyValuePair<string, object?>("initiated_by", initiatedBy));

    public void Dispose() => _meter.Dispose();
}
