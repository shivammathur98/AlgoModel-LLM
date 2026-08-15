namespace AlgoTrader.Domain.Trading;

/// <summary>
/// Exchange session calendar abstraction (§27). Never hardcode session times in
/// strategy or risk code; query the calendar instead. Initially models the NSE
/// equity session; holidays and special sessions (e.g. muhurat) are added later.
/// </summary>
public interface ITradingCalendar
{
    /// <summary>True when the given date is a regular trading day.</summary>
    bool IsTradingDay(DateTimeOffset dateUtc);

    /// <summary>True when the timestamp falls inside a live trading session.</summary>
    bool IsSessionOpen(DateTimeOffset timestampUtc);

    /// <summary>Returns the trading session for the given date, or null on non-trading days.</summary>
    TradingSession? GetSession(DateTimeOffset dateUtc);
}

/// <summary>A continuous trading session window, expressed in UTC.</summary>
public sealed record TradingSession(DateTimeOffset StartUtc, DateTimeOffset EndUtc);
