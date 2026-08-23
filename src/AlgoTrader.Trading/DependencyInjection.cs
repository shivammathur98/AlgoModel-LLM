namespace AlgoTrader.Trading;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>DI registration for the Trading layer (§8, §11).</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the <see cref="TradingLoopService"/> hosted service. It self-gates on trading mode and
    /// configuration, so it is safe to register unconditionally — it idles in Research/Backtest and when
    /// credentials or a universe are missing.
    /// </summary>
    public static IServiceCollection AddAlgoTraderTrading(this IServiceCollection services)
    {
        services.AddHostedService<TradingLoopService>();
        return services;
    }
}
