namespace AlgoTrader.UnitTests;

using AlgoTrader.Application;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

public class OptionsValidationTests
{
    [Fact]
    public void DefaultSettings_AreValid()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlgoTraderApplication(config);

        using var provider = services.BuildServiceProvider();

        // Resolving options triggers ValidateOnStart validation
        var trading = provider.GetRequiredService<IOptions<AlgoTrader.Application.Configuration.TradingSettings>>().Value;
        var risk = provider.GetRequiredService<IOptions<AlgoTrader.Application.Configuration.RiskSettings>>().Value;

        trading.Should().NotBeNull();
        trading.Mode.Should().Be(AlgoTrader.Domain.Enums.TradingMode.Backtest);
        trading.StartingCapital.Should().Be(525_000m);
        risk.MaxDailyLoss.Should().Be(5_000m);
    }

    [Fact]
    public void InvalidTradingSettings_StartingCapitalZero_ThrowsOnResolve()
    {
        var overrides = new Dictionary<string, string?>
        {
            ["Trading:StartingCapital"] = "0"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(overrides).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlgoTraderApplication(config);

        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<AlgoTrader.Application.Configuration.TradingSettings>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void LiveModeWithoutEnableLiveTrading_ThrowsOnResolve()
    {
        var overrides = new Dictionary<string, string?>
        {
            ["Trading:Mode"] = "Live",
            ["Trading:EnableLiveTrading"] = "false"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(overrides).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlgoTraderApplication(config);

        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<AlgoTrader.Application.Configuration.TradingSettings>>().Value;

        act.Should().Throw<OptionsValidationException>()
            .Which.Message.Should().Contain("EnableLiveTrading");
    }

    [Fact]
    public void LiveModeWithAllGates_ConfiguresSuccessfully()
    {
        var overrides = new Dictionary<string, string?>
        {
            ["Trading:Mode"] = "Live",
            ["Trading:EnableLiveTrading"] = "true",
            ["Trading:LiveTradingAcknowledgement"] = "I-ACCEPT-LIVE-TRADING-RISK"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(overrides).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlgoTraderApplication(config);

        using var provider = services.BuildServiceProvider();

        var trading = provider.GetRequiredService<IOptions<AlgoTrader.Application.Configuration.TradingSettings>>().Value;
        trading.Mode.Should().Be(AlgoTrader.Domain.Enums.TradingMode.Live);
        trading.EnableLiveTrading.Should().BeTrue();
    }

    [Fact]
    public void InvalidRiskSettings_NegativeMaxDailyLoss_ThrowsOnResolve()
    {
        var overrides = new Dictionary<string, string?>
        {
            ["Risk:MaxDailyLoss"] = "-1000"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(overrides).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlgoTraderApplication(config);

        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<AlgoTrader.Application.Configuration.RiskSettings>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void InvalidStrategySettings_BadTimeFormat_ThrowsOnResolve()
    {
        var overrides = new Dictionary<string, string?>
        {
            ["Strategy:EntryStartTime"] = "25:99"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(overrides).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAlgoTraderApplication(config);

        using var provider = services.BuildServiceProvider();

        var act = () => provider.GetRequiredService<IOptions<AlgoTrader.Application.Configuration.StrategySettings>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }
}
