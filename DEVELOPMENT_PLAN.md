# AlgoTrader — Development Plan (12-Phase Roadmap)

Production-oriented algorithmic trading platform for Indian NSE equity intraday/swing trading, built with
C# / **.NET 9** and integrating with Zerodha Kite Connect. The platform evolves **backtesting → paper → live**.

> **Reconstruction note.** The original plan document was lost from the repository. This file was rebuilt on
> **2026-08-23** to match the *shipped code*, verified by a full-solution build (0 warnings / 0 errors) and the
> green test suite — not from the historical spec. Phase boundaries for the later stages (8–12) were developed
> iteratively with some overlap, so the numbering is a faithful reconstruction rather than the literal original.

**Legend:** ✅ complete (code present + tests green) · 🟡 partial (code done, validation/ops outstanding) · ⬜ not started

---

## Guiding constraints (apply to every phase)

- **Triple-gated live trading** (§6, §36): real orders require `Mode=Live` **and** `EnableLiveTrading=true`
  **and** `LiveTradingAcknowledgement="I-ACCEPT-LIVE-TRADING-RISK"`. Enforced at startup *and* re-checked at
  order-transmit time. If any gate is unmet, no real order is ever sent. Live is **disabled by default**.
- **Secrets** are never hardcoded and never logged — API secret, access token, auth codes, DB password come
  from environment variables / .NET user-secrets / a secret store only.
- **Money uses `decimal`**, never `double`. Time is **UTC internally**; IST = UTC+05:30.
- **No look-ahead bias**: strategies see only information available at the decision timestamp; the backtest
  engine fills on the *next* candle. Strategies never depend on broker/Zerodha types.
- **Strategies are unvalidated hypotheses** — no profitability is claimed or implied.
- **Discipline**: build + `dotnet test` at every phase boundary; do not advance until green.

---

## Phases

### Phase 1 — Foundation & Safety ✅
Clean/modular-monolith skeleton and the safety spine.
- **Deliverables:** Domain entities/value objects/interfaces; strongly-typed config with DataAnnotations
  validation (startup fails on invalid config); order **state machine** (§25); **kill switch** (§15);
  **triple-gate** safety validator (§6, §36); API composition root with health/status endpoints; `ISystemClock`.
- **Code:** `AlgoTrader.Domain`, `AlgoTrader.Application/Configuration`, `AlgoTrader.Application/Safety`
  (`LiveTradingSafetyValidator`, `KillSwitchService`), `AlgoTrader.Api/Program.cs`.

### Phase 2 — Persistence ✅
Durable storage for instruments, candles, and orders.
- **Deliverables:** EF Core model + `InitialCreate` migration; Instrument/Order/MarketCandle repositories;
  idempotent writes; database seeder.
- **Code:** `AlgoTrader.Persistence` (`Migrations/`, `Repositories/`, `Seeders/`).

### Phase 3 — Historical Market Data Ingestion ✅
- **Deliverables:** Kite historical candle API → UTC domain candles; response validation; bounded, idempotent
  backfill; 1m/5m/15m/daily intervals.
- **Code:** `AlgoTrader.MarketData/Kite/KiteHistoricalDataProvider`, historical backfill service.

### Phase 4 — Backtesting Engine ✅
Deterministic, candle-driven historical replay.
- **Deliverables:** event loop with **next-candle fills** (no look-ahead); intrabar stop/target; end-of-day and
  max-holding-time exits; configurable position sizing; capital curve, drawdown, Sharpe/Sortino; data-split
  boundaries; run persistence/replay metadata; slippage/execution models.
- **Code:** `AlgoTrader.Backtesting` (`BacktestEngine`, `BacktestMetrics`, `BacktestPerformanceCalculator`,
  `BacktestExecutionModels`, `BacktestDataSplit`, `BacktestRunPersistenceService`).

### Phase 5 — Strategy Framework & Strategies ✅
Two selectable, broker-agnostic strategies (chosen via `Strategy:Name`).
- **Deliverables:** `IStrategy` + shared `Indicators` (EMA/ATR, Wilder); **MomentumBreakoutV1** (intraday);
  **TrendAlignedPullbackV1** (15-min swing/delivery, 1–2 session hold, trend-regime + pullback-resume entry,
  ATR stop capped by max %, trend/time exits). Both carry a deterministic per-day entry counter.
- **Code:** `AlgoTrader.Strategy` (`MomentumBreakoutV1`, `TrendAlignedPullbackV1`, `Indicators`, parameter types).

### Phase 6 — Trading Cost Model ✅
- **Deliverables:** `ZerodhaEquityCostCalculator` (§18) — brokerage, STT (delivery both legs / intraday
  sell-only), exchange txn, SEBI, stamp duty, GST, DP charges. Single home for all charge formulas.
