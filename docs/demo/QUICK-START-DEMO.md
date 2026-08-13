# Phase 5 本地演示快速指南

## 前置条件

- .NET 8 SDK 已安装
- Docker Desktop (WSL2) 运行中（Dify 需要）
- 本地 Dify 实例：`http://127.0.0.1:18080`

---

## Step 1: 启动 .NET API（推荐先测）

```bash
cd D:\Project\Github\Agent\market-intelligence-agent\src\MarketIntelligence.Agent.Api
dotnet run
```

**端口确认：** 启动后看到 `Now listening on: http://localhost:5294`

---

## Step 2: 测试 API 端点

### 2.1 健康检查

```bash
curl http://localhost:5294/health
```

**预期：** `{"status":"ready"}`

### 2.2 触发招标采集（无需 Dify）

```bash
curl -X POST http://localhost:5294/api/bidding/collect \
  -H "Content-Type: application/json" \
  -H "X-Agent-Api-Key: demo-key-2026" \
  -d "{}"
```

**预期响应：**
```json
{
  "plansExecuted": 1,
  "totalNoticesCollected": 3,
  "status": "success",
  "skippedCount": 0,
  "plans": [
    {
      "planId": "demo-plan-001",
      "noticesCollected": 3,
      "outcome": "completed",
      "error": null
    }
  ]
}
```

### 2.3 查看持久化数据

```bash
# 查看去重指纹 ledger
cat C:/tmp/bidding-demo-ledger/notice-fingerprints.jsonl

# 查看执行历史
cat C:/tmp/bidding-demo-ledger/scheduled-collection-history.jsonl
```

**第一次运行：** 3 条公告
**第二次运行：** `"outcome": "skipped"` (因为 `demo-plan-001` 今天已执行过)

---

## Step 3: 连接 Dify（可选）

### 3.1 确认 Dify 运行

```powershell
# 启动 Dify（如果未运行）
.\scripts\start-dify-local.ps1 -OpenBrowser

# 访问 http://127.0.0.1:18080
```

### 3.2 在 Dify 创建 HTTP Tool

1. 打开 Dify → **工具** → **自定义工具** → **创建工具**
2. 工具名称：`BiddingCollector`
3. 配置：

**OpenAPI Schema:**
```yaml
openapi: "3.1.0"
info:
  title: Bidding Collection API
  version: "1.0"
servers:
  - url: http://host.docker.internal:5294
paths:
  /api/bidding/collect:
    post:
      summary: 触发招标采集
      operationId: collectBidding
      requestBody:
        content:
          application/json:
            schema:
              type: object
              properties:
                planIds:
                  type: array
                  items:
                    type: string
                  description: 计划ID列表，留空=执行所有计划
                asOf:
                  type: string
                  description: 执行日期(yyyy-MM-dd)，默认今天
      responses:
        '200':
          description: 成功
          content:
            application/json:
              schema:
                type: object
```

**鉴权设置：**
- Type: `Custom`
- Header Name: `X-Agent-Api-Key`
- Header Value: `demo-key-2026`

4. **测试工具**：在 Dify UI 点"测试"，应该返回3条公告

### 3.3 在 Workflow 中使用

1. 创建新 Workflow 或打开现有的
2. 添加节点 → **工具** → **BiddingCollector**
3. 参数：留空（触发所有计划）或 `{"planIds": ["demo-plan-001"]}`
4. 运行 Workflow → 查看返回的 `plansExecuted` 和 `totalNoticesCollected`

---

## Step 4: Worker 自动调度（可选）

如果要测试定时触发而非手动 API 调用：

```bash
cd D:\Project\Github\Agent\market-intelligence-agent\src\MarketIntelligence.Agent.Worker
dotnet run
```

**日志输出：**
```
[ScheduledBiddingCollectionService] Checking for due plans...
[ScheduledCollectionCoordinator] Executing plan: demo-plan-001
[DemoFixtureBiddingNoticeCollector] Returning 3 fixture notices
[WebhookNotificationChannel] [DRY-RUN] Would POST to: (not configured, using default dry-run)
[ScheduledCollectionCoordinator] Plan demo-plan-001 completed: 3 notices
```

Worker 每 60 秒轮询一次，启动时立即执行一次。

---

## 观察点：五层架构体现

| 层 | 在演示中的体现 |
|----|----------------|
| **Layer 1 关键词** | `demo-plan-001` 的 `Keywords: ["云计算", "软件采购", ...]` |
| **Layer 2 搜资料** | `DemoFixtureBiddingNoticeCollector` 返回3条公告（真实环境=HTTP采集） |
| **Layer 3 沉淀知识** | `JsonLinesBiddingNoticeLedger` 指纹去重，持久化到 `notice-fingerprints.jsonl` |
| **Layer 4 生成内容** | `NotificationMessage` 渲染（Markdown格式，≤20条公告） |
| **Layer 5 推送通知** | `WebhookNotificationChannel` DryRun 模式（日志记录，不实际POST） |

**关键日志验证点：**
- ✅ 去重生效：第二次运行返回 `"outcome": "skipped"`（今天已执行）
- ✅ 指纹持久化：`C:/tmp/bidding-demo-ledger/notice-fingerprints.jsonl` 有3行
- ✅ 历史记录：`scheduled-collection-history.jsonl` 记录 `(demo-plan-001, 2026-08-13)`

---

## 故障排查

### 401 Unauthorized
**原因：** API Key 不匹配
**解决：** 确认 `appsettings.Development.json` 里 `BridgeApiKey` 和 curl 命令的 `X-Agent-Api-Key` 一致

### 500 Internal Server Error
**查看详细日志：**
```bash
cd src/MarketIntelligence.Agent.Api
dotnet run --environment Development
```
检查是否：
- `Notifications.Enabled: true` 未配置（导致 `notification_not_configured`）
- `LedgerRoot` 目录无写权限

### Dify HTTP Tool 调用失败
**检查：**
1. API 是否在运行（`curl http://localhost:5294/health`）
2. Dify 里 URL 是否用 `host.docker.internal:5294`（不是 `localhost`）
3. Header `X-Agent-Api-Key` 是否正确配置

### 第二次运行返回 0 条公告
**正常行为：** `(planId, date)` 去重生效，今天已执行的计划会跳过
**重置演示：** 删除 `C:/tmp/bidding-demo-ledger/scheduled-collection-history.jsonl`，下次运行会重新执行

---

## 下一步

1. ✅ **本地 API 测试通过** → 继续
2. ✅ **Dify HTTP Tool 能调通** → P5-06 五层集成验证完成
3. ⏳ **真实平台集成** → P5-05b（待业务审批平台列表）

**完整技术文档：** `docs/ops/phase5-integration-runbook.md`
