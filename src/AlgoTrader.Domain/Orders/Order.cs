namespace AlgoTrader.Domain.Orders;

using AlgoTrader.Domain.Common;
using AlgoTrader.Domain.Enums;

/// <summary>
/// Local record of an order through its full lifecycle. The state machine in
/// <see cref="OrderStateExtensions"/> governs which transitions are legal (§25).
/// </summary>
public class Order : Entity
{
    /// <summary>Broker-assigned order id once the order has been accepted.</summary>
    public string? BrokerOrderId { get; set; }

    public int InstrumentToken { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Exchange { get; set; } = string.Empty;

    public OrderSide Side { get; set; }
    public OrderType Type { get; set; }
    public OrderValidity Validity { get; set; }
    public ProductType Product { get; set; }

    public int Quantity { get; set; }
    public decimal? Price { get; set; }
    public decimal? TriggerPrice { get; set; }

    public int FilledQuantity { get; set; }
    public decimal? AverageFillPrice { get; set; }

    public OrderState State { get; set; } = OrderState.New;
    public string? RejectionReason { get; set; }

    /// <summary>Free-form tag, e.g. the signal correlation id.</summary>
    public string? Tag { get; set; }

    public string CorrelationId { get; set; } = string.Empty;
    public string? StrategyName { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastUpdatedAtUtc { get; set; }
    public DateTimeOffset? FilledAtUtc { get; set; }

    /// <summary>True once the order has reached a terminal state.</summary>
    public bool IsTerminal => State.IsTerminal();

    /// <summary>
    /// Attempts a state transition, enforcing the §25 state machine.
    /// Returns false (and leaves state unchanged) when the transition is illegal.
    /// </summary>
    public bool TryTransitionTo(OrderState newState)
    {
        if (!State.IsValidTransition(newState))
        {
            return false;
        }

        State = newState;
        return true;
    }
}
