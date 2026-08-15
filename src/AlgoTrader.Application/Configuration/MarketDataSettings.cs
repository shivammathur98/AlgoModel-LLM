namespace AlgoTrader.Application.Configuration;

using System.ComponentModel.DataAnnotations;
using AlgoTrader.Domain.Enums;

/// <summary>Market data and instrument universe settings (§7, §9).</summary>
public sealed class MarketDataSettings
{
    public const string SectionName = "MarketData";

    /// <summary>Default exchange segment.</summary>
    public string Exchange { get; set; } = "NSE";

    /// <summary>Segment within the exchange.</summary>
    public string Segment { get; set; } = "EQ";

    /// <summary>Bar size used for initial strategy research.</summary>
    public Timeframe DefaultTimeframe { get; set; } = Timeframe.Minute5;

    /// <summary>Page size used when paginating historical candle fetches.</summary>
    [Range(1, 300)]
    public int HistoricalFetchPageSize { get; set; } = 100;

    /// <summary>Instrument universe configuration. Configurable, never hardcoded (§9).</summary>
    public UniverseSettings Universe { get; set; } = new();
}

/// <summary>
/// Trading universe definition (§9). An explicit symbol list takes precedence over filters.
/// Filters are deliberately simple initially; liquidity/ATR/spread/market-cap filters are added later.
/// </summary>
public sealed class UniverseSettings
{
    /// <summary>Explicit watchlist of NSE symbols. When non-empty, filters are ignored.</summary>
    public List<string> Symbols { get; set; } = new();

    /// <summary>Initial research price ceiling in INR. A low price is NOT a claim of better opportunity.</summary>
    [Range(0.0, 1_000_000.0)]
    public decimal MaxPrice { get; set; } = 500m;

    /// <summary>Exclude penny/dust symbols.</summary>
    [Range(0.0, 1_000_000.0)]
    public decimal MinPrice { get; set; } = 10m;

    /// <summary>Minimum average daily volume for a symbol to be considered liquid enough.</summary>
    [Range(typeof(long), "0", "9223372036854775807")]
    public long MinAverageDailyVolume { get; set; } = 500_000;

    /// <summary>Maximum number of symbols traded simultaneously.</summary>
    [Range(1, 500)]
    public int MaxSymbols { get; set; } = 20;
}
