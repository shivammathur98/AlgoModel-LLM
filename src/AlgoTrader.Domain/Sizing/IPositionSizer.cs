namespace AlgoTrader.Domain.Sizing;

/// <summary>Position sizing method (§13).</summary>
public enum PositionSizingMethod
{
    /// <summary>Deploy a fixed capital amount per trade.</summary>
    FixedCapital,

    /// <summary>Deploy a fixed percentage of available capital.</summary>
    PercentOfCapital,

    /// <summary>Size from maximum allowed monetary risk: quantity = risk budget / risk per share.</summary>
    RiskBased
}

/// <summary>Inputs required to size one entry.</summary>
public sealed record PositionSizeRequest(
    decimal EntryPrice,
    decimal StopPrice,
    decimal AvailableCapital,
    decimal MaxCapitalPerTrade,
    decimal MaxRiskPerTrade,
    decimal MaxExposurePerSymbol,
    decimal CurrentSymbolExposure,
    PositionSizingMethod Method = PositionSizingMethod.RiskBased,
    decimal PercentOfCapital = 0.10m);

/// <summary>Outcome of a sizing calculation.</summary>
public sealed record PositionSizeResult(
    int Quantity,
    decimal Notional,
    decimal RiskAmount,
    PositionSizingMethod Method,
    bool IsRejected = false,
    string? RejectionReason = null)
{
    /// <summary>Creates a rejection result, e.g. when limits or funds prevent any position.</summary>
    public static PositionSizeResult Reject(string reason, PositionSizingMethod method)
        => new(0, 0m, 0m, method, true, reason);
}

/// <summary>
/// Position sizing is independent of strategy logic (§13). Calculated quantity must never
/// exceed available funds, per-trade capital, per-symbol exposure or broker margin.
/// </summary>
public interface IPositionSizer
{
    PositionSizeResult Calculate(PositionSizeRequest request);
}
