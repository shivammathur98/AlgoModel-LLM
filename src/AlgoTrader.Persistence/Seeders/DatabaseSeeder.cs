namespace AlgoTrader.Persistence.Seeders;

using AlgoTrader.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Seeds the database with initial reference data: NSE instruments and trading calendar.
/// Called during startup or via a CLI command. Idempotent — safe to run multiple times.
/// </summary>
public static class DatabaseSeeder
{
    /// <summary>Seeds all reference data.</summary>
    public static async Task SeedAsync(AlgoTraderDbContext db, ILogger logger, CancellationToken cancellationToken = default)
    {
        await db.Database.MigrateAsync(cancellationToken);

        await SeedInstrumentsAsync(db, logger, cancellationToken);
        await SeedTradingCalendarAsync(db, logger, cancellationToken);

        logger.LogInformation("Database seeding complete");
    }

    private static async Task SeedInstrumentsAsync(AlgoTraderDbContext db, ILogger logger, CancellationToken ct)
    {
        if (await db.Instruments.AnyAsync(ct))
        {
            logger.LogDebug("Instruments already seeded — skipping");
            return;
        }

        // Initial liquid NSE equity universe (price < ₹500 filter applied later via configuration).
        // Instrument tokens are placeholder values; real tokens come from the Kite instruments dump.
        var instruments = new List<InstrumentEntity>
        {
            Create("RELIANCE", "Reliance Industries", 2885, 0.05m),
            Create("TCS", "Tata Consultancy Services", 2953, 0.05m),
            Create("HDFCBANK", "HDFC Bank", 3412, 0.05m),
            Create("INFY", "Infosys", 4080, 0.05m),
            Create("ICICIBANK", "ICICI Bank", 1270, 0.05m),
            Create("SBIN", "State Bank of India", 3045, 0.05m),
            Create("BHARTIARTL", "Bharti Airtel", 2714, 0.05m),
            Create("ITC", "ITC Limited", 4249, 0.05m),
            Create("KOTAKBANK", "Kotak Mahindra Bank", 4920, 0.05m),
            Create("LT", "Larsen & Toubro", 1146, 0.05m),
            Create("HCLTECH", "HCL Technologies", 2329, 0.05m),
            Create("WIPRO", "Wipro", 969, 0.05m),
            Create("TATAMOTORS", "Tata Motors", 3456, 0.05m),
            Create("AXISBANK", "Axis Bank", 1510, 0.05m),
            Create("ADANIENT", "Adani Enterprises", 25, 0.05m),
            Create("POWERGRID", "Power Grid Corp", 3834, 0.05m),
            Create("NIFTY", "Nifty 50 Index", 256265, 0.05m),
            Create("BANKNIFTY", "Nifty Bank Index", 260105, 0.05m)
        };

        var now = DateTimeOffset.UtcNow;
        foreach (var inst in instruments)
        {
            inst.CreatedAtUtc = now;
            inst.UpdatedAtUtc = now;
        }

        await db.Instruments.AddRangeAsync(instruments, ct);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded {Count} instruments", instruments.Count);
    }

    private static InstrumentEntity Create(string symbol, string name, int token, decimal tickSize) => new()
    {
        InstrumentToken = token,
        Symbol = symbol,
        Exchange = "NSE",
        Segment = "EQ",
        Name = name,
        TickSize = tickSize,
        LotSize = 1,
        IsTradable = true
    };

    private static async Task SeedTradingCalendarAsync(AlgoTraderDbContext db, ILogger logger, CancellationToken ct)
    {
        if (await db.TradingDays.AnyAsync(ct))
        {
            logger.LogDebug("Trading calendar already seeded — skipping");
            return;
        }

        // Generate trading days for 2025-2026 (weekdays only, excluding known holidays).
        // A production system would load holidays from a configuration or exchange API.
        var knownHolidays2026 = new HashSet<DateOnly>
        {
            new(2026, 1, 26),  // Republic Day
            new(2026, 3, 2),   // Holi
            new(2026, 3, 25),  // Ram Navami
            new(2026, 4, 14),  // Ambedkar Jayanti
            new(2026, 5, 1),   // May Day
            new(2026, 8, 15),  // Independence Day
            new(2026, 10, 2),  // Gandhi Jayanti
            new(2026, 10, 21), // Diwali (estimated)
            new(2026, 12, 25)  // Christmas
        };

        var days = new List<TradingDayEntity>();
        var start = new DateOnly(2025, 1, 1);
        var end = new DateOnly(2026, 12, 31);

        // IST = UTC + 5:30. NSE session: 09:15-15:30 IST = 03:45-10:00 UTC
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            var isWeekend = d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var isHoliday = knownHolidays2026.Contains(d);
            var isTrading = !isWeekend && !isHoliday;

            DateTimeOffset? sessionStart = null;
            DateTimeOffset? sessionEnd = null;

            if (isTrading)
            {
                // 09:15 IST = 03:45 UTC
                sessionStart = new DateTimeOffset(d.Year, d.Month, d.Day, 3, 45, 0, TimeSpan.Zero);
                // 15:30 IST = 10:00 UTC
                sessionEnd = new DateTimeOffset(d.Year, d.Month, d.Day, 10, 0, 0, TimeSpan.Zero);
            }

            days.Add(new TradingDayEntity
            {
                Date = d,
                IsTradingDay = isTrading,
                SessionStartUtc = sessionStart,
                SessionEndUtc = sessionEnd
            });
        }

        await db.TradingDays.AddRangeAsync(days, ct);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded {Count} trading calendar days", days.Count);
    }
}
