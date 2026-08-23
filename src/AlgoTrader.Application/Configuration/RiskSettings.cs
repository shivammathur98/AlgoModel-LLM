namespace AlgoTrader.Application.Configuration;

using System.ComponentModel.DataAnnotations;
using System.Globalization;

/// <summary>
/// Risk limits (§10, §14, §15). Every limit is configurable; the risk engine enforces them.
/// </summary>
public sealed class RiskSettings
{
    public const string SectionName = "Risk";

    private const string TimePattern = "^([01][0-9]|2[0-3]):[0-5][0-9]$";

    /// <summary>Maximum capital deployed on any single trade.</summary>
    [Range(0.0, 1_000_000_000.0)]
    public decimal MaxCapitalPerTrade { get; set; } = 100_000m;

    /// <summary>Maximum percentage of starting capital that may be deployed at once.</summary>
    [Range(0.0, 100.0)]
    public decimal MaxCapitalUtilizationPercent { get; set; } = 60m;

    /// <summary>Maximum number of positions open at the same time.</summary>
    [Range(0, 500)]
    public int MaxSimultaneousPositions { get; set; } = 5;

    /// <summary>Maximum monetary risk per trade (entry-to-stop distance × quantity).</summary>
    [Range(0.0, 1_000_000.0)]
    public decimal MaxRiskPerTrade { get; set; } = 1_500m;

    /// <summary>Maximum realized daily loss before trading halts (§15).</summary>
    [Range(0.0, 1_000_000.0)]
    public decimal MaxDailyLoss { get; set; } = 5_000m;

    /// <summary>Maximum completed trades per day.</summary>
    [Range(0, 1000)]
    public int MaxTradesPerDay { get; set; } = 10;

    /// <summary>Maximum simultaneously open (unfilled) orders.</summary>
    [Range(0, 1000)]
    public int MaxOpenOrders { get; set; } = 10;

    /// <summary>Maximum notional exposure to any single symbol.</summary>
    [Range(0.0, 1_000_000_000.0)]
    public decimal MaxExposurePerSymbol { get; set; } = 100_000m;

    /// <summary>Market data older than this many seconds is treated as stale.</summary>
    [Range(1, 3600)]
    public int MarketDataStaleAfterSeconds { get; set; } = 30;

    /// <summary>Start of the tradable session in IST ("HH:mm"). NSE equity opens 09:15.</summary>
    [RegularExpression(TimePattern)]
    public string TradingSessionStartIst { get; set; } = "09:15";

    /// <summary>End of the tradable session in IST ("HH:mm"), exclusive. NSE equity closes 15:30.</summary>
    [RegularExpression(TimePattern)]
    public string TradingSessionEndIst { get; set; } = "15:30";

    /// <summary>Whether to flatten open positions when the daily loss limit is breached (§15).</summary>
    public bool FlattenPositionsOnDailyLossBreach { get; set; } = true;

    /// <summary>After a halt, require an explicit resume instead of restarting automatically (§34).</summary>
    public bool RequireManualResetAfterHalt { get; set; } = true;

    public TimeOnly GetTradingSessionStartIst() => TimeOnly.Parse(TradingSessionStartIst, CultureInfo.InvariantCulture);

    public TimeOnly GetTradingSessionEndIst() => TimeOnly.Parse(TradingSessionEndIst, CultureInfo.InvariantCulture);
}
