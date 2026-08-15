namespace AlgoTrader.Application;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Application.Safety;
using AlgoTrader.Application.Services;
using AlgoTrader.Application.Status;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers all Application-layer services and binds strongly typed configuration (§30).
/// Called from the composition root (Api/Program.cs).
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Adds Application services to the DI container. Binds and validates all settings sections.
    /// Live trading misconfiguration causes startup failure via ValidateOnStart.
    /// </summary>
    public static IServiceCollection AddAlgoTraderApplication(this IServiceCollection services, IConfiguration configuration)
    {
        // Trading settings with live-trading safety gate
        services.AddOptions<TradingSettings>()
            .Bind(configuration.GetSection(TradingSettings.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                LiveTradingSafetyValidator.StartupConfigurationIsValid,
                "Live trading is misconfigured. When Trading:Mode=Live, both Trading:EnableLiveTrading=true and Trading:LiveTradingAcknowledgement='I-ACCEPT-LIVE-TRADING-RISK' are required.")
            .ValidateOnStart();

        // Risk settings
        services.AddOptions<RiskSettings>()
            .Bind(configuration.GetSection(RiskSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Broker settings
        services.AddOptions<BrokerSettings>()
            .Bind(configuration.GetSection(BrokerSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Market data settings
        services.AddOptions<MarketDataSettings>()
            .Bind(configuration.GetSection(MarketDataSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Strategy settings
        services.AddOptions<StrategySettings>()
            .Bind(configuration.GetSection(StrategySettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Backtest settings
        services.AddOptions<BacktestSettings>()
            .Bind(configuration.GetSection(BacktestSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Cost settings
        services.AddOptions<CostSettings>()
            .Bind(configuration.GetSection(CostSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Slippage settings
        services.AddOptions<SlippageSettings>()
            .Bind(configuration.GetSection(SlippageSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Logging settings
        services.AddOptions<LoggingSettings>()
            .Bind(configuration.GetSection(LoggingSettings.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Application services
        services.AddSingleton<LiveTradingSafetyValidator>();
        services.AddSingleton<IKillSwitch, KillSwitchService>();
        services.AddSingleton<ISystemStatusService, SystemStatusService>();
        services.AddScoped<HistoricalCandleBackfillService>();

        return services;
    }
}
