namespace AlgoTrader.Domain.Enums;

/// <summary>Direction of an order.</summary>
public enum OrderSide
{
    Buy,
    Sell
}

/// <summary>Order pricing type. Names map cleanly onto Kite order types (MARKET/LIMIT/SL/SL-M).</summary>
public enum OrderType
{
    Market,
    Limit,
    StopLoss,
    StopLossLimit
}

/// <summary>Order validity.</summary>
public enum OrderValidity
{
    Day,
    Ioc
}

/// <summary>Broker-agnostic product types (Kite: MIS = intraday, CNC = delivery).</summary>
public enum ProductType
{
    /// <summary>Intraday (MIS). Positions must be squared off before market close.</summary>
    Intraday,

    /// <summary>Delivery (CNC).</summary>
    Delivery
}

/// <summary>Explicit order lifecycle states (§25).</summary>
public enum OrderState
{
    /// <summary>Created locally, not yet handed to the execution pipeline.</summary>
    New,

    /// <summary>Queued locally, awaiting submission.</summary>
    Pending,

    /// <summary>Submitted to the broker, awaiting acceptance.</summary>
    Submitted,

    /// <summary>Accepted by the broker / exchange, resting unfilled.</summary>
    Open,

    /// <summary>Partially filled, remainder still live.</summary>
    PartiallyFilled,

    /// <summary>Fully filled. Terminal.</summary>
    Filled,

    /// <summary>Cancellation requested, awaiting broker confirmation.</summary>
    CancelPending,

    /// <summary>Cancelled. Terminal.</summary>
    Cancelled,

    /// <summary>Rejected by broker/exchange/risk engine. Terminal.</summary>
    Rejected,

    /// <summary>Failed locally (connectivity, serialization, timeout). Terminal.</summary>
    Failed
}

/// <summary>Order state machine rules (§25).</summary>
public static class OrderStateExtensions
{
    private static readonly IReadOnlySet<OrderState> EmptySet = new HashSet<OrderState>();

    private static readonly IReadOnlyDictionary<OrderState, IReadOnlySet<OrderState>> AllowedTransitions =
        new Dictionary<OrderState, IReadOnlySet<OrderState>>
        {
            [OrderState.New] = new HashSet<OrderState>
            {
                OrderState.Pending, OrderState.Submitted, OrderState.Rejected, OrderState.Failed
            },
            [OrderState.Pending] = new HashSet<OrderState>
            {
                OrderState.Submitted, OrderState.Open, OrderState.CancelPending, OrderState.Rejected, OrderState.Failed
            },
            [OrderState.Submitted] = new HashSet<OrderState>
            {
                OrderState.Open, OrderState.PartiallyFilled, OrderState.Filled,
                OrderState.CancelPending, OrderState.Cancelled, OrderState.Rejected, OrderState.Failed
            },
            [OrderState.Open] = new HashSet<OrderState>
            {
                OrderState.PartiallyFilled, OrderState.Filled, OrderState.CancelPending, OrderState.Rejected, OrderState.Failed
            },
            [OrderState.PartiallyFilled] = new HashSet<OrderState>
            {
                OrderState.PartiallyFilled, OrderState.Filled, OrderState.CancelPending, OrderState.Rejected, OrderState.Failed
            },
            [OrderState.CancelPending] = new HashSet<OrderState>
            {
                OrderState.Cancelled, OrderState.Open, OrderState.PartiallyFilled, OrderState.Filled, OrderState.Failed
            },
            [OrderState.Filled] = EmptySet,
            [OrderState.Cancelled] = EmptySet,
            [OrderState.Rejected] = EmptySet,
            [OrderState.Failed] = EmptySet
        };

    /// <summary>True when moving from <paramref name="from"/> to <paramref name="to"/> is legal.</summary>
    public static bool IsValidTransition(this OrderState from, OrderState to)
        => AllowedTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    /// <summary>True for states that can never change again.</summary>
    public static bool IsTerminal(this OrderState state)
        => state is OrderState.Filled or OrderState.Cancelled or OrderState.Rejected or OrderState.Failed;
}
