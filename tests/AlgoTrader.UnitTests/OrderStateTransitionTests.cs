namespace AlgoTrader.UnitTests;

using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Orders;
using FluentAssertions;
using Xunit;

public class OrderStateTransitionTests
{
    [Theory]
    [InlineData(OrderState.New, OrderState.Pending)]
    [InlineData(OrderState.New, OrderState.Submitted)]
    [InlineData(OrderState.New, OrderState.Rejected)]
    [InlineData(OrderState.New, OrderState.Failed)]
    [InlineData(OrderState.Pending, OrderState.Submitted)]
    [InlineData(OrderState.Pending, OrderState.Open)]
    [InlineData(OrderState.Pending, OrderState.CancelPending)]
    [InlineData(OrderState.Pending, OrderState.Rejected)]
    [InlineData(OrderState.Pending, OrderState.Failed)]
    [InlineData(OrderState.Submitted, OrderState.Open)]
    [InlineData(OrderState.Submitted, OrderState.PartiallyFilled)]
    [InlineData(OrderState.Submitted, OrderState.Filled)]
    [InlineData(OrderState.Submitted, OrderState.CancelPending)]
    [InlineData(OrderState.Submitted, OrderState.Cancelled)]
    [InlineData(OrderState.Submitted, OrderState.Rejected)]
    [InlineData(OrderState.Submitted, OrderState.Failed)]
    [InlineData(OrderState.Open, OrderState.PartiallyFilled)]
    [InlineData(OrderState.Open, OrderState.Filled)]
    [InlineData(OrderState.Open, OrderState.CancelPending)]
    [InlineData(OrderState.Open, OrderState.Rejected)]
    [InlineData(OrderState.Open, OrderState.Failed)]
    [InlineData(OrderState.PartiallyFilled, OrderState.PartiallyFilled)]
    [InlineData(OrderState.PartiallyFilled, OrderState.Filled)]
    [InlineData(OrderState.PartiallyFilled, OrderState.CancelPending)]
    [InlineData(OrderState.PartiallyFilled, OrderState.Rejected)]
    [InlineData(OrderState.PartiallyFilled, OrderState.Failed)]
    [InlineData(OrderState.CancelPending, OrderState.Cancelled)]
    [InlineData(OrderState.CancelPending, OrderState.Open)]
    [InlineData(OrderState.CancelPending, OrderState.PartiallyFilled)]
    [InlineData(OrderState.CancelPending, OrderState.Filled)]
    [InlineData(OrderState.CancelPending, OrderState.Failed)]
    public void ValidTransitions_AreAccepted(OrderState from, OrderState to)
    {
        from.IsValidTransition(to).Should().BeTrue();
    }

    [Theory]
    [InlineData(OrderState.New, OrderState.Filled)]
    [InlineData(OrderState.New, OrderState.Cancelled)]
    [InlineData(OrderState.New, OrderState.Open)]
    [InlineData(OrderState.Filled, OrderState.Open)]
    [InlineData(OrderState.Filled, OrderState.New)]
    [InlineData(OrderState.Cancelled, OrderState.Open)]
    [InlineData(OrderState.Rejected, OrderState.Submitted)]
    [InlineData(OrderState.Failed, OrderState.Pending)]
    [InlineData(OrderState.Open, OrderState.New)]
    public void InvalidTransitions_AreRejected(OrderState from, OrderState to)
    {
        from.IsValidTransition(to).Should().BeFalse();
    }

    [Theory]
    [InlineData(OrderState.Filled)]
    [InlineData(OrderState.Cancelled)]
    [InlineData(OrderState.Rejected)]
    [InlineData(OrderState.Failed)]
    public void TerminalStates_HaveNoOutgoingTransitions(OrderState terminal)
    {
        terminal.IsTerminal().Should().BeTrue();

        foreach (var state in Enum.GetValues<OrderState>())
        {
            terminal.IsValidTransition(state).Should().BeFalse($"terminal state {terminal} must not transition to {state}");
        }
    }

    [Fact]
    public void Order_TryTransitionTo_AppliesValidTransition()
    {
        var order = new Order { State = OrderState.New };
        order.TryTransitionTo(OrderState.Pending).Should().BeTrue();
        order.State.Should().Be(OrderState.Pending);
    }

    [Fact]
    public void Order_TryTransitionTo_RejectsInvalidTransition()
    {
        var order = new Order { State = OrderState.New };
        order.TryTransitionTo(OrderState.Filled).Should().BeFalse();
        order.State.Should().Be(OrderState.New);
    }

    [Fact]
    public void Order_IsTerminal_ReflectsState()
    {
        var order = new Order { State = OrderState.Open };
        order.IsTerminal.Should().BeFalse();

        order.TryTransitionTo(OrderState.Filled);
        order.IsTerminal.Should().BeTrue();
    }
}
