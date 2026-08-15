namespace AlgoTrader.Domain.Enums;

/// <summary>Indian exchanges supported by the platform.</summary>
public enum Exchange
{
    /// <summary>National Stock Exchange of India.</summary>
    Nse,

    /// <summary>Bombay Stock Exchange.</summary>
    Bse
}

/// <summary>Helpers for <see cref="Exchange"/>.</summary>
public static class ExchangeExtensions
{
    /// <summary>Broker/exchange segment code (e.g. "NSE").</summary>
    public static string ToCode(this Exchange exchange) => exchange switch
    {
        Exchange.Nse => "NSE",
        Exchange.Bse => "BSE",
        _ => throw new ArgumentOutOfRangeException(nameof(exchange), exchange, "Unknown exchange.")
    };
}

/// <summary>Candle bar size.</summary>
public enum Timeframe
{
    Minute1,
    Minute5,
    Minute15,
    Minute30,
    Minute60,
    Daily
}

/// <summary>Helpers for <see cref="Timeframe"/>.</summary>
public static class TimeframeExtensions
{
    /// <summary>Bar length in minutes. A daily bar counts as one full 24-hour day.</summary>
    public static int Minutes(this Timeframe timeframe) => timeframe switch
    {
        Timeframe.Minute1 => 1,
        Timeframe.Minute5 => 5,
        Timeframe.Minute15 => 15,
        Timeframe.Minute30 => 30,
        Timeframe.Minute60 => 60,
        Timeframe.Daily => 1440,
        _ => throw new ArgumentOutOfRangeException(nameof(timeframe), timeframe, "Unknown timeframe.")
    };
}
