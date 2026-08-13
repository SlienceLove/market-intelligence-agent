# Phase 5 Integration Runbook

> Generated: 2026-08-13
> Branch: feat/p5-bidding-collection
> Test baseline: 328 passed / 4 skipped

This runbook covers the end-to-end five-layer market intelligence pipeline, the
Phase 5 bidding collection chain, and the demo script for business presentation.
It is the operator reference for running, verifying, and demonstrating the
integrated system.

---

## 3.1 System Architecture Overview

### Five-Layer Pipeline

The market intelligence agent is structured as five independent but connected layers:

```
┌──────────────────────────────────────────────────────────────────┐
│ Layer 1  列关键词  Keyword Input                                  │
│   Bidding: keywords in ScheduledCollectionPlan                   │
│   Knowledge: user query to Dify phase2-routing-v1                │
└──────────────────────────┬───────────────────────────────────────┘
                           │
                           ▼
┌──────────────────────────────────────────────────────────────────┐
│ Layer 2  搜资料入库  Search + Ingest                              │
│   Dify: phase2-routing-v1 retrieves from knowledge datasets      │
│     P1 产品与解决方案 → P2 行业与市场研究 → P3 待分类 → Tavily   │
│   Bidding: CompositeBiddingNoticeCollector → platform parsers    │
└──────────────────────────┬───────────────────────────────────────┘
                           │
                           ▼
┌──────────────────────────────────────────────────────────────────┐
│ Layer 3  沉淀成知识  Consolidate into Knowledge                   │
│   Dify: curated P1/P2/P3 datasets + knowledge operations         │
│   Bidding: IBiddingNoticeLedger fingerprint deduplication        │
└──────────────────────────┬───────────────────────────────────────┘
                           │
                           ▼
┌──────────────────────────────────────────────────────────────────┐
│ Layer 4  按任务生成内容  Generate Content                         │
│   Dify: phase3-content-generation Workflow (doc/ppt/article)     │
│   Bidding: notification rendering (text + markdown summary)      │
│   [Future: phase3 workflow generates structured bidding reports] │
└──────────────────────────┬───────────────────────────────────────┘
                           │
                           ▼
┌──────────────────────────────────────────────────────────────────┐
│ Layer 5  收集全国招标信息  Nationwide Bidding Collection          │
│   ScheduledBiddingCollectionService (Worker, 1-min poll)         │
│   POST /api/bidding/collect (on-demand)                          │
│   INotificationChannel: SmtpNotificationChannel / Webhook        │
│   IScheduledCollectionHistory: (planId, date) idempotency guard  │
└──────────────────────────────────────────────────────────────────┘
```

### Entry Points

**Scheduled (Worker)**
`ScheduledBiddingCollectionService` runs as a `BackgroundService` in the Worker host.
It polls every 60 seconds via `PeriodicTimer` and also fires once at startup so a
restart does not miss a slot that is already due. On each tick it calls
`IScheduledCollectionCoordinator.ExecuteDuePlansAsync(DateTimeOffset.UtcNow)`. Plans
that pass `IsDueAt(now)` run in sequence; a failure in one plan does not block others.

**On-Demand (API)**
`POST /api/bidding/collect` accepts `{"planIds": ["plan-id"]}` or an empty body to
run all registered plans. Authentication: `X-Agent-Api-Key` header checked against
`Bidding:BridgeApiKey` via constant-time comparison. Calls `OnDemandCollectionService`,
which bypasses `IsDueAt` and runs immediately, subject to the `(planId, date)`
idempotency guard in the coordinator.

### Coordinator Flow

For each plan, `ScheduledCollectionCoordinator.ExecuteAsync` runs these steps:

```
1.  Check plan.Enabled flag
2.  Validate plan (Validate()) — before touching history so bad config
    does not burn the day's idempotency slot
3.  GetExecutionResultAsync(planId, date) — if Completed, return cached result
4.  TryRecordExecutionAsync(planId, date) — claim the slot
5.  BuildCollectionRequest(plan, date) — keywords, region, industry,
    time window [now-LookbackDays .. now], MaxResults cap
6.  IBiddingNoticeCollector.CollectAsync(request)
      └─ CompositeBiddingNoticeCollector
           └─ HttpBiddingNoticeCollector
                 ├─ robots.txt check (RobotsTxtCache — RFC 9309)
                 ├─ rate limit (BiddingRateLimiter: global/platform/request)
                 └─ IPlatformParser.ParseAsync → List<BiddingNotice>
7.  For each notice: IBiddingNoticeLedger.TryRegisterAsync(fingerprint)
8.  If new notices > 0: render NotificationMessage (text + markdown, ≤20 items)
9.  SelectChannel(plan.NotificationChannel) → INotificationChannel
10. INotificationChannel.SendAsync(message)
11. For each sent notice: IBiddingNoticeLedger.MarkNotifiedAsync(fingerprint)
12. SaveExecutionResultAsync(result) → IScheduledCollectionHistory
```

