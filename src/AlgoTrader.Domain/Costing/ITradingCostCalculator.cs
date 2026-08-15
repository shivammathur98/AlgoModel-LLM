namespace AlgoTrader.Domain.Costing;

using AlgoTrader.Domain.Enums;

/// <summary>Everything a cost calculator needs to price one order leg.</summary>
public sealed record CostCalculationContext(
    string Exchange,
    ProductType Product,
    OrderSide Side,
    int Quantity,
    decimal Price);

/// <summary>
/// Full charge breakdown for one order leg (§18). Implementations are centralized;
/// charge formulas must not be spread through strategy or backtesting code.
/// </summary>
public sealed record TradingCostBreakdown(
    decimal Brokerage,
    decimal Stt,
    decimal ExchangeTransactionCharges,
    decimal SebiCharges,
    decimal StampDuty,
    decimal Gst,
    decimal DpCharges,
    decimal OtherCharges)
{
    /// <summary>Total of all charge components.</summary>
    public decimal Total => Brokerage + Stt + ExchangeTransactionCharges + SebiCharges
                            + StampDuty + Gst + DpCharges + OtherCharges;
}

/// <summary>Centralized trading charge calculation (§18).</summary>
public interface ITradingCostCalculator
{
    /// <summary>Calculates the complete charge breakdown for one order leg.</summary>
    TradingCostBreakdown Calculate(CostCalculationContext context);
}
