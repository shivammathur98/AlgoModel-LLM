namespace AlgoTrader.UnitTests.Execution;

using AlgoTrader.Application.Configuration;
using AlgoTrader.Application.Execution;
using AlgoTrader.Application.Observability;
using AlgoTrader.Application.Repositories;
using AlgoTrader.Application.Safety;
using AlgoTrader.Domain.Broker;
using AlgoTrader.Domain.Common;
using AlgoTrader.Domain.Enums;
using AlgoTrader.Domain.Execution;
using AlgoTrader.Domain.Orders;
using AlgoTrader.Execution;
using AlgoTrader.UnitTests.Observability;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Verifies the execution engine (§8, §11, §25). The load-bearing property is safety: a real broker order
/// is transmitted only when the platform is fully gated for Live (§6, §36); every other mode simulates or
/// refuses and must never call the broker.
/// </summary>
public sealed class OrderExecutionEngineTests
{
    // ---- Simulated modes --------------------------------------------------

    [Fact]
    public async Task PaperLimitOrder_RestsAsOpen_WithoutBroker()
    {
        var broker = new FakeBroker();
        var repo = new InMemoryOrderRepository();
        var engine = Engine(Paper(), broker, repo);

        var result = await engine.SubmitAsync(LimitOrder(quantity: 10, price: 250.50m));

        result.IsAccepted.Should().BeTrue();
        result.State.Should().Be(OrderState.Open);
        result.OrderId.Should().BeGreaterThan(0);
        broker.PlaceOrderCallCount.Should().Be(0);

        var stored = await repo.GetByIdAsync(result.OrderId);
        stored!.State.Should().Be(OrderState.Open);
        stored.FilledQuantity.Should().Be(0);
    }

    [Fact]
    public async Task PaperMarketOrder_RestsAsOpen_WithoutFabricatingPrice()
    {
        var broker = new FakeBroker();
        var engine = Engine(Paper(), broker, new InMemoryOrderRepository());

        var result = await engine.SubmitAsync(MarketOrder(quantity: 10));

        result.IsAccepted.Should().BeTrue();
        result.State.Should().Be(OrderState.Open);
        broker.PlaceOrderCallCount.Should().Be(0);
    }

    [Fact]
    public async Task ResearchMode_RefusesAllExecution()
    {
        var broker = new FakeBroker();
        var repo = new InMemoryOrderRepository();
        var engine = Engine(Mode(TradingMode.Research), broker, repo);

        var result = await engine.SubmitAsync(LimitOrder(quantity: 10, price: 100m));

        result.IsAccepted.Should().BeFalse();
        result.State.Should().Be(OrderState.Rejected);
        result.Message.Should().Contain("Research");
        broker.PlaceOrderCallCount.Should().Be(0);

        // Even a refused order is persisted for audit (§28).
        var stored = await repo.GetByIdAsync(result.OrderId);
        stored!.State.Should().Be(OrderState.Rejected);
    }

    // ---- Live safety gate (§6, §36) — the critical invariant --------------

    [Fact]
    public async Task LiveMode_WithGatesUnsatisfied_RejectsAndNeverTouchesBroker()
    {
        // Mode is Live but the acknowledgement + enable flags are missing: no real order may be sent.
        var broker = new FakeBroker();
        var repo = new InMemoryOrderRepository();
        var settings = new TradingSettings { Mode = TradingMode.Live, EnableLiveTrading = false, LiveTradingAcknowledgement = "" };
        var engine = Engine(settings, broker, repo);

        var result = await engine.SubmitAsync(LimitOrder(quantity: 10, price: 100m));

        result.IsAccepted.Should().BeFalse();
        result.State.Should().Be(OrderState.Rejected);
        broker.PlaceOrderCallCount.Should().Be(0);

        var stored = await repo.GetByIdAsync(result.OrderId);
        stored!.RejectionReason.Should().Contain("EnableLiveTrading");
    }

    [Fact]
    public async Task LiveMode_FullyGated_TransmitsToBroker()
    {
        var broker = new FakeBroker { PlaceResult = new PlaceOrderResult(true, BrokerOrderId: "KITE-777") };
        var repo = new InMemoryOrderRepository();
        var engine = Engine(FullyGatedLive(), broker, repo);

        var result = await engine.SubmitAsync(LimitOrder(quantity: 10, price: 100m));

        result.IsAccepted.Should().BeTrue();
        result.State.Should().Be(OrderState.Submitted);
        result.BrokerOrderId.Should().Be("KITE-777");
        broker.PlaceOrderCallCount.Should().Be(1);

        var stored = await repo.GetByIdAsync(result.OrderId);
        stored!.BrokerOrderId.Should().Be("KITE-777");
        stored.State.Should().Be(OrderState.Submitted);
    }

    [Fact]
    public async Task LiveMode_BrokerBusinessRejection_MarksOrderRejected()
    {
        var broker = new FakeBroker { PlaceResult = new PlaceOrderResult(false, ErrorMessage: "Insufficient margin") };
        var engine = Engine(FullyGatedLive(), broker, new InMemoryOrderRepository());

        var result = await engine.SubmitAsync(LimitOrder(quantity: 10, price: 100m));

        result.IsAccepted.Should().BeFalse();
        result.State.Should().Be(OrderState.Rejected);
        result.Message.Should().Contain("Insufficient margin");
        broker.PlaceOrderCallCount.Should().Be(1);
    }

