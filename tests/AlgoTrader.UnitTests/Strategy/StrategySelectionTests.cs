namespace AlgoTrader.UnitTests.Strategy;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Domain.Strategy;
using AlgoTrader.Strategy;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>Verifies the composition root selects the correct single <see cref="IStrategy"/> from config.</summary>
public sealed class StrategySelectionTests
{
    [Fact]
    public void DefaultsToMomentumBreakout_WhenNameAbsent()
    {
        using var provider = BuildProvider(strategyName: null);

        provider.GetRequiredService<IStrategy>().Should().BeOfType<MomentumBreakoutV1>();
    }

    [Fact]
    public void SelectsMomentumBreakout_WhenNamed()
    {
        using var provider = BuildProvider("MomentumBreakoutV1");

        provider.GetRequiredService<IStrategy>().Should().BeOfType<MomentumBreakoutV1>();
    }

    [Fact]
    public void SelectsTrendAlignedPullback_WhenNamed()
    {
        using var provider = BuildProvider("TrendAlignedPullbackV1");

        var strategy = provider.GetRequiredService<IStrategy>();
        strategy.Should().BeOfType<TrendAlignedPullbackV1>();
        strategy.Name.Should().Be("TrendAlignedPullbackV1");
    }

    [Fact]
    public void RegistersExactlyOneStrategy()
    {
        using var provider = BuildProvider("TrendAlignedPullbackV1");

        provider.GetServices<IStrategy>().Should().ContainSingle();
    }

    [Fact]
    public void Throws_OnUnknownStrategyName()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Strategy:Name"] = "NoSuchStrategy" })
            .Build();

        var act = () => new ServiceCollection().AddActiveStrategy(configuration);

        act.Should().Throw<InvalidOperationException>().WithMessage("*NoSuchStrategy*");
    }

    private static ServiceProvider BuildProvider(string? strategyName)
    {
        var settings = new Dictionary<string, string?>();
        if (strategyName is not null) settings["Strategy:Name"] = strategyName;

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddOptions<StrategySettings>().Bind(configuration.GetSection(StrategySettings.SectionName));
        services.AddOptions<SwingStrategySettings>().Bind(configuration.GetSection(SwingStrategySettings.SectionName));
        services.AddActiveStrategy(configuration);
        return services.BuildServiceProvider();
    }
}