### Component Map

```
Worker
  └─ ScheduledBiddingCollectionService
        └─ IScheduledCollectionCoordinator
              └─ ScheduledCollectionCoordinator
                    ├─ IBiddingNoticeCollector
                    │    └─ CompositeBiddingNoticeCollector
                    │         └─ HttpBiddingNoticeCollector
                    │               ├─ RobotsTxtCache
                    │               ├─ BiddingRateLimiter
                    │               └─ IPlatformParser
                    │                     └─ MockRssPlatformParser (current)
                    ├─ IBiddingNoticeLedger
                    │    ├─ JsonLinesBiddingNoticeLedger  (LedgerRoot configured)
                    │    └─ InMemoryNoticeLedger          (fallback)
                    ├─ IScheduledCollectionHistory
                    │    ├─ JsonLinesScheduledCollectionHistory (LedgerRoot configured)
                    │    └─ InMemoryScheduledCollectionHistory  (fallback)
                    ├─ INotificationChannel [smtp]
                    │    ├─ SmtpNotificationChannel       (Smtp:Enabled=true)
                    │    └─ UnconfiguredNotificationChannel (default)
                    └─ INotificationChannel [webhook]
                         ├─ WebhookNotificationChannel    (Webhook:Enabled=true)
                         └─ UnconfiguredNotificationChannel (default)

API
  └─ POST /api/bidding/collect
        └─ OnDemandCollectionService
              └─ IScheduledCollectionCoordinator (same singleton instance)
```

---

## 3.2 Prerequisites

### Required Configuration

| Key | Purpose | Default | Required for |
|-----|---------|---------|--------------|
| `Bidding:BridgeApiKey` | API auth (`X-Agent-Api-Key`) | (empty) | Any API call |
| `Bidding:LedgerRoot` | Directory for JSON Lines files | (empty) | Cross-restart persistence |
| `Bidding:Smtp:Enabled` | Enable SMTP channel | `false` | Email delivery |
| `Bidding:Smtp:DryRun` | Render but do not send | `true` | Override to `false` for real email |
| `Bidding:Smtp:Host` | SMTP server hostname | (empty) | Email delivery |
| `Bidding:Smtp:Port` | SMTP port | 587 | Email delivery |
| `Bidding:Smtp:Username` | SMTP auth username | (empty) | Email delivery |
| `Bidding:Smtp:Password` | SMTP auth password | (empty) | Env var or secrets store only |
| `Bidding:Webhook:Enabled` | Enable Webhook channel | `false` | Webhook delivery |
| `Bidding:Webhook:DryRun` | Render but do not POST | `true` | Override to `false` for real POST |
| `Bidding:Webhook:Url` | Webhook endpoint (HTTPS only) | (empty) | Webhook delivery |

Credentials (`Smtp:Password`, `Webhook:Url`) must come from environment variables or
a local secrets store. Do not write them in `appsettings.json` or commit them to the
repository.

### Environment

- .NET 8 SDK — verify with `dotnet --version` (must show `8.x`)
- Worker host: `MarketIntelligence.Agent.Worker` — runs `ScheduledBiddingCollectionService`
- API host: `MarketIntelligence.Agent.Api` — exposes `POST /api/bidding/collect`
- For Layers 1–4 (Dify): local Dify instance running; see `docs/ops/local-dify-runbook.md`

---

## 3.3 End-to-End Walk-Through (Mock Data Path)

This walk-through uses `MockRssPlatformParser`, which returns fixture notices locally
with no external HTTP calls. No real bidding platform access is required.

### Step 1: Build

```bash
cd D:\Project\Github\Agent\market-intelligence-agent
dotnet build
```

Expected: `Build succeeded.  0 Error(s)`.

### Step 2: Start the Worker

```bash
cd src/MarketIntelligence.Agent.Worker
dotnet run --environment Development
```

The service starts, fires once immediately, then polls every minute. Without
configured plans the log shows:

```
info: ScheduledBiddingCollectionService
      Scheduled bidding collection service started. Poll interval: 00:01:00.
```