    [Fact]
    public async Task LiveMode_FullyGatedButKillSwitchEngaged_RejectsAndNeverTouchesBroker()
    {
        // The engine is fully gated for Live, but the kill switch is engaged (as an operator/monitor could do
        // out-of-band between the caller's risk check and transmission). The execution boundary must re-check it
        // and refuse to send a real order — proving the kill switch is honoured "during order" (§7, §15, §18).
        var broker = new FakeBroker { PlaceResult = new PlaceOrderResult(true, BrokerOrderId: "KITE-KS") };
        var repo = new InMemoryOrderRepository();
        var killSwitch = new KillSwitchService(new FixedClock(Clock), NullLogger<KillSwitchService>.Instance);
        killSwitch.Engage("daily loss limit breached", "risk-monitor");
        var engine = Engine(FullyGatedLive(), broker, repo, killSwitch: killSwitch);

        var result = await engine.SubmitAsync(LimitOrder(quantity: 10, price: 100m));

        result.IsAccepted.Should().BeFalse();
        result.State.Should().Be(OrderState.Rejected);
        result.Message.Should().Contain("Kill switch");
        broker.PlaceOrderCallCount.Should().Be(0);

        // Even a kill-switch-refused order is persisted with its reason for audit (§28).
        var stored = await repo.GetByIdAsync(result.OrderId);
        stored!.State.Should().Be(OrderState.Rejected);
        stored.RejectionReason.Should().Contain("daily loss limit breached");
    }

    // ---- Uncertain live submission (§20, Safety Rules #5/#8/#9) -----------
    // The load-bearing distinction: a DEFINITIVE rejection (broker refused; order not placed) is safe to mark
    // terminally Rejected and trading continues; an UNCERTAIN submission (transport failure, timeout, 5xx, or an
    // unreadable success — the order MAY be live at the broker) must NOT be treated as a non-fill. It moves the
    // order to terminal Failed (not Rejected) and engages the kill switch so no further real order transmits until
    // an operator reconciles. Never blindly retry (Rule #9); prefer STOP over CONTINUE BLINDLY (Rule #5).

    [Fact]
    public async Task LiveMode_UncertainBrokerSubmission_MarksFailedAndEngagesKillSwitch()
    {
        var broker = new FakeBroker { PlaceResult = new PlaceOrderResult(false, ErrorMessage: "502 upstream timeout", IsUncertain: true) };
        var repo = new InMemoryOrderRepository();
        var killSwitch = new KillSwitchService(new FixedClock(Clock), NullLogger<KillSwitchService>.Instance);
        var engine = Engine(FullyGatedLive(), broker, repo, killSwitch: killSwitch);

        var result = await engine.SubmitAsync(LimitOrder(quantity: 10, price: 100m));

        result.IsAccepted.Should().BeFalse();
        result.State.Should().Be(OrderState.Failed); // NOT Rejected — we cannot assert the broker refused it.
        broker.PlaceOrderCallCount.Should().Be(1);    // Attempted exactly once; never blindly retried (Rule #9).
        killSwitch.IsEngaged.Should().BeTrue();        // STOP NEW TRADING until reconciled (Rule #5).

        var stored = await repo.GetByIdAsync(result.OrderId);
        stored!.State.Should().Be(OrderState.Failed);
        stored.BrokerOrderId.Should().BeNull();
        stored.RejectionReason.Should().Contain("reconcile"); // The in-flight record survives for the operator (defeats EXEC-2 re-submit).
    }

    [Fact]
    public async Task LiveMode_BrokerSubmissionThrows_MarksFailedAndEngagesKillSwitch()
    {
        // A client-side timeout / transport fault that the adapter surfaces as a throw (rather than IsUncertain)
        // must still be treated as ambiguous, never as a definitive non-placement.
        var broker = new FakeBroker { OnPlace = _ => throw new TimeoutException("client timeout") };
        var repo = new InMemoryOrderRepository();
        var killSwitch = new KillSwitchService(new FixedClock(Clock), NullLogger<KillSwitchService>.Instance);
        var engine = Engine(FullyGatedLive(), broker, repo, killSwitch: killSwitch);

        var result = await engine.SubmitAsync(LimitOrder(quantity: 10, price: 100m));

        result.IsAccepted.Should().BeFalse();
        result.State.Should().Be(OrderState.Failed);
        killSwitch.IsEngaged.Should().BeTrue();
        (await repo.GetByIdAsync(result.OrderId))!.State.Should().Be(OrderState.Failed);
    }