- **Code:** `AlgoTrader.Application/Costing/ZerodhaEquityCostCalculator`.

### Phase 7 — Risk Engine & Position Sizing ✅
- **Deliverables:** stateless pre-trade veto (§14/§15) — system-integrity gates (kill switch / broker down /
  malformed) block everything incl. exits; budget+market gates (daily loss, trades/day, max positions,
  symbol-already-open, session hours, stale data, capital, funds) block only risk-increasing entries.
  `RiskAwarePositionSizer` (RiskBased sizing) shared by backtest + paper + live.
- **Code:** `AlgoTrader.Risk` (`RiskEngine`, `RiskAwarePositionSizer`).

### Phase 8 — Live Market Data & Paper Trading ✅
Prove the full decision loop against a live feed with simulated fills.
- **Deliverables:** Kite **WebSocket** streaming — binary tick decode incl. **full-mode 5×2 market depth**
  (`DepthReceived`), best bid/ask from top-of-book; `LastPriceCache`; candle aggregation; `PaperPortfolio`
  ledger (cash, positions, realized P&L net of round-trip charges); `PaperTradingCycle` (tick → fill resting
  order → aggregate → strategy → risk → sizing → submit market order that fills next tick).
- **Code:** `AlgoTrader.MarketData/Kite/KiteWebSocketMarketDataProvider`, `AlgoTrader.MarketData/LastPriceCache`,
  `AlgoTrader.Trading` (`PaperPortfolio`, `PaperTradingCycle`).

### Phase 9 — Broker Integration (Zerodha Kite Connect) ✅
- **Deliverables:** `ZerodhaKiteBroker` (orders, funds, positions; typed `HttpClient`; secrets never logged);
  `KiteOrderStream` — real async order postbacks (fills/cancels/rejects) over the ticker socket, raising
  `OrderUpdated`.
- **Code:** `AlgoTrader.Broker/Zerodha` (`ZerodhaKiteBroker`, `KiteOrderStream`).

### Phase 10 — Order Execution Engine ✅
The single decision point for turning a risk-approved request into a persisted, state-tracked order.
- **Deliverables:** persist every order (incl. rejections, for audit §28); §25 state machine; **triple-gate
  routing** — real `PlaceOrderAsync` only in Live with a passing safety re-check; Research refuses; Backtest/Paper
  simulate honestly (no fabricated prices); `ApplyBrokerUpdateAsync` reconciles async broker fills; `CancelAsync`.
- **Code:** `AlgoTrader.Execution/OrderExecutionEngine`.

### Phase 11 — Live Trading Loop, Reconciliation & Observability ✅
Wire everything into a hosted service and add live truth-tracking.
- **Deliverables:** `TradingLoopService` (hosted, gated, never crashes the host); `LiveTradingCycle` +
  `LiveAccountView` (broker-truth snapshot, restart-safe daily-loss/trades-today derivation); `LiveReconciler`
  (EOD local-vs-broker drift → kill switch on critical); `ITradingMetrics` observability seam
  (`System.Diagnostics.Metrics`, low-cardinality labels, never tags secrets); correlation-id log scopes.
- **Code:** `AlgoTrader.Trading` (`TradingLoopService`, `LiveTradingCycle`, `LiveAccountView`, `LiveReconciler`),
  `AlgoTrader.Application/Observability` (`ITradingMetrics`, `MeterTradingMetrics`).

### Phase 12 — Live Go-Live & Hardening 🟡
The final gate: real orders, smallest viable size, staged promotion.
- **Done (code):** armed-live path exists and is triple-gated; end-of-day reconciliation wired; go-live runbook.
- **Outstanding (operational, not code):** run multiple clean **paper** sessions against a live feed; a **live
  dry-run** (real credentials, gates off, zero orders); then the first armed session under supervision.
- **Code/docs:** `docs/GO_LIVE_CHECKLIST.md`.

---

## What "complete" does **not** mean

The platform is feature-complete at the engine/loop level, but the following are deliberately outstanding and
are **validation/operations, not missing code**:

1. **Strategy validation** — neither strategy has been validated over real historical data (no walk-forward,
   no out-of-sample study). They remain unproven hypotheses by design.
2. **No live paper/dry-run session** — the loop has not yet been run against a real Kite feed for a full session.
3. **Exchange-timestamp decoding** on the WebSocket depth/tick packets is deferred until the epoch convention is
   confirmed against a live capture (receipt time is used meanwhile).

## Current baseline

**306 tests green** — 296 unit / 7 backtesting / 3 integration. Full-solution build: 0 warnings / 0 errors on
.NET 9. Re-verify with `dotnet build AlgoTrader.sln` + `dotnet test AlgoTrader.sln` at every phase boundary.