### Step 3: Configure a demo plan

Plans are supplied via `IScheduledCollectionPlanSource`, registered in DI.
The default implementation is `InMemoryScheduledCollectionPlanSource` with an empty
list. To supply a demo plan for local testing, register it in the Worker's DI setup
(Development environment only — do not commit real keywords or schedules):

```csharp
// In Worker Program.cs, under Development environment guard:
services.AddSingleton<IScheduledCollectionPlanSource>(_ =>
    new InMemoryScheduledCollectionPlanSource([
        new ScheduledCollectionPlan
        {
            PlanId            = "demo-plan-001",
            Name              = "Demo — 工业自动化招标",
            Enabled           = true,
            Keywords          = ["工业自动化", "智能制造", "PLC"],
            ExecutionTimeUtc  = new TimeOnly(9, 0),
            NotificationChannel = "webhook",
            LookbackDays      = 7,
            MaxResults        = 20
        }
    ]));
```

Add `appsettings.Development.json` (not committed to VCS):

```json
{
  "Bidding": {
    "BridgeApiKey": "demo-key-local",
    "LedgerRoot":   "C:/tmp/bidding-ledger",
    "Webhook": { "Enabled": false, "DryRun": true }
  }
}
```

`DryRun: true` means notifications are fully rendered and logged but not delivered.

### Step 4: Trigger on-demand via API

Start the API in a second terminal:

```bash
cd src/MarketIntelligence.Agent.Api
dotnet run --environment Development
```

Then POST a collection request:

```bash
curl -X POST http://localhost:5000/api/bidding/collect \
  -H "Content-Type: application/json" \
  -H "X-Agent-Api-Key: demo-key-local" \
  -d '{"planIds": ["demo-plan-001"]}'
```

### Step 5: Expected API response

```json
{
  "plansExecuted": 1,
  "totalNoticesCollected": 5,
  "skippedCount": 0,
  "status": "success",
  "plans": [
    {
      "planId": "demo-plan-001",
      "noticesCollected": 5,
      "outcome": "completed",
      "error": null
    }
  ]
}
```

`MockRssPlatformParser` returns fixture notices. If the plan already ran today
(idempotency guard fired), `outcome` shows `"skipped"` and `totalNoticesCollected`
is `0`.

### Step 6: Verify the ledger

With `Bidding:LedgerRoot` set, the directory contains two JSON Lines files after
the first successful run:

```
C:/tmp/bidding-ledger/
  notices.jsonl   ← fingerprint dedup ledger (one record per fingerprint)
  history.jsonl   ← execution history (one record per (planId, date))
```

Sample `notices.jsonl` line:

```json
{"fingerprint":"abc123...","firstSeenAt":"2026-08-13T09:00:00Z","notifiedAt":"2026-08-13T09:00:01Z"}
```

Sample `history.jsonl` line:

```json
{"planId":"demo-plan-001","executionDate":"2026-08-13","status":"Completed","noticesCollected":5,"noticesDeduplicated":5,"noticesNotified":5}
```

### Step 7: Verify dry-run notification in logs

With `DryRun: true`, no real notification is sent. Coordinator log shows:

```
info: ScheduledCollectionCoordinator  Plan demo-plan-001 collected 5 notices.
info: ScheduledCollectionCoordinator  Plan demo-plan-001: 5 new notices after deduplication (from 5).
info: ScheduledCollectionCoordinator  Plan demo-plan-001 completed successfully. Notification <dry-run-id> delivered.
```

To enable real delivery: set `Webhook:DryRun: false` and `Webhook:Url` to a valid
HTTPS endpoint (not an IP literal, not a private address). For email: set
`Smtp:Enabled: true` plus SMTP credentials from environment variables only.

### Layer Trace Summary

| Layer | Action | Component | Observable artifact |
|-------|--------|-----------|---------------------|
| 1 Keywords | Plan keywords → BiddingCollectionRequest | ScheduledCollectionPlan | Config / API request body |
| 2 Ingest | Platform queried, notices parsed | MockRssPlatformParser | Log: "collected N notices" |
| 3 Knowledge | Fingerprint registered, dedup | JsonLinesBiddingNoticeLedger | `notices.jsonl` on disk |
| 4 Content | NotificationMessage rendered (text+markdown) | ScheduledCollectionCoordinator | Log: dry-run result |
| 5 Delivery | Channel sends (or dry-runs) | SmtpNotificationChannel / WebhookNotificationChannel | `history.jsonl`: status=Completed |

