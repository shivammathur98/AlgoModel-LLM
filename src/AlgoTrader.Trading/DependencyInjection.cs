namespace AlgoTrader.Trading;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Application.Observability;
using AlgoTrader.Domain.Costing;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Risk;
using AlgoTrader.Domain.Sizing;
using AlgoTrader.Domain.Strategy;
using AlgoTrader.Domain.MarketData;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>DI registration for the Trading layer (§8, §11).</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the <see cref="TradingLoopService"/> hosted service, the in-memory <see cref="PaperPortfolio"/>
    /// ledger, and the <see cref="PaperTradingCycle"/>. The loop self-gates on trading mode and configuration, so
    /// it is safe to register unconditionally — it idles in Research/Backtest and when credentials or a universe
    /// are missing. The portfolio and cycle are singletons (one running paper book per host); the portfolio
    /// safely captures the singleton cost calculator, and the cycle resolves the scoped execution engine per
    /// operation via <see cref="IServiceScopeFactory"/>.
    /// </summary>
    public static IServiceCollection AddAlgoTraderTrading(this IServiceCollection services)
    {
        services.AddSingleton<IPaperPortfolio>(sp => new PaperPortfolio(
            sp.GetRequiredService<IOptions<TradingSettings>>().Value.StartingCapital,
            sp.GetRequiredService<ITradingCostCalculator>()));

        // Product for paper orders: Intraday (MIS) — the default MomentumBreakoutV1 is an intraday strategy.
        // Per-strategy product selection (e.g. Delivery/CNC for the swing strategy) is a later refinement.
        services.AddSingleton<IPaperTradingCycle>(sp => new PaperTradingCycle(
            sp.GetRequiredService<IStrategy>(),
            sp.GetRequiredService<IRiskEngine>(),
            sp.GetRequiredService<IPositionSizer>(),
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IPaperPortfolio>(),
            sp.GetRequiredService<ICandleAggregator>(),
            sp.GetRequiredService<IOptions<StrategySettings>>(),
            sp.GetRequiredService<IOptions<RiskSettings>>(),
            sp.GetRequiredService<IOptions<MarketDataSettings>>(),
            ProductType.Intraday,
            sp.GetRequiredService<ILogger<PaperTradingCycle>>(),
            sp.GetRequiredService<ITradingMetrics>()));

        // Live account state (§26): broker-truth positions/funds plus local order provenance, for the live
        // decision cycle. Stateless — a singleton assembler the cycle hands its session-scope broker/repo to.
        services.AddSingleton<ILiveAccountView, LiveAccountView>();

        // Daily reconciliation (§26, §28): compares our local order/position record against broker truth. A
        // stateless, read-only assembler; run against the authenticated session broker (e.g. at end of day).
        services.AddSingleton<ILiveReconciler, LiveReconciler>();

        // Live decision cycle (§8, §11, §26). Singleton, mirroring the paper cycle. It takes NO scope factory:
        // every broker read and order submission must run on the loop's authenticated session scope (a fresh
        // scope's broker is unauthenticated), which the loop binds via Attach after the broker authenticates.
        services.AddSingleton<ILiveTradingCycle>(sp => new LiveTradingCycle(
            sp.GetRequiredService<IStrategy>(),
            sp.GetRequiredService<IRiskEngine>(),
            sp.GetRequiredService<IPositionSizer>(),
            sp.GetRequiredService<ILiveAccountView>(),
            sp.GetRequiredService<ICandleAggregator>(),
            sp.GetRequiredService<IOptions<StrategySettings>>(),
            sp.GetRequiredService<IOptions<RiskSettings>>(),
            sp.GetRequiredService<IOptions<MarketDataSettings>>(),
            ProductType.Intraday,
            sp.GetRequiredService<ILogger<LiveTradingCycle>>(),
            sp.GetRequiredService<ITradingMetrics>()));

        services.AddHostedService<TradingLoopService>();
        return services;
    }
}
