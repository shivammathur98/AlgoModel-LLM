namespace AlgoTrader.Risk;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Domain.Risk;
using AlgoTrader.Domain.Sizing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>Registers the risk layer (§14, §15) with the DI container.</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers <see cref="RiskEngine"/> as the singleton <see cref="IRiskEngine"/> — the single pre-trade
    /// authority every signal and order passes through. It reads the validated <see cref="RiskSettings"/>
    /// options and shares the process-wide <see cref="Application.Safety.IKillSwitch"/> (registered by the
    /// Application layer), so the engine sees kill-switch state changes immediately. A singleton is correct:
    /// the engine is stateless and thread-safe, deriving every verdict from the caller-supplied context.
    /// </summary>
    public static IServiceCollection AddAlgoTraderRisk(this IServiceCollection services)
    {
        services.AddSingleton<IRiskEngine>(sp => new RiskEngine(
            sp.GetRequiredService<IOptions<RiskSettings>>().Value,
            sp.GetRequiredService<Application.Safety.IKillSwitch>(),
            sp.GetRequiredService<AlgoTrader.Domain.MarketData.ILastPriceCache>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RiskEngine>>()));

        // Position sizing (§13) is a separate, stateless concern from pre-trade veto: the trading loop uses it to
        // turn an approved entry signal into a share quantity. Registered here alongside the risk engine because
        // both draw on the same RiskSettings limits. A singleton is correct — RiskAwarePositionSizer holds no state.
        services.AddSingleton<IPositionSizer, RiskAwarePositionSizer>();
        return services;
    }
}