---

## 3.4 Phase 2 Baseline Regression

`phase2-routing-v1` is a published Workflow hosted in the local Dify instance.
It is **not** stored as a JSON file in this repository. The established baseline is
**22 nodes / 22 edges**, published on 2026-08-06 at 06:53:00.

### To verify the baseline

This check requires access to the running Dify instance.

1. Open the Dify UI (default: `http://localhost/apps`).
2. Navigate to the `phase2-knowledge-routing` Workflow.
3. Open the published version `phase2-routing-v1`.
4. Confirm the node count and edge count in the graph editor:
   - Expected: **22 nodes, 22 edges**
   - Published timestamp: `2026-08-06 06:53:00`

Via Dify API (requires a workflow API key):

```bash
curl http://localhost/v1/workflows/run \
  -H "Authorization: Bearer <dify-workflow-api-key>" \
  -H "Content-Type: application/json" \
  -d '{"inputs": {"query": "当前市场趋势"}, "response_mode": "blocking", "user": "regression-check"}'
```

Expected: HTTP 200, `"status": "succeeded"`. A web-intent query routes directly to
Tavily, confirming the workflow is live without touching the P2/P3 dataset branches.

### Codebase confirmation

A full-text search of this repository confirms:

- No Workflow JSON, YAML, or Dify export file is stored in `src/`, `scripts/`, or `docs/`.
- No `.NET` source file in `src/` directly references or calls `phase2-routing-v1`.
- All occurrences of `phase2-routing-v1` in the repository are read-only documentation
  references in `docs/ops/phase2-workflow-routing-runbook.md`,
  `docs/ops/phase2-baseline-checklist.md`, and plan documents, all stating the
  workflow must not be modified.

The Phase 5 implementation adds no Dify workflow calls and does not import, export,
or modify any Dify workflow. The 22-node/22-edge baseline is unchanged by this phase.

### Phase 3 baseline

`phase3-content-generation` and `phase3-image-generation-api2img` remain as
independent, unpublished Dify Workflow drafts. No Phase 5 code touches them.

---

## 3.5 Demo Script

**Audience:** Business stakeholders
**Duration:** ~10 minutes
**Prerequisites:** Worker and API running with demo plan registered; `DryRun: true`

---

### Opening (2 minutes)

*"We've built a market intelligence agent that automates five tasks our team
currently does manually: defining the keywords we care about, searching and
ingesting market information, organizing it into a knowledge base, generating
reports on demand, and — the newest capability — automatically collecting
nationwide bidding notices around the clock.*

*Today I'll show the fifth layer: the bidding collection and push notification
system. This runs unattended. Once configured, it executes every day at a set
UTC time, deduplicates notices across runs so you never receive the same notice
twice, and delivers a summary to your email or group chat."*

---

### Demo Flow (8 minutes)

**Scene 1 — Worker already running (1.5 min)**

Show the Worker terminal log:

```
Scheduled bidding collection service started. Poll interval: 00:01:00.
```

*"The service polls every minute. When a plan's scheduled time arrives, it runs
automatically. After a restart, it picks up right away without waiting for the
next tick."*

**Scene 2 — Trigger on-demand via API (2 min)**

```bash
curl -X POST http://localhost:5000/api/bidding/collect \
  -H "Content-Type: application/json" \
  -H "X-Agent-Api-Key: demo-key-local" \
  -d '{"planIds": ["demo-plan-001"]}'
```

Show the JSON response: `plansExecuted: 1, totalNoticesCollected: 5, status: success`.

*"The API lets us trigger any plan immediately — useful for ad-hoc collection
outside the normal schedule. The response shows how many notices were collected
and whether any were new."*

**Scene 3 — Show notices in the ledger (1.5 min)**

```bash
cat C:/tmp/bidding-ledger/notices.jsonl
```

Point out `fingerprint`, `firstSeenAt`, `notifiedAt`.

*"Every notice gets a fingerprint derived from its source platform, URL, and
title. The ledger persists across restarts. Even if the service restarts overnight,
it will not resend notices you already received."*

**Scene 4 — Show notification delivered dry-run (1.5 min)**

Point to the Worker log:

```
Plan demo-plan-001 completed successfully. Notification <id> delivered.
```

*"DryRun is enabled in this demo — the notification is fully formatted and logged,
but not actually sent. The log shows exactly what your email or group chat would
receive. Enabling real delivery is a one-line configuration change, made only after
confirming the recipient list."*

**Scene 5 — Show idempotency (1 min)**

