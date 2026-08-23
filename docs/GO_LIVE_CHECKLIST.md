# Go-Live Checklist

**Read this end-to-end before sending a single real order.** This platform can lose real money. The
default configuration cannot trade live, and that is deliberate. Going live is a staged promotion, not a
config flip — you earn each stage by proving the previous one, and you dry-run before you commit capital.

> **Non-negotiable secrets rule.** API secret, access token, auth codes, and DB password are **never**
> hardcoded and **never** logged. Supply them through environment variables or a secret store only. If you
> find any of these values in `appsettings.json`, in source, or in a log line — **stop and fix that first.**

---

## The staged path (do not skip stages)

Each stage must run clean for a meaningful window before you promote to the next. "Clean" is defined by the
abort criteria at the bottom of this document.

| Stage | `Trading:Mode` | Broker interaction | Purpose | Real money |
|-------|----------------|--------------------|---------|------------|
| 1. Research  | `Research` | none | Data exploration, sanity checks | No |
| 2. Backtest  | `Backtest` | none | Deterministic historical replay of the chosen strategy | No |
| 3. Paper     | `Paper`    | live market data, **simulated** execution | Prove the full decision → risk → sizing → execution loop against a live feed | No |
| 4. Live (dry run) | `Live` gates **off** | live feed, orders still refused by the safety gate | Rehearse the live wiring with real credentials but zero orders | No |
| 5. Live (armed) | `Live` gates **on** | real orders | Actual trading, smallest viable size | **Yes** |

You do not touch stage 4 until stage 3 has run clean across multiple full sessions. You do not touch stage 5
until stage 4 has run clean for at least one full session with real market-data credentials.

---

## Stage 3 — Paper (the real proving ground)

Paper mode is where the strategy is actually validated. It runs the identical decision cycle used live, but
fills are simulated at observed prices (no fabricated prices, no look-ahead).

- [ ] `Trading:Mode = "Paper"`, `EnableLiveTrading = false`.
- [ ] Broker credentials for the **live data feed** provided via env vars (see below). Paper still needs a
      valid Kite session to receive real ticks.
- [ ] Pick the strategy explicitly: `Strategy:Name` = `MomentumBreakoutV1` **or** `TrendAlignedPullbackV1`.
- [ ] Risk limits (`Risk:*`) set to the values you intend to trade live — not looser. Paper must exercise the
      same guards.
- [ ] Run a full trading session. At the close, confirm the metrics tell a coherent story (see Observability).
- [ ] Confirm end-of-day reconciliation runs and reports **clean** (`algotrader.reconciliations` with
      `clean=true`, `has_critical=false`).

**Do not promote out of Paper until the strategy has been observed over enough sessions to trust the loop —
not the P&L. A profitable paper run is not a validated strategy; it is one sample.**

---

## Stage 4 — Live dry run (real credentials, zero orders)

This is the rehearsal. `Mode=Live` but the triple gate stays **off**, so the safety validator refuses every
real order. You are proving that credentials, session, feed, and wiring all work under the live code path
before any order can escape.

- [ ] `Trading:Mode = "Live"`, `EnableLiveTrading = false`, `LiveTradingAcknowledgement = ""`.
- [ ] Start the platform. Confirm it boots (the startup safety gate only *blocks* an armed-but-misconfigured
      live setup; a deliberately-disarmed live mode is allowed to run as a dry run).
- [ ] Confirm ticks and candles are flowing (`algotrader.ticks_processed`, `algotrader.candles_closed` with
      `mode=Live` climbing).
- [ ] Confirm signals are generated and that any order the strategy *would* send is counted under
      `algotrader.orders_rejected` (`mode=Live`) — the safety gate vetoing it is the expected, correct outcome.
- [ ] Confirm **zero** `algotrader.orders_filled` with `mode=Live`. If anything filled, the gate failed — stop.
- [ ] Exercise the kill switch manually and confirm `algotrader.kill_switch_engagements` increments and the
      cycle stops acting.

---

## Stage 5 — Live armed (real orders)

Only after stages 1–4 are clean. This is the only stage that sends real orders.

### The triple gate (all three, exactly)

Live trading requires **all three** to be satisfied. Miss any one and no real order is ever sent — the
platform refuses to start armed. This is by design; do not attempt to bypass it.

```json
{
  "Trading": {
    "Mode": "Live",
    "EnableLiveTrading": true,
    "LiveTradingAcknowledgement": "I-ACCEPT-LIVE-TRADING-RISK"
  }
}
```

