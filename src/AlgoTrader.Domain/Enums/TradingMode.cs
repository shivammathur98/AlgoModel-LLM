namespace AlgoTrader.Domain.Enums;

/// <summary>
/// Operating mode of the platform (§6). The mode determines which subsystems are allowed
/// to interact with the outside world. Real orders are only possible in <see cref="Live"/>,
/// and only after explicit safety validation.
/// </summary>
public enum TradingMode
{
    /// <summary>Data exploration and analysis only. No broker interaction, no simulated execution.</summary>
    Research = 0,

    /// <summary>Deterministic replay of historical data. No broker interaction.</summary>
    Backtest = 1,

    /// <summary>Live market data with simulated order execution. No real orders are ever sent.</summary>
    Paper = 2,

    /// <summary>Real orders are routed to the broker. Requires explicit opt-in and safety validation.</summary>
    Live = 3
}

/// <summary>Capability queries for <see cref="TradingMode"/>.</summary>
public static class TradingModeExtensions
{
    /// <summary>True when the mode may establish a broker session (paper and live).</summary>
    public static bool RequiresBrokerConnection(this TradingMode mode)
        => mode is TradingMode.Paper or TradingMode.Live;

    /// <summary>True when the mode consumes live market data (paper and live).</summary>
    public static bool UsesLiveMarketData(this TradingMode mode)
        => mode is TradingMode.Paper or TradingMode.Live;

    /// <summary>True only for the mode that may transmit real orders. Every other mode must simulate.</summary>
    public static bool AllowsRealOrders(this TradingMode mode)
        => mode == TradingMode.Live;

    /// <summary>True when the mode operates exclusively on historical data (research and backtest).</summary>
    public static bool UsesHistoricalDataOnly(this TradingMode mode)
        => mode is TradingMode.Research or TradingMode.Backtest;

    /// <summary>True when fills are simulated (backtest and paper).</summary>
    public static bool UsesSimulatedExecution(this TradingMode mode)
        => mode is TradingMode.Backtest or TradingMode.Paper;
}
