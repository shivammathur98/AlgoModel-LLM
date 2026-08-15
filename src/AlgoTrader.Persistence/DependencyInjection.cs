namespace AlgoTrader.Persistence;

using AlgoTrader.Application.Repositories;
using AlgoTrader.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>Registers Persistence-layer services (EF Core DbContext + repositories).</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds the AlgoTraderDbContext with SQL Server provider and all repository implementations.
    /// Connection string is read from the "ConnectionStrings:AlgoTrader" configuration key.
    /// </summary>
    public static IServiceCollection AddAlgoTraderPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("AlgoTrader")
            ?? "Server=(localdb)\\mssqllocaldb;Database=AlgoTrader;Trusted_Connection=True;TrustServerCertificate=True;";

        services.AddDbContext<AlgoTraderDbContext>(options =>
            options.UseSqlServer(connectionString, sqlOptions =>
            {
                sqlOptions.MigrationsAssembly(typeof(AlgoTraderDbContext).Assembly.FullName);
                sqlOptions.EnableRetryOnFailure(maxRetryCount: 3);
            }));

        // Repository registrations
        services.AddScoped<IMarketCandleRepository, MarketCandleRepository>();
        services.AddScoped<IInstrumentRepository, InstrumentRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IPositionRepository, PositionRepository>();
        services.AddScoped<IBacktestRunRepository, BacktestRunRepository>();

        return services;
    }
}