- [ ] `Mode = "Live"`
- [ ] `EnableLiveTrading = true`
- [ ] `LiveTradingAcknowledgement = "I-ACCEPT-LIVE-TRADING-RISK"` (exact string)

### Secrets (environment variables only — never committed, never logged)

```bash
export Broker__ApiKey="your_kite_api_key"
export Broker__ApiSecret="your_kite_api_secret"
export Broker__AccessToken="your_daily_access_token"
```

- [ ] Access token is **today's** — Kite tokens expire daily. A stale token = no session.
- [ ] Grep the repo and `appsettings.json` one last time: no secret is present in any tracked file.

### First armed session — smallest viable size

- [ ] Set risk limits to the **floor**: `Risk:MaxRiskPerTrade`, `Risk:MaxCapitalPerTrade`,
      `Risk:MaxTradesPerDay`, `Risk:MaxDailyLoss` all at the smallest values that let a single trade through.
- [ ] `Risk:FlattenPositionsOnDailyLossBreach = true` and `Risk:RequireManualResetAfterHalt = true`.
- [ ] Be present at the screen for the entire first session. Watch the metrics live.
- [ ] Confirm the first real order's lifecycle end to end: `orders_submitted` → `orders_filled` (`mode=Live`),
      and that the position appears where you expect.
- [ ] Keep the kill-switch trigger within reach and know exactly how to fire it.

---

## Observability — what to watch while live

The platform emits metrics via `System.Diagnostics.Metrics` under the meter **`AlgoTrader.Trading`** (no extra
dependency). Subscribe with `dotnet-counters`, an OpenTelemetry collector, or a Prometheus exporter:

```bash
dotnet-counters monitor --process-id <pid> --counters AlgoTrader.Trading
```

| Instrument | Labels | What it tells you |
|-----------|--------|-------------------|
| `algotrader.ticks_processed` | `mode` | Feed is alive and reaching the cycle |
| `algotrader.candles_closed` | `mode` | Decision candles are forming |
| `algotrader.signals_generated` | `mode`, `strategy`, `direction` | Strategy is producing entries/exits |
| `algotrader.risk_rejections` | `mode`, `reason` | Which guard is vetoing, and how often |
| `algotrader.orders_submitted` | `mode`, `side`, `type` | Orders leaving the cycle |
| `algotrader.orders_filled` | `mode`, `side` | Orders that actually filled |
| `algotrader.orders_rejected` | `mode`, `side` | Safety gate / broker / Research-mode refusals |
| `algotrader.reconciliations` | `clean`, `has_critical` | End-of-day drift between platform and broker |
| `algotrader.kill_switch_engagements` | `initiated_by` | Emergency stops |

Labels are deliberately low-cardinality (enum-valued). **No metric is ever tagged with a correlation id, order
id, or symbol** — those live in the structured logs, not the metrics.

**Structured logs:** each signal is processed inside a logging scope carrying `CorrelationId`, `Symbol`, and
`Strategy`, so you can trace one signal from generation through risk, sizing, and execution across log lines.
The correlation id is a join key for the logs — never a metric label.

**Sanity reads while live:**
- `ticks_processed` climbing but `candles_closed` flat for too long → feed or aggregation problem.
- `signals_generated` climbing but `orders_submitted` flat → check `risk_rejections`; a guard is vetoing.
- Any `orders_filled` with `mode=Live` during a dry run → the gate failed; kill it.

---

## Abort criteria — stop and investigate immediately

Fire the kill switch and stand down if **any** of these occur:

- An order fills with `mode=Live` during a dry run (stage 4).
- End-of-day reconciliation reports `has_critical=true`, or is not clean.
- A secret appears in a log line or a tracked file.
- Realized loss approaches `Risk:MaxDailyLoss` (the platform should auto-halt; verify it did).
- The data feed stalls (`ticks_processed` stops) while positions are open.
- Behaviour diverges from what paper trading led you to expect.

## Rollback

To disarm instantly, flip **any one** gate off (simplest: `EnableLiveTrading = false`) and restart. Because all
three are required, disabling one is sufficient to guarantee no further real orders. Then drop back to `Paper`
to reproduce and diagnose before re-arming.

---

*This is an operational runbook, not a promise of profit. Validate the strategy yourself; you are responsible
for monitoring live activity, setting risk limits, and your own tax/compliance obligations.*
