namespace AlgoTrader.UnitTests;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Application.Safety;
using AlgoTrader.Domain.Enums;
using FluentAssertions;
using Xunit;

public class LiveTradingSafetyValidatorTests
{
    private readonly LiveTradingSafetyValidator _validator = new();

    [Fact]
    public void ValidateForLiveTrading_AllGatesSatisfied_ReturnsSuccess()
    {
        var settings = new TradingSettings
        {
            Mode = TradingMode.Live,
            EnableLiveTrading = true,
            LiveTradingAcknowledgement = TradingSettings.RequiredLiveAcknowledgement
        };

        var result = _validator.ValidateForLiveTrading(settings);

        result.IsValid.Should().BeTrue();
        result.Failures.Should().BeEmpty();
    }

    [Fact]
    public void ValidateForLiveTrading_ModeNotLive_Fails()
    {
        var settings = new TradingSettings
        {
            Mode = TradingMode.Paper,
            EnableLiveTrading = true,
            LiveTradingAcknowledgement = TradingSettings.RequiredLiveAcknowledgement
        };

        var result = _validator.ValidateForLiveTrading(settings);

        result.IsValid.Should().BeFalse();
        result.Failures.Should().ContainSingle(f => f.Contains("Mode"));
    }

    [Fact]
    public void ValidateForLiveTrading_EnableLiveTradingFalse_Fails()
    {
        var settings = new TradingSettings
        {
            Mode = TradingMode.Live,
            EnableLiveTrading = false,
            LiveTradingAcknowledgement = TradingSettings.RequiredLiveAcknowledgement
        };

        var result = _validator.ValidateForLiveTrading(settings);

        result.IsValid.Should().BeFalse();
        result.Failures.Should().ContainSingle(f => f.Contains("EnableLiveTrading"));
    }

    [Fact]
    public void ValidateForLiveTrading_WrongAcknowledgement_Fails()
    {
        var settings = new TradingSettings
        {
            Mode = TradingMode.Live,
            EnableLiveTrading = true,
            LiveTradingAcknowledgement = "wrong-phrase"
        };

        var result = _validator.ValidateForLiveTrading(settings);

        result.IsValid.Should().BeFalse();
        result.Failures.Should().ContainSingle(f => f.Contains("LiveTradingAcknowledgement"));
    }

    [Fact]
    public void ValidateForLiveTrading_EmptyAcknowledgement_Fails()
    {
        var settings = new TradingSettings
        {
            Mode = TradingMode.Live,
            EnableLiveTrading = true,
            LiveTradingAcknowledgement = string.Empty
        };

        var result = _validator.ValidateForLiveTrading(settings);

        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void ValidateForLiveTrading_AllGatesMissing_AccumulatesAllFailures()
    {
        var settings = new TradingSettings
        {
            Mode = TradingMode.Backtest,
            EnableLiveTrading = false,
            LiveTradingAcknowledgement = string.Empty
        };

        var result = _validator.ValidateForLiveTrading(settings);

        result.IsValid.Should().BeFalse();
        result.Failures.Should().HaveCount(3);
    }

    [Theory]
    [InlineData(TradingMode.Research, false, "", true)]
    [InlineData(TradingMode.Backtest, false, "", true)]
    [InlineData(TradingMode.Paper, false, "", true)]
    [InlineData(TradingMode.Live, false, "", false)]
    [InlineData(TradingMode.Live, true, "", false)]
    [InlineData(TradingMode.Live, true, "I-ACCEPT-LIVE-TRADING-RISK", true)]
    [InlineData(TradingMode.Live, false, "I-ACCEPT-LIVE-TRADING-RISK", false)]
    public void StartupConfigurationIsValid_EnforcesLiveGates(
        TradingMode mode,
        bool enableLive,
        string acknowledgement,
        bool expectedValid)
    {
        var settings = new TradingSettings
        {
            Mode = mode,
            EnableLiveTrading = enableLive,
            LiveTradingAcknowledgement = acknowledgement
        };

        LiveTradingSafetyValidator.StartupConfigurationIsValid(settings).Should().Be(expectedValid);
    }
}
