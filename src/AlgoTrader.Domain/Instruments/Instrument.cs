namespace AlgoTrader.Domain.Instruments;

/// <summary>
/// Immutable description of a tradable instrument. The broker-assigned
/// <see cref="InstrumentToken"/> is the stable identifier used for market data and orders.
/// </summary>
public sealed record Instrument(
    int InstrumentToken,
    string Symbol,
    string Exchange,
    string Segment,
    string Name,
    decimal TickSize,
    int LotSize)
{
    /// <summary>Convenience factory for NSE equity instruments (lot size 1, default tick 0.05).</summary>
    public static Instrument NseEquity(int instrumentToken, string symbol, string name, decimal tickSize = 0.05m)
        => new(instrumentToken, symbol, "NSE", "EQ", name, tickSize, 1);
}
