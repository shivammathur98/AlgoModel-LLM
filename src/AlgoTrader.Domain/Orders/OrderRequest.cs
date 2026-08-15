namespace AlgoTrader.Domain.Orders;

using AlgoTrader.Domain.Enums;

/// <summary>
/// Immutable description of an order to be submitted to the execution engine.
/// Created from an approved signal by position sizing; never mutated afterwards.
/// </summary>
public sealed record OrderRequest(
    int InstrumentToken,
    string Symbol,
    string Exchange,
    OrderSide Side,
    OrderType Type,
    int Quantity,
    ProductType Product,
    decimal? Price = null,
    decimal? TriggerPrice = null,
    OrderValidity Validity = OrderValidity.Day,
    string? Tag = null,
    string? StrategyName = null)
{
    /// <summary>Correlation identifier carried through risk, execution, orders and logs.</summary>
    public string CorrelationId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>When the request was created (set from the system clock by the creator).</summary>
    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}