    [Fact]
    public async Task LiveMode_DefinitiveBusinessRejection_DoesNotEngageKillSwitch()
    {
        // Contrast with the uncertain path: a definitive rejection means the order was NOT placed, so trading is
        // safe to continue — the kill switch must stay disengaged and the order is Rejected (not Failed).
        var broker = new FakeBroker { PlaceResult = new PlaceOrderResult(false, ErrorMessage: "Insufficient margin") };
        var repo = new InMemoryOrderRepository();
        var killSwitch = new KillSwitchService(new FixedClock(Clock), NullLogger<KillSwitchService>.Instance);
        var engine = Engine(FullyGatedLive(), broker, repo, killSwitch: killSwitch);

        var result = await engine.SubmitAsync(LimitOrder(quantity: 10, price: 100m));

        result.IsAccepted.Should().BeFalse();
        result.State.Should().Be(OrderState.Rejected);
        killSwitch.IsEngaged.Should().BeFalse();
        broker.PlaceOrderCallCount.Should().Be(1);
    }

    [Fact]
    public async Task LiveMode_CallerCancellation_PropagatesWithoutFailingOrderOrEngagingKillSwitch()
    {
        // Genuine caller cancellation (host shutdown / caller token) is NOT an uncertain order outcome: it must
        // propagate, and must not engage the kill switch or mark the order Failed. This proves the
        // `when (cancellationToken.IsCancellationRequested)` guard distinguishes shutdown from a transport fault.
        using var cts = new CancellationTokenSource();
        var broker = new FakeBroker
        {
            OnPlace = _ =>
            {
                cts.Cancel();
                throw new OperationCanceledException(cts.Token);
            }
        };
        var repo = new InMemoryOrderRepository();
        var killSwitch = new KillSwitchService(new FixedClock(Clock), NullLogger<KillSwitchService>.Instance);
        var engine = Engine(FullyGatedLive(), broker, repo, killSwitch: killSwitch);

        var act = async () => await engine.SubmitAsync(LimitOrder(quantity: 10, price: 100m), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        killSwitch.IsEngaged.Should().BeFalse();
    }

    // ---- Cancellation -----------------------------------------------------

    [Fact]
    public async Task Cancel_SimulatedRestingOrder_TransitionsToCancelled()
    {
        var broker = new FakeBroker();
        var repo = new InMemoryOrderRepository();
        var engine = Engine(Paper(), broker, repo);
        var open = await engine.SubmitAsync(MarketOrder(quantity: 10)); // rests as Open

        var result = await engine.CancelAsync(open.OrderId);

        result.IsAccepted.Should().BeTrue();
        result.State.Should().Be(OrderState.Cancelled);
        broker.CancelCallCount.Should().Be(0);
    }

    [Fact]
    public async Task Cancel_LiveBrokerOrder_RequestsBrokerCancelAndMarksCancelPending()
    {
        var broker = new FakeBroker { PlaceResult = new PlaceOrderResult(true, BrokerOrderId: "KITE-1") };
        var repo = new InMemoryOrderRepository();
        var engine = Engine(FullyGatedLive(), broker, repo);
        var live = await engine.SubmitAsync(LimitOrder(quantity: 10, price: 100m)); // Submitted with broker id

        var result = await engine.CancelAsync(live.OrderId);

        result.IsAccepted.Should().BeTrue();
        result.State.Should().Be(OrderState.CancelPending);
        broker.CancelCallCount.Should().Be(1);
    }

    [Fact]
    public async Task Cancel_UnknownOrder_IsNoOp()
    {
        var engine = Engine(Paper(), new FakeBroker(), new InMemoryOrderRepository());

        var result = await engine.CancelAsync(orderId: 999);

        result.IsAccepted.Should().BeFalse();
        result.Message.Should().Contain("not found");
    }

    // ---- Async broker reconciliation (§25, §26) ---------------------------

    [Fact]
    public async Task ApplyBrokerUpdate_Fill_MarksLiveOrderFilled()
    {
        var (engine, repo, live) = await SubmittedLiveOrder("KITE-9");

        var result = await engine.ApplyBrokerUpdateAsync(Update("KITE-9", OrderState.Filled, filled: 10, avg: 100.25m));

        result.IsAccepted.Should().BeTrue();
        result.State.Should().Be(OrderState.Filled);
        var stored = await repo.GetByIdAsync(live.OrderId);
        stored!.FilledQuantity.Should().Be(10);
        stored.AverageFillPrice.Should().Be(100.25m);
        stored.FilledAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task ApplyBrokerUpdate_PartialThenFull_TracksFillProgress()
    {
        var (engine, repo, live) = await SubmittedLiveOrder("KITE-2");

        var partial = await engine.ApplyBrokerUpdateAsync(Update("KITE-2", OrderState.PartiallyFilled, filled: 4, avg: 100m));
        partial.State.Should().Be(OrderState.PartiallyFilled);
        (await repo.GetByIdAsync(live.OrderId))!.FilledQuantity.Should().Be(4);

        var full = await engine.ApplyBrokerUpdateAsync(Update("KITE-2", OrderState.Filled, filled: 10, avg: 100.5m));
        full.State.Should().Be(OrderState.Filled);
        var stored = await repo.GetByIdAsync(live.OrderId);
        stored!.FilledQuantity.Should().Be(10);
        stored.AverageFillPrice.Should().Be(100.5m);
    }

    [Fact]
    public async Task ApplyBrokerUpdate_StaleLowerCumulativeFill_DoesNotRegressFillProgress()
    {
        // Partial-fill safety (§20): broker filled_quantity is CUMULATIVE and monotonic, but Kite postbacks are
        // best-effort and can arrive out of order or duplicated, and PartiallyFilled->PartiallyFilled is a legal
        // self-transition. A delayed postback carrying a LOWER cumulative fill than already recorded must not
        // rewind the fill (which would also drag the blended average price backwards). Pre-fix this overwrote
        // unconditionally and regressed 6 -> 3.
        var (engine, repo, live) = await SubmittedLiveOrder("KITE-OOO");

        await engine.ApplyBrokerUpdateAsync(Update("KITE-OOO", OrderState.PartiallyFilled, filled: 6, avg: 100.20m));
        (await repo.GetByIdAsync(live.OrderId))!.FilledQuantity.Should().Be(6);

        // Out-of-order/duplicate: an earlier "filled 3" lands after "filled 6".
        await engine.ApplyBrokerUpdateAsync(Update("KITE-OOO", OrderState.PartiallyFilled, filled: 3, avg: 99.00m));

        var stored = await repo.GetByIdAsync(live.OrderId);
        stored!.FilledQuantity.Should().Be(6);          // not rewound to 3
        stored.AverageFillPrice.Should().Be(100.20m);   // average not dragged back
    }

    [Fact]
    public async Task ApplyBrokerUpdate_ForUntrackedBrokerId_IsIgnored()
    {
        var engine = Engine(Paper(), new FakeBroker(), new InMemoryOrderRepository());

        var result = await engine.ApplyBrokerUpdateAsync(Update("UNKNOWN", OrderState.Filled, filled: 1, avg: 10m));

        result.IsAccepted.Should().BeFalse();
    }

    [Fact]
    public async Task ApplyBrokerUpdate_DuplicateTerminal_IsIgnoredIdempotently()
    {
        var (engine, repo, live) = await SubmittedLiveOrder("KITE-3");
        await engine.ApplyBrokerUpdateAsync(Update("KITE-3", OrderState.Filled, filled: 10, avg: 100m));

        // A second Filled update cannot transition Filled->Filled: ignored, state unchanged.
        var again = await engine.ApplyBrokerUpdateAsync(Update("KITE-3", OrderState.Filled, filled: 10, avg: 100m));

        again.IsAccepted.Should().BeFalse();
        (await repo.GetByIdAsync(live.OrderId))!.State.Should().Be(OrderState.Filled);
    }

    [Fact]
    public async Task ApplyBrokerUpdate_CancelConfirmation_CompletesCancellation()
    {
        var broker = new FakeBroker { PlaceResult = new PlaceOrderResult(true, BrokerOrderId: "KITE-4") };
        var repo = new InMemoryOrderRepository();
        var engine = Engine(FullyGatedLive(), broker, repo);
        var live = await engine.SubmitAsync(LimitOrder(quantity: 10, price: 100m));
        await engine.CancelAsync(live.OrderId); // -> CancelPending

        var result = await engine.ApplyBrokerUpdateAsync(Update("KITE-4", OrderState.Cancelled, filled: 0, avg: null));

        result.State.Should().Be(OrderState.Cancelled);
    }

    [Fact]
    public async Task ApplyBrokerUpdate_Rejection_RecordsReason()
    {
        var (engine, repo, live) = await SubmittedLiveOrder("KITE-5");

        await engine.ApplyBrokerUpdateAsync(new BrokerOrderUpdate("KITE-5", OrderState.Rejected, 0, null, Clock, "RMS: limit breached"));

        var stored = await repo.GetByIdAsync(live.OrderId);
        stored!.State.Should().Be(OrderState.Rejected);
        stored.RejectionReason.Should().Contain("RMS");
    }

    // ---- AUDIT-0009 / EXEC-3: async fill-reconciliation concurrency (CRITICAL) --------------------------------
    // Reconciliation runs fire-and-forget on independent DI scopes / DbContexts, outside the trading-cycle gate.
    // These tests reproduce the two confirmed races against an EF-semantics store (read returns a snapshot; write
    // is FindAsync + blind field-copy = last-writer-wins, no rowversion). Each FAILS on pre-fix code and passes
    // only with the shared IOrderMutationGate (re-read inside the gate + bounded retry-on-null).

    [Fact]
    public async Task ApplyBrokerUpdate_FillArrivingBeforeBrokerIdCommitted_IsNotDropped()
    {
        // Race 1 (dropped fill): a fast fill postback resolves the order by broker id before RouteLiveAsync has
        // committed that id, so the first GetByBrokerIdAsync returns null. Pre-fix this is logged "untracked" and
        // the fill is silently dropped; the fix must retry until the id is visible, then apply the fill.
        var repo = new EfSemanticsOrderRepository { NullBrokerReadsBeforeResolve = 1 };
        var order = new Order
        {
            Symbol = "INFY", CorrelationId = "race-1", State = OrderState.Submitted, BrokerOrderId = "KITE-RACE1",
            Quantity = 10, CreatedAtUtc = Clock, LastUpdatedAtUtc = Clock
        };
        await repo.AddAsync(order);

        var result = await Engine(FullyGatedLive(), new FakeBroker(), repo)
            .ApplyBrokerUpdateAsync(Update("KITE-RACE1", OrderState.Filled, filled: 10, avg: 100m));

        result.IsAccepted.Should().BeTrue();
        result.State.Should().Be(OrderState.Filled);
        var stored = repo.Row(order.Id);
        stored.State.Should().Be(OrderState.Filled);
        stored.FilledQuantity.Should().Be(10);
        stored.FilledAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task ApplyBrokerUpdate_ConcurrentStaleOpenAfterFill_DoesNotRevertTheFill()
    {
        // Race 2 (last-writer-wins): two postbacks for one order reconcile concurrently — a COMPLETE (Filled=100)
        // and a stale OPEN (filled=0). Pre-fix both read State=Submitted on independent snapshots, both pass the
        // transition check, and the OPEN commits last, reverting Filled->Open / 100->0 (fill erased, position
        // unmanaged, daily-loss gate blinded). The fix serializes them and re-reads under the gate, so the OPEN
        // sees Filled and is rejected as an illegal Filled->Open transition.
        var repo = new EfSemanticsOrderRepository();
        var order = new Order
        {
            Symbol = "INFY", CorrelationId = "race-2", State = OrderState.Submitted, BrokerOrderId = "KITE-RACE2",
            Quantity = 100, CreatedAtUtc = Clock, LastUpdatedAtUtc = Clock
        };
        await repo.AddAsync(order);
        var engine = Engine(FullyGatedLive(), new FakeBroker(), repo);

        var completeParked = new TaskCompletionSource();
        var releaseComplete = new TaskCompletionSource();
        var openParked = new TaskCompletionSource();
        var releaseOpen = new TaskCompletionSource();
        repo.OnBeforePersist = o =>
        {
            if (o.State == OrderState.Filled) { completeParked.TrySetResult(); return releaseComplete.Task; }
            if (o.State == OrderState.Open) { openParked.TrySetResult(); return releaseOpen.Task; }
            return Task.CompletedTask;
        };

        // Start the COMPLETE update; it reads Submitted and parks at the point of persisting Filled (holding the
        // gate under the fix).
        var complete = engine.ApplyBrokerUpdateAsync(Update("KITE-RACE2", OrderState.Filled, filled: 100, avg: 100m));
        await completeParked.Task;

        // Start the stale OPEN update. Pre-fix it reads Submitted and parks at its own persist; under the fix it
        // blocks on the gate and never reaches persist (so openParked never fires — hence the bounded wait).
        var staleOpen = engine.ApplyBrokerUpdateAsync(Update("KITE-RACE2", OrderState.Open, filled: 0, avg: null));
        await Task.WhenAny(openParked.Task, Task.Delay(500));

        // Let COMPLETE finish first (persists Filled/100), then release OPEN. Pre-fix, OPEN now commits last and
        // clobbers the fill; under the fix, OPEN has already been rejected without writing.
        releaseComplete.TrySetResult();
        await complete;
        releaseOpen.TrySetResult();
        await staleOpen;

        var stored = repo.Row(order.Id);
        stored.State.Should().Be(OrderState.Filled);
        stored.FilledQuantity.Should().Be(100);
        stored.FilledAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Cancel_ConcurrentWithFillThatCompletesFirst_DoesNotClobberTheFill()
    {
        // A cancel and a fill postback race on the same live order. The fill completes first; pre-fix the cancel
        // holds a stale Submitted snapshot and writes CancelPending over Filled. The fix re-reads under the gate,
        // sees the terminal fill, and leaves it intact (the broker cancel is a harmless no-op against a fill).
        var repo = new EfSemanticsOrderRepository();
        var order = new Order
        {
            Symbol = "INFY", CorrelationId = "race-cx", State = OrderState.Submitted, BrokerOrderId = "KITE-RACECX",
            Quantity = 10, CreatedAtUtc = Clock, LastUpdatedAtUtc = Clock
        };
        await repo.AddAsync(order);
        var engine = Engine(FullyGatedLive(), new FakeBroker(), repo);

        var fillParked = new TaskCompletionSource();
        var releaseFill = new TaskCompletionSource();
        var cancelParked = new TaskCompletionSource();
        var releaseCancel = new TaskCompletionSource();
        repo.OnBeforePersist = o =>
        {
            if (o.State == OrderState.Filled) { fillParked.TrySetResult(); return releaseFill.Task; }
            if (o.State == OrderState.CancelPending) { cancelParked.TrySetResult(); return releaseCancel.Task; }
            return Task.CompletedTask;
        };

        var fill = engine.ApplyBrokerUpdateAsync(Update("KITE-RACECX", OrderState.Filled, filled: 10, avg: 100m));
        await fillParked.Task;

        var cancel = engine.CancelAsync(order.Id);
        await Task.WhenAny(cancelParked.Task, Task.Delay(500));

        releaseFill.TrySetResult();
        await fill;
        releaseCancel.TrySetResult();
        await cancel;

        var stored = repo.Row(order.Id);
        stored.State.Should().Be(OrderState.Filled);
        stored.FilledQuantity.Should().Be(10);
    }

    // ---- Price-fed paper fill --------------------------------------------

    [Fact]
    public async Task ApplyPaperFill_FillsRestingMarketOrder_AtObservedPrice()
    {
        var repo = new InMemoryOrderRepository();
        var engine = Engine(Paper(), new FakeBroker(), repo);
        var resting = await engine.SubmitAsync(MarketOrder(quantity: 10)); // Open

        var result = await engine.ApplyPaperFillAsync(resting.OrderId, fillPrice: 251.25m);

        result.IsAccepted.Should().BeTrue();
        result.State.Should().Be(OrderState.Filled);
        var stored = await repo.GetByIdAsync(resting.OrderId);
        stored!.FilledQuantity.Should().Be(10);
        stored.AverageFillPrice.Should().Be(251.25m);
    }

    [Fact]
    public async Task ApplyPaperFill_OnNonRestingOrder_IsNoOp()
    {
        var engine = Engine(Paper(), new FakeBroker(), new InMemoryOrderRepository());
        var resting = await engine.SubmitAsync(MarketOrder(quantity: 10));
        await engine.CancelAsync(resting.OrderId); // Transitions to Cancelled locally for simulated orders

        var result = await engine.ApplyPaperFillAsync(resting.OrderId, fillPrice: 105m);

        result.IsAccepted.Should().BeFalse();
    }

    [Fact]
    public async Task ApplyPaperFill_UnknownOrder_IsNotFound()
    {
        var engine = Engine(Paper(), new FakeBroker(), new InMemoryOrderRepository());

        var result = await engine.ApplyPaperFillAsync(orderId: 404, fillPrice: 100m);

        result.IsAccepted.Should().BeFalse();
        result.Message.Should().Contain("not found");
    }

    [Fact]
    public async Task ApplyPaperFill_WithNonPositivePrice_Throws()
    {
        var engine = Engine(Paper(), new FakeBroker(), new InMemoryOrderRepository());
        var resting = await engine.SubmitAsync(MarketOrder(quantity: 10));

        var act = async () => await engine.ApplyPaperFillAsync(resting.OrderId, fillPrice: 0m);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    // ---- Guards -----------------------------------------------------------

    [Fact]
    public async Task Submit_WithNonPositiveQuantity_Throws()
    {
        var engine = Engine(Paper(), new FakeBroker(), new InMemoryOrderRepository());
        var order = new OrderRequest(738561, "INFY", "NSE", OrderSide.Buy, OrderType.Limit, 0, ProductType.Delivery, Price: 100m);

        var act = async () => await engine.SubmitAsync(order);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    // ---- Metrics (observability) -----------------------------------------

    [Fact]
    public async Task Submit_EmitsOrderSubmitted_ForPaperLimit()
    {
        var metrics = new RecordingTradingMetrics();
        var engine = Engine(Paper(), new FakeBroker(), new InMemoryOrderRepository(), metrics);

        await engine.SubmitAsync(LimitOrder(quantity: 10, price: 250.50m));

        metrics.Submitted.Should().ContainSingle().Which.Should().Be((TradingMode.Paper, OrderSide.Buy, OrderType.Limit));
        metrics.Filled.Should().BeEmpty();
        metrics.Rejected.Should().BeEmpty();
    }

    [Fact]
    public async Task ResearchMode_EmitsOrderRejected_NotFilled()
    {
        var metrics = new RecordingTradingMetrics();
        var engine = Engine(Mode(TradingMode.Research), new FakeBroker(), new InMemoryOrderRepository(), metrics);

        await engine.SubmitAsync(LimitOrder(quantity: 10, price: 100m));

        metrics.Rejected.Should().ContainSingle().Which.Should().Be((TradingMode.Research, OrderSide.Buy));
        metrics.Filled.Should().BeEmpty();
    }

    [Fact]
    public async Task ApplyPaperFill_EmitsOrderFilled_OnlyWhenFilled()
    {
        var metrics = new RecordingTradingMetrics();
        var engine = Engine(Paper(), new FakeBroker(), new InMemoryOrderRepository(), metrics);
        var resting = await engine.SubmitAsync(MarketOrder(quantity: 10)); // rests Open — no fill yet
        metrics.Filled.Should().BeEmpty();

        await engine.ApplyPaperFillAsync(resting.OrderId, fillPrice: 251.25m);

        metrics.Filled.Should().ContainSingle().Which.Should().Be((TradingMode.Paper, OrderSide.Buy));
    }

    [Fact]
    public async Task ApplyBrokerUpdate_Rejection_EmitsOrderRejected()
    {
        var metrics = new RecordingTradingMetrics();
        var broker = new FakeBroker { PlaceResult = new PlaceOrderResult(true, BrokerOrderId: "KITE-R") };
        var engine = Engine(FullyGatedLive(), broker, new InMemoryOrderRepository(), metrics);
        await engine.SubmitAsync(LimitOrder(quantity: 10, price: 100m)); // Submitted (records a submit, no fill/reject)

        await engine.ApplyBrokerUpdateAsync(new BrokerOrderUpdate("KITE-R", OrderState.Rejected, 0, null, Clock, "RMS"));

        metrics.Rejected.Should().ContainSingle().Which.Should().Be((TradingMode.Live, OrderSide.Buy));
        metrics.Filled.Should().BeEmpty();
    }

    // ---- Helpers ----------------------------------------------------------

    private static readonly DateTimeOffset Clock = new(2026, 8, 24, 4, 30, 0, TimeSpan.Zero);

    private static OrderExecutionEngine Engine(TradingSettings settings, FakeBroker broker, IOrderRepository repo, ITradingMetrics? metrics = null, IKillSwitch? killSwitch = null, IOrderMutationGate? mutationGate = null) =>
        new(settings, broker, new LiveTradingSafetyValidator(),
            killSwitch ?? new KillSwitchService(new FixedClock(Clock), NullLogger<KillSwitchService>.Instance), repo,
            mutationGate ?? new OrderMutationGate(),
            new FixedClock(Clock), NullLogger<OrderExecutionEngine>.Instance, metrics,
            reconcileResolveDelay: TimeSpan.Zero); // no wall-clock wait between resolve retries in tests

    /// <summary>Submits a fully-gated live limit order that the broker accepts, leaving it Submitted with the given broker id.</summary>
    private static async Task<(OrderExecutionEngine Engine, InMemoryOrderRepository Repo, ExecutionResult Order)> SubmittedLiveOrder(string brokerOrderId)
    {
        var broker = new FakeBroker { PlaceResult = new PlaceOrderResult(true, BrokerOrderId: brokerOrderId) };
        var repo = new InMemoryOrderRepository();
        var engine = Engine(FullyGatedLive(), broker, repo);
        var order = await engine.SubmitAsync(LimitOrder(quantity: 10, price: 100m));
        return (engine, repo, order);
    }

    private static BrokerOrderUpdate Update(string brokerOrderId, OrderState state, int filled, decimal? avg) =>
        new(brokerOrderId, state, filled, avg, Clock);


    private static TradingSettings Paper() => new() { Mode = TradingMode.Paper };
    private static TradingSettings Mode(TradingMode mode) => new() { Mode = mode };

    private static TradingSettings FullyGatedLive() => new()
    {
        Mode = TradingMode.Live,
        EnableLiveTrading = true,
        LiveTradingAcknowledgement = TradingSettings.RequiredLiveAcknowledgement
    };

    private static OrderRequest LimitOrder(int quantity, decimal price) =>
        new(738561, "INFY", "NSE", OrderSide.Buy, OrderType.Limit, quantity, ProductType.Delivery, Price: price);

    private static OrderRequest MarketOrder(int quantity) =>
        new(738561, "INFY", "NSE", OrderSide.Buy, OrderType.Market, quantity, ProductType.Delivery);

    private sealed class FixedClock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    /// <summary>Records broker interaction so tests can assert the safety gate never leaks a real order.</summary>
    private sealed class FakeBroker : ITradingBroker
    {
        public PlaceOrderResult PlaceResult { get; set; } = new(true, BrokerOrderId: "BROKER-1");

        /// <summary>Optional hook to simulate an ambiguous submission that throws (transport failure / timeout / caller cancellation).</summary>
        public Func<CancellationToken, Task<PlaceOrderResult>>? OnPlace { get; set; }

        public int PlaceOrderCallCount { get; private set; }
        public int CancelCallCount { get; private set; }

        public string ProviderName => "Fake";
        public bool IsAuthenticated => true;

        public async Task<PlaceOrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken cancellationToken = default)
        {
            PlaceOrderCallCount++;
            if (OnPlace is not null)
                return await OnPlace(cancellationToken);
            return PlaceResult;
        }

        public Task CancelOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default)
        {
            CancelCallCount++;
            return Task.CompletedTask;
        }

        public Task AuthenticateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<BrokerProfile> GetProfileAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrokerFunds> GetFundsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BrokerHolding>> GetHoldingsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BrokerPositionSummary>> GetPositionsAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<BrokerOrderInfo>> GetOrdersAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BrokerOrderInfo> GetOrderAsync(string brokerOrderId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ModifyOrderResult> ModifyOrderAsync(string brokerOrderId, OrderModification modification, CancellationToken cancellationToken = default) => throw new NotSupportedException();

#pragma warning disable CS0067 // Not raised in these tests; async fill reconciliation is a separate concern.
        public event EventHandler<BrokerOrderUpdate>? OrderUpdated;
        public event EventHandler<EventArgs>? StreamDisconnected;
        public bool IsConnected => true;

#pragma warning restore CS0067
    }

    /// <summary>Minimal in-memory order store: assigns ids and holds instances by reference.</summary>
    private sealed class InMemoryOrderRepository : IOrderRepository
    {
        private readonly Dictionary<long, Order> _orders = new();
        private long _nextId;

        public Task<Order> AddAsync(Order order, CancellationToken cancellationToken = default)
        {
            order.Id = ++_nextId;
            _orders[order.Id] = order;
            return Task.FromResult(order);
        }

        public Task UpdateAsync(Order order, CancellationToken cancellationToken = default)
        {
            _orders[order.Id] = order;
            return Task.CompletedTask;
        }

        public Task<Order?> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_orders.GetValueOrDefault(id));

        public Task<Order?> GetByBrokerIdAsync(string brokerOrderId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_orders.Values.FirstOrDefault(o => o.BrokerOrderId == brokerOrderId));

        public Task<IReadOnlyList<Order>> GetByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Order>>(_orders.Values.Where(o => o.CorrelationId == correlationId).ToList());

        public Task<IReadOnlyList<Order>> GetOpenOrdersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Order>>(_orders.Values.Where(o => !o.IsTerminal).ToList());

        public Task<IReadOnlyList<Order>> GetOrdersAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Order>>(_orders.Values.Where(o => o.CreatedAtUtc >= fromUtc && o.CreatedAtUtc <= toUtc).ToList());
    }

    /// <summary>
    /// Order store with EF Core write semantics, for the AUDIT-0009 concurrency tests. Reads return a detached
    /// snapshot (clone); <see cref="UpdateAsync"/> emulates FindAsync + blind field-copy + SaveChanges
    /// (last-writer-wins, no rowversion) — the exact behaviour that lets a stale write clobber a committed fill.
    /// The by-reference <see cref="InMemoryOrderRepository"/> shares one instance and so cannot reproduce it.
    /// </summary>
    private sealed class EfSemanticsOrderRepository : IOrderRepository
    {
        private readonly Dictionary<long, Order> _rows = new();
        private long _nextId;
        private int _nullBrokerReads;

        /// <summary>Leading <see cref="GetByBrokerIdAsync"/> calls that return null even though the row exists — emulates a fill postback arriving before RouteLiveAsync commits the BrokerOrderId (Race 1).</summary>
        public int NullBrokerReadsBeforeResolve { get; set; }

        /// <summary>Awaited inside <see cref="UpdateAsync"/> before the row is overwritten — lets a test park a writer to force a deterministic interleave (Race 2).</summary>
        public Func<Order, Task>? OnBeforePersist { get; set; }

        /// <summary>The committed row (as a clone), for assertions.</summary>
        public Order Row(long id) => Clone(_rows[id]);

        public Task<Order> AddAsync(Order order, CancellationToken cancellationToken = default)
        {
            order.Id = ++_nextId;
            _rows[order.Id] = Clone(order);
            return Task.FromResult(order);
        }

        public async Task UpdateAsync(Order order, CancellationToken cancellationToken = default)
        {
            if (OnBeforePersist is { } hook)
                await hook(order);
            if (_rows.TryGetValue(order.Id, out var row))
                CopyMutable(from: order, to: row); // blind field copy: no concurrency predicate
        }

        public Task<Order?> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
            Task.FromResult<Order?>(_rows.TryGetValue(id, out var row) ? Clone(row) : null);

        public Task<Order?> GetByBrokerIdAsync(string brokerOrderId, CancellationToken cancellationToken = default)
        {
            if (_nullBrokerReads < NullBrokerReadsBeforeResolve)
            {
                _nullBrokerReads++;
                return Task.FromResult<Order?>(null);
            }

            var match = _rows.Values.FirstOrDefault(o => o.BrokerOrderId == brokerOrderId);
            return Task.FromResult<Order?>(match is null ? null : Clone(match));
        }

        public Task<IReadOnlyList<Order>> GetByCorrelationIdAsync(string correlationId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Order>>(_rows.Values.Where(o => o.CorrelationId == correlationId).Select(Clone).ToList());

        public Task<IReadOnlyList<Order>> GetOpenOrdersAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Order>>(_rows.Values.Where(o => !o.IsTerminal).Select(Clone).ToList());

        public Task<IReadOnlyList<Order>> GetOrdersAsync(DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Order>>(_rows.Values.Where(o => o.CreatedAtUtc >= fromUtc && o.CreatedAtUtc <= toUtc).Select(Clone).ToList());

        private static Order Clone(Order o) => new()
        {
            Id = o.Id,
            BrokerOrderId = o.BrokerOrderId,
            InstrumentToken = o.InstrumentToken,
            Symbol = o.Symbol,
            Exchange = o.Exchange,
            Side = o.Side,
            Type = o.Type,
            Validity = o.Validity,
            Product = o.Product,
            Quantity = o.Quantity,
            Price = o.Price,
            TriggerPrice = o.TriggerPrice,
            FilledQuantity = o.FilledQuantity,
            AverageFillPrice = o.AverageFillPrice,
            State = o.State,
            RejectionReason = o.RejectionReason,
            Tag = o.Tag,
            CorrelationId = o.CorrelationId,
            StrategyName = o.StrategyName,
            CreatedAtUtc = o.CreatedAtUtc,
            LastUpdatedAtUtc = o.LastUpdatedAtUtc,
            FilledAtUtc = o.FilledAtUtc
        };

        private static void CopyMutable(Order from, Order to)
        {
            to.BrokerOrderId = from.BrokerOrderId;
            to.State = from.State;
            to.FilledQuantity = from.FilledQuantity;
            to.AverageFillPrice = from.AverageFillPrice;
            to.RejectionReason = from.RejectionReason;
            to.FilledAtUtc = from.FilledAtUtc;
            to.LastUpdatedAtUtc = from.LastUpdatedAtUtc;
        }
    }
}

