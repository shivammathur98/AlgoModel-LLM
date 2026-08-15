namespace AlgoTrader.Risk;

using AlgoTrader.Domain.Sizing;

/// <summary>
/// Deterministic position sizing implementation shared by backtests and future paper/live flows.
/// It is deliberately independent of strategy and broker code.
/// </summary>
public sealed class RiskAwarePositionSizer : IPositionSizer
{
    /// <inheritdoc />
    public PositionSizeResult Calculate(PositionSizeRequest request)
    {
        if (request.EntryPrice <= 0m)
            return PositionSizeResult.Reject("Entry price must be positive.", request.Method);
        if (request.AvailableCapital <= 0m)
            return PositionSizeResult.Reject("No capital is available.", request.Method);
        if (request.MaxCapitalPerTrade <= 0m || request.MaxExposurePerSymbol <= 0m)
            return PositionSizeResult.Reject("Capital and exposure limits must be positive.", request.Method);
        if (request.CurrentSymbolExposure < 0m)
            return PositionSizeResult.Reject("Current symbol exposure cannot be negative.", request.Method);

        var exposureCapacity = request.MaxExposurePerSymbol - request.CurrentSymbolExposure;
        if (exposureCapacity <= 0m)
            return PositionSizeResult.Reject("Maximum exposure for the symbol has been reached.", request.Method);

        var capitalCapacity = Math.Min(request.AvailableCapital, Math.Min(request.MaxCapitalPerTrade, exposureCapacity));
        if (capitalCapacity < request.EntryPrice)
            return PositionSizeResult.Reject("Capital limits cannot fund one share.", request.Method);

        int quantity;
        switch (request.Method)
        {
            case PositionSizingMethod.FixedCapital:
                quantity = FloorToQuantity(capitalCapacity / request.EntryPrice);
                break;

            case PositionSizingMethod.PercentOfCapital:
                if (request.PercentOfCapital <= 0m || request.PercentOfCapital > 1m)
                    return PositionSizeResult.Reject("Percent of capital must be in the range (0, 1].", request.Method);
                quantity = FloorToQuantity(Math.Min(capitalCapacity, request.AvailableCapital * request.PercentOfCapital) / request.EntryPrice);
                break;

            case PositionSizingMethod.RiskBased:
                if (request.StopPrice <= 0m || request.StopPrice >= request.EntryPrice)
                    return PositionSizeResult.Reject("Long risk-based sizing requires a positive stop below entry price.", request.Method);
                if (request.MaxRiskPerTrade <= 0m)
                    return PositionSizeResult.Reject("Maximum monetary risk per trade must be positive.", request.Method);

                var riskPerShare = request.EntryPrice - request.StopPrice;
                var riskLimitedQuantity = FloorToQuantity(request.MaxRiskPerTrade / riskPerShare);
                var capitalLimitedQuantity = FloorToQuantity(capitalCapacity / request.EntryPrice);
                quantity = Math.Min(riskLimitedQuantity, capitalLimitedQuantity);
                break;

            default:
                return PositionSizeResult.Reject("Unknown position sizing method.", request.Method);
        }

        if (quantity <= 0)
            return PositionSizeResult.Reject("Sizing limits result in zero quantity.", request.Method);

        var notional = quantity * request.EntryPrice;
        var riskAmount = request.StopPrice < request.EntryPrice
            ? quantity * (request.EntryPrice - request.StopPrice)
            : 0m;
        return new PositionSizeResult(quantity, notional, riskAmount, request.Method);
    }

    private static int FloorToQuantity(decimal value) => value >= int.MaxValue ? int.MaxValue : (int)Math.Floor(value);
}
