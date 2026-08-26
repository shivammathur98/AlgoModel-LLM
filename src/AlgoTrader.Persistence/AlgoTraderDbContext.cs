namespace AlgoTrader.Persistence;

using AlgoTrader.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>
/// EF Core database context for the AlgoTrader platform.
/// Contains all tables defined in §8.
/// </summary>
public class AlgoTraderDbContext : DbContext
{
    public AlgoTraderDbContext(DbContextOptions<AlgoTraderDbContext> options)
        : base(options)
    {
    }

    // Instruments
    public DbSet<InstrumentEntity> Instruments => Set<InstrumentEntity>();

    // Market data
    public DbSet<MarketCandleEntity> MarketCandles => Set<MarketCandleEntity>();
    public DbSet<MarketTickEntity> MarketTicks => Set<MarketTickEntity>();

    // Trading calendar
    public DbSet<TradingDayEntity> TradingDays => Set<TradingDayEntity>();

    // Strategies
    public DbSet<StrategyEntity> Strategies => Set<StrategyEntity>();
    public DbSet<StrategyVersionEntity> StrategyVersions => Set<StrategyVersionEntity>();

    // Backtesting
    public DbSet<BacktestRunEntity> BacktestRuns => Set<BacktestRunEntity>();
    public DbSet<BacktestTradeEntity> BacktestTrades => Set<BacktestTradeEntity>();

    // Paper/Live trading
    public DbSet<PaperTradeEntity> PaperTrades => Set<PaperTradeEntity>();
    public DbSet<LiveTradeEntity> LiveTrades => Set<LiveTradeEntity>();

    public DbSet<OrderEntity> Orders => Set<OrderEntity>();

    // Portfolio
    public DbSet<PositionEntity> Positions => Set<PositionEntity>();
    public DbSet<PortfolioSnapshotEntity> PortfolioSnapshots => Set<PortfolioSnapshotEntity>();

    // Risk & system events
    public DbSet<RiskEventEntity> RiskEvents => Set<RiskEventEntity>();
    public DbSet<SystemEventEntity> SystemEvents => Set<SystemEventEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Apply all configurations from this assembly
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AlgoTraderDbContext).Assembly);

        // SQLite doesn't support DateTimeOffset natively, so convert to string for storage
        if (Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite")
        {
            var dateTimeOffsetConverter = new ValueConverter<DateTimeOffset, string>(
                v => v.ToString("O"),
                v => DateTimeOffset.Parse(v));

            var nullableDateTimeOffsetConverter = new ValueConverter<DateTimeOffset?, string?>(
                v => v.HasValue ? v.Value.ToString("O") : null,
                v => v != null ? DateTimeOffset.Parse(v) : null);

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(DateTimeOffset))
                    {
                        property.SetValueConverter(dateTimeOffsetConverter);
                    }
                    else if (property.ClrType == typeof(DateTimeOffset?))
                    {
                        property.SetValueConverter(nullableDateTimeOffsetConverter);
                    }
                }
            }
        }
    }
}