Run the same curl again. Show `"outcome": "skipped"`.

*"The system already ran this plan today. The idempotency guard prevents a second
delivery. This protection holds across restarts — the execution history is stored
on disk, not only in memory."*

**Scene 6 — Five-layer architecture (1 min)**

*"This is Layer 5 of five. Layer 1 is keyword definition. Layer 2 is searching
and ingesting market research via the Dify knowledge retrieval workflow. Layer 3
is organizing those materials into our knowledge datasets. Layer 4 is generating
reports — documents, presentations, articles — via the content generation workflow.
All five layers run independently and do not block each other."*

---

### Fallback Talking Points

| Symptom | Likely cause | Talking point |
|---------|-------------|---------------|
| `totalNoticesCollected: 0` | Mock parser returned no fixture matches | "The mock uses fixture data. Live platforms return real notices once the platform list is approved." |
| `"outcome": "skipped"` on first attempt | Plan already ran today from a prior test | "The idempotency guard is working correctly — this prevents duplicate pushes in production." |
| HTTP 401 | Wrong or missing `X-Agent-Api-Key` | "Authentication is working. Let me use the correct key." |
| HTTP 500 | Configuration gap — check coordinator logs | "There is a configuration issue I will fix offline. The architecture and flow still apply." |
| No notification in logs | `Webhook:Enabled: false` | "Notifications are disabled by default for safety. Real delivery requires explicit opt-in after confirming recipients." |
| `notices.jsonl` not found | `LedgerRoot` not configured | "Without LedgerRoot set, the ledger is in-memory only. The pipeline still runs; cross-restart persistence is inactive." |

---

## 3.6 Known Limitations and Pending Work

| Item | Status | Impact |
|------|--------|--------|
| P5-05b: Real platform parsers | Blocked — awaiting business approval of platform list | Only mock (fixture) data collected; no live bidding notices |
| Layer 4 Dify integration | Future work | Phase 3 content-generation Workflow not yet wired into notification rendering |
| Plan source persistence | `InMemoryScheduledCollectionPlanSource` backed by DI registration | Plans cannot be added or changed without restarting the service |
| Scale testing | Not yet performed | Behavior with multiple real platforms and high notice volumes is untested |
| Multi-platform collection | Platforms iterated serially by `CompositeBiddingNoticeCollector` | No parallel collection; acceptable for ≤5 platforms, revisit at scale |
| Real SMTP/Webhook smoke | Not yet performed end-to-end | Delivery tested at unit/integration level; no live server smoke test |

---

## 3.7 Troubleshooting

**Build fails**

```bash
dotnet --version   # must show 8.x
dotnet build
```

**`bidding_source_not_configured` in response**

`IBiddingNoticeCollector` is not registered. Confirm `AddBiddingCollectionInfrastructure()`
is called in `Program.cs` (API) and in the Worker's service registration.

**`notification_not_configured` in response**

The notification channel is disabled. Set `Bidding:Webhook:Enabled: true` or
`Bidding:Smtp:Enabled: true`. With `DryRun: true` the notification is rendered and
logged without being delivered — safe for development and demo.

**Plan never triggers on schedule**

Verify `ExecutionTimeUtc` in the plan matches the current UTC time-of-day.
`IsDueAt` requires `currentTime.TimeOfDay >= ExecutionTimeUtc` and the plan has not
already completed today. Use `POST /api/bidding/collect` to trigger immediately
regardless of schedule.

**`plan_disabled` in response**

`ScheduledCollectionPlan.Enabled` is `false`. Set it to `true` in the plan source.

**Ledger or history file corrupted**

Files are isolated automatically: the corrupted file is renamed to
`.corrupted.YYYYMMDD-HHmmss` and the in-memory state is reset. The service
continues running.

Note: a corrupted `history.jsonl` clears the execution slots for the current day,
which may allow re-execution. This is intentional (fail-open): it is safer to
re-send a notice once than to silently suppress one that was never sent.

**Webhook URL rejected (SSRF guard)**

The URL must be HTTPS, must not be an IP literal, and must not resolve to a
private, loopback, or link-local address. See `Infrastructure/Notifications/SsrfGuard.cs`
for the full rejection rules. Legitimate group chat webhooks (DingTalk, WeCom) satisfy
all conditions; internal test servers typically do not.

**Dify Workflow not accessible**

Start Dify: `.\scripts\start-dify-local.ps1`. See `docs/ops/local-dify-runbook.md`
for startup and troubleshooting.

---

*End of runbook.*
