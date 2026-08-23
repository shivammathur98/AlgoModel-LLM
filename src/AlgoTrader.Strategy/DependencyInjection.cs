namespace AlgoTrader.Strategy;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Domain.Strategy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>Registers the strategy layer with the DI container.</summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the single active <see cref="IStrategy"/> chosen by configuration key
    /// <c>Strategy:Name</c>. Recognized values are <c>"MomentumBreakoutV1"</c> (default when the key is
    /// absent) and <c>"TrendAlignedPullbackV1"</c>. Each strategy reads its own settings section, so both
    /// can be configured simultaneously while exactly one is instantiated. An unrecognized name fails fast
    /// rather than silently running the wrong hypothesis.
    /// </summary>
    public static IServiceCollection AddActiveStrategy(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var name = configuration.GetValue<string>($"{StrategySettings.SectionName}:{nameof(StrategySettings.Name)}");
        name = string.IsNullOrWhiteSpace(name) ? "MomentumBreakoutV1" : name.Trim();

        return name switch
        {
            "MomentumBreakoutV1" => services.AddMomentumBreakoutStrategy(),
            "TrendAlignedPullbackV1" => services.AddTrendAlignedPullbackStrategy(),
            _ => throw new InvalidOperationException(
                $"Unknown Strategy:Name '{name}'. Expected 'MomentumBreakoutV1' or 'TrendAlignedPullbackV1'.")
        };
    }

    /// <summary>
    /// Registers <see cref="MomentumBreakoutV1"/> as the active <see cref="IStrategy"/>, deriving its
    /// typed parameters from the validated <see cref="StrategySettings"/> options. Registered as a
    /// singleton: one long-lived strategy instance owns the running per-instrument daily trade tally
    /// across the continuous live/paper candle feed. Backtests construct their own instance per run.
    /// </summary>
    public static IServiceCollection AddMomentumBreakoutStrategy(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
            MomentumBreakoutParameters.FromSettings(sp.GetRequiredService<IOptions<StrategySettings>>().Value));
        services.AddSingleton<IStrategy, MomentumBreakoutV1>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="TrendAlignedPullbackV1"/> as the active <see cref="IStrategy"/>, deriving its
    /// typed parameters from the validated <see cref="SwingStrategySettings"/> options. Registered as a
    /// singleton: one long-lived instance owns the running per-instrument daily entry tally across the
    /// continuous live/paper candle feed. Backtests construct their own instance per run.
    /// <para>
    /// This is an alternative to <see cref="AddMomentumBreakoutStrategy"/>; a host should register exactly
    /// one strategy (the composition root selects which, e.g. from configuration).
    /// </para>
    /// </summary>
    public static IServiceCollection AddTrendAlignedPullbackStrategy(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
            TrendAlignedPullbackParameters.FromSettings(sp.GetRequiredService<IOptions<SwingStrategySettings>>().Value));
        services.AddSingleton<IStrategy, TrendAlignedPullbackV1>();
        return services;
    }
}
