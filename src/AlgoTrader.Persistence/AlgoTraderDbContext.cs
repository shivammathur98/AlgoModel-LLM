namespace AlgoTrader.Persistence;

using AlgoTrader.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

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

    // Orders
    public DbSet<OrderEntity> Orders => Set<OrderEntity>();
    public DbSet<OrderExecutionEntity> OrderExecutions => Set<OrderExecutionEntity>();

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
    }
}
