namespace AlgoTrader.Backtesting;

/// <summary>Immutable, fully-costed record of one closed simulated trade.</summary>
public sealed record BacktestTrade(
    string TradeId,
    string StrategyName,
    string StrategyVersion,
    int InstrumentToken,
    string Symbol,
    DateTimeOffset EntryTimestampUtc,
    decimal EntryPrice,
    DateTimeOffset ExitTimestampUtc,
    decimal ExitPrice,
    int Quantity,
    decimal EntryCharges,
    decimal ExitCharges,
    decimal EntrySlippage,
    decimal ExitSlippage,
    string ExitReason)
{
    /// <summary>Profit from the actual simulated fill prices, before direct trading charges.</summary>
    public decimal GrossPnl => (ExitPrice - EntryPrice) * Quantity;

    /// <summary>Total direct trading charges for both legs.</summary>
    public decimal TotalCharges => EntryCharges + ExitCharges;

    /// <summary>Total price impact recorded by the execution model for both legs.</summary>
    public decimal TotalSlippage => EntrySlippage + ExitSlippage;

    /// <summary>
    /// Profit after direct trading charges. Slippage is already reflected in the simulated entry
    /// and exit prices, so subtracting <see cref="TotalSlippage"/> again would double-count it.
    /// </summary>
    public decimal NetPnl => GrossPnl - TotalCharges;

    /// <summary>Duration for which the position was held.</summary>
    public TimeSpan HoldingTime => ExitTimestampUtc - EntryTimestampUtc;
}
