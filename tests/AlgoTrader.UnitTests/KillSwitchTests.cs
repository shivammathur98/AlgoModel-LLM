namespace AlgoTrader.UnitTests;

using AlgoTrader.Application.Safety;
using AlgoTrader.Domain.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

public class KillSwitchTests
{
    private readonly FakeClock _clock = new();
    private readonly ILogger<KillSwitchService> _logger = NullLogger<KillSwitchService>.Instance;

    private KillSwitchService CreateService() => new(_clock, _logger);

    [Fact]
    public void Initially_Disengaged()
    {
        var service = CreateService();

        service.IsEngaged.Should().BeFalse();
        service.State.Should().Be(KillSwitchState.Disengaged);
        service.Reason.Should().BeNull();
    }

    [Fact]
    public void Engage_SetsStateAndReason()
    {
        var service = CreateService();

        service.Engage("daily loss breached", "risk-engine");

        service.IsEngaged.Should().BeTrue();
        service.State.Should().Be(KillSwitchState.Engaged);
        service.Reason.Should().Be("daily loss breached");
    }

    [Fact]
    public void Engage_FiresEvent()
    {
        var service = CreateService();
        var events = new List<KillSwitchEventArgs>();
        service.StateChanged += (_, e) => events.Add(e);

        service.Engage("test", "operator");

        events.Should().ContainSingle();
        events[0].State.Should().Be(KillSwitchState.Engaged);
        events[0].Reason.Should().Be("test");
        events[0].InitiatedBy.Should().Be("operator");
    }

    [Fact]
    public void Engage_IsIdempotent_SecondEngageDoesNotFireEvent()
    {
        var service = CreateService();
        service.Engage("first", "system");

        var events = new List<KillSwitchEventArgs>();
        service.StateChanged += (_, e) => events.Add(e);

        service.Engage("second", "system");

        events.Should().BeEmpty();
        service.Reason.Should().Be("first");
    }

    [Fact]
    public void Reset_ClearsStateAndFiresEvent()
    {
        var service = CreateService();
        service.Engage("reason", "system");

        var events = new List<KillSwitchEventArgs>();
        service.StateChanged += (_, e) => events.Add(e);

        service.Reset("admin");

        service.IsEngaged.Should().BeFalse();
        service.State.Should().Be(KillSwitchState.Disengaged);
        service.Reason.Should().BeNull();

        events.Should().ContainSingle();
        events[0].State.Should().Be(KillSwitchState.Disengaged);
        events[0].InitiatedBy.Should().Be("admin");
    }

    [Fact]
    public void Reset_WhenAlreadyDisengaged_DoesNotFireEvent()
    {
        var service = CreateService();
        var events = new List<KillSwitchEventArgs>();
        service.StateChanged += (_, e) => events.Add(e);

        service.Reset("admin");

        events.Should().BeEmpty();
    }

    private sealed class FakeClock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);
    }
}
