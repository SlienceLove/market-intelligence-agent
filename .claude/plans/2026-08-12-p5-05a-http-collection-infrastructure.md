# P5-05a: HTTP 采集基础设施实施计划

> **日期：** 2026-08-12  
> **任务：** 招投标 HTTP 采集器基础设施（platform-agnostic layer）  
> **前置依赖：** P5-01 ~ P5-04a 已完成（260 测试通过）  
> **阻塞状态：** 不依赖业务方平台清单，可独立推进  
> **预计工期：** 1.5-2 天  
> **委派策略：** Codex 实施，Claude 验收

---

## 1. 设计决策总结

### 1.1 架构分层

```
IBiddingNoticeCollector (Application 层接口)
    ↓
CompositeBiddingNoticeCollector (聚合多平台)
    ↓
HttpBiddingNoticeCollector (单平台 HTTP 层)
    ↓ 委派解析给
IPlatformParser (可注入的平台特定解析器)
```

**关键点：**
- `HttpBiddingNoticeCollector` 负责 HTTP 层关注点：robots.txt、限速、大小限制、超时、allowlist
- `IPlatformParser` 负责平台特定解析：RSS/HTML/JSON → `BiddingNotice` 列表
- `CompositeBiddingNoticeCollector` 聚合所有平台，单平台失败隔离

### 1.2 复用现有构件

| 构件 | 位置 | 复用方式 |
|---|---|---|
| `MediaSourceUriPolicy` | Application/Media | 直接复用 allowlist 校验逻辑 |
| `HttpChannelMediaCollector` | Infrastructure/Media | 参考 inflight 幂等、超时处理、重定向校验模式 |
| `BiddingFailureCatalog` | Application/Bidding | 新增采集专用失败码 |
| `ServiceAuthorization` | Api | 后续 P5-05c API 端点复用 |

### 1.3 robots.txt 处理策略

**RFC 9309 核心规则：**
- User-agent 匹配（`*` 通配符，最长匹配优先）
- `Disallow` / `Allow` 规则（最长匹配，Allow 覆盖 Disallow）
- `Crawl-delay`（秒数，影响限速器）

**失败语义：**
- 404 / 410 → 视为允许（无 robots.txt）
- DNS 失败 / 超时 / 5xx → **失败关闭**（deny access），返回 `robots_fetch_failed`
- 解析失败 → **失败关闭**，返回 `robots_parse_failed`

**缓存：**
- TTL 默认 24 小时（可配置）
- 按域名缓存（`https://example.com/robots.txt` → `example.com`）
- 非并发安全（单进程假设，文档记录）

### 1.4 限速器设计

**三层限速：**
1. **单平台串行门控**：同一平台的请求按序发送（`SemaphoreSlim(1, 1)` per platform）
2. **最小间隔**：两次请求间至少间隔 N 秒（默认 2 秒，下限 1 秒）
3. **全局 QPS 上限**：所有平台合计不超过 5 QPS（`SemaphoreSlim` + token bucket or sliding window）

**配置项：**
```csharp
public sealed record BiddingCollectorOptions
{
    public bool Enabled { get; init; }
    public string[]? AllowedHosts { get; init; }
    public int MinIntervalSeconds { get; init; } = 2;  // 下限 1 秒
    public int GlobalQpsLimit { get; init; } = 5;
    public int MaxResponseBytes { get; init; } = 2_097_152;  // 2 MB
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxRedirects { get; init; } = 3;
    public string UserAgent { get; init; } = "MarketIntelligenceAgent/1.0 (+https://github.com/Sliencelove/market-intelligence-agent)";
    public TimeSpan RobotsCacheTtl { get; init; } = TimeSpan.FromHours(24);
}
```

### 1.5 IPlatformParser 接口

```csharp
public interface IPlatformParser
{
    /// <summary>
    /// Stable platform identifier matching the collector's SourcePlatform.
    /// </summary>
    string PlatformId { get; }
    
    /// <summary>
    /// Parse response content into notices. Throws for unrecoverable errors,
    /// returns empty on "valid response, no matching results".
    /// </summary>
    Task<IReadOnlyList<BiddingNotice>> ParseAsync(
        string contentType,
        string content,
        CancellationToken cancellationToken = default);
}
```

**初始实现：**
- `MockRssPlatformParser`：解析固定 RSS fixture（用于测试）
- `UnconfiguredPlatformParser`：返回空列表（占位）

### 1.6 测试策略

**本地 mock HTTP 服务器：**
- 使用 `WireMock.Net` 或手写 `TestServer`
- 模拟 robots.txt 响应（允许、禁止、404、超时）
- 模拟限速场景（验证间隔时序）
- 模拟大响应、超时、重定向到非 allowlist 域名

**测试覆盖：**
1. robots.txt 允许/禁止路径匹配
2. robots.txt fetch 失败 → deny
3. 限速：单平台串行 + 最小间隔 + 全局 QPS
4. 响应大小超限拒绝
5. 超时处理
6. 重定向到非 allowlist 域名被拒绝
7. 解析失败隔离（单平台失败不影响其他）
8. 取消传播
9. User-Agent 断言（mock 服务器回显请求头）
10. 日志脱敏（不含 PII、不含完整 URL query string）

---

## 2. 文件清单

### 新增文件

**Application 层（接口与契约）：**
1. `src/MarketIntelligence.Agent.Application/Bidding/IPlatformParser.cs`
2. `src/MarketIntelligence.Agent.Application/Bidding/UnconfiguredPlatformParser.cs`
3. `src/MarketIntelligence.Agent.Application/Bidding/CompositeBiddingNoticeCollector.cs`

**Infrastructure 层（实现）：**
4. `src/MarketIntelligence.Agent.Infrastructure/Bidding/BiddingCollectorOptions.cs`（扩展现有 `BiddingOptions`）
5. `src/MarketIntelligence.Agent.Infrastructure/Bidding/RobotsTxtCache.cs`
6. `src/MarketIntelligence.Agent.Infrastructure/Bidding/RobotsTxtRule.cs`（数据结构）
7. `src/MarketIntelligence.Agent.Infrastructure/Bidding/BiddingRateLimiter.cs`
8. `src/MarketIntelligence.Agent.Infrastructure/Bidding/HttpBiddingNoticeCollector.cs`
9. `src/MarketIntelligence.Agent.Infrastructure/Bidding/MockRssPlatformParser.cs`（测试用）

**Tests：**
10. `tests/MarketIntelligence.Agent.Tests/RobotsTxtCacheTests.cs`
11. `tests/MarketIntelligence.Agent.Tests/BiddingRateLimiterTests.cs`
12. `tests/MarketIntelligence.Agent.Tests/HttpBiddingNoticeCollectorTests.cs`
13. `tests/MarketIntelligence.Agent.Tests/CompositeBiddingNoticeCollectorTests.cs`

**Docs：**
14. `docs/ops/bidding-robots-txt-handling.md`（robots.txt 处理决策与失败语义文档）

### 修改文件

15. `src/MarketIntelligence.Agent.Application/Bidding/BiddingContracts.cs`（新增失败码）
16. `src/MarketIntelligence.Agent.Infrastructure/Bidding/BiddingOptions.cs`（新增 Collector 配置节）
17. `docs/PROGRESS.md`（更新 P5-05a 状态）

---

## 3. 新增失败码

在 `BiddingFailureCatalog` 中注册：

| 失败码 | 分类 | 重试? | 触发条件 |
|---|---|---|---|
| `robots_fetch_failed` | Transient | Y | robots.txt DNS/网络/超时失败 |
| `robots_parse_failed` | ProviderUnavailable | N | robots.txt 解析失败 |
| `robots_disallowed` | Authorization | N | 路径明确被 Disallow |
| `rate_limit_exceeded` | LimitExceeded | Y | 平台内部限速触发（预留，初版不实现） |
| `parse_timeout` | Transient | Y | 解析器超时 |
| `parse_failed` | ProviderUnavailable | N | 解析器返回错误 |
| `collector_not_configured` | ProviderUnavailable | N | HTTP 采集器未配置 |

**注意：** `notice_parse_failed` 已在 P5-01 注册，复用。

---

## 4. 实施顺序

### Phase 1: robots.txt 处理（0.5 天）

1. `RobotsTxtRule.cs`：数据结构（User-agent, Disallow, Allow, Crawl-delay）
2. `RobotsTxtCache.cs`：
   - `Task<bool> IsAllowedAsync(Uri uri, CancellationToken)`
   - `Task<int?> GetCrawlDelayAsync(string host, CancellationToken)`
   - 内部：fetch + parse + cache（按域名，TTL 24h）
   - 失败关闭：DNS/超时/5xx → deny
3. `RobotsTxtCacheTests.cs`：
   - 允许/禁止路径匹配
   - 最长匹配优先
   - User-agent `*` 匹配
   - 404 → 允许
   - 超时 → 拒绝
   - 缓存命中/过期

### Phase 2: 限速器（0.5 天）

4. `BiddingRateLimiter.cs`：
   - `Task AcquireAsync(string platformId, CancellationToken)`
   - 单平台串行：`ConcurrentDictionary<string, SemaphoreSlim>`
   - 最小间隔：上次释放时间 + MinIntervalSeconds
   - 全局 QPS：`SemaphoreSlim` + 滑动窗口计数
5. `BiddingRateLimiterTests.cs`：
   - 单平台串行验证
   - 最小间隔时序断言（`Stopwatch`）
   - 全局 QPS 上限
   - 取消传播

### Phase 3: HTTP 采集器（0.5 天）

6. `BiddingCollectorOptions.cs`：配置模型
7. `HttpBiddingNoticeCollector.cs`：
   - 构造函数注入 `HttpClient`, `RobotsTxtCache`, `BiddingRateLimiter`, `IPlatformParser`, `IOptions<BiddingCollectorOptions>`
   - `CollectAsync` 流程：
     1. 校验输入（复用 `BiddingCollectionRequest.Validate()`）
     2. 构造平台搜索 URL（委派给 parser 或传入）
     3. robots.txt 检查
     4. 限速器 acquire
     5. HTTP fetch（User-Agent, 超时, 大小限制, 重定向校验）
     6. 委派 parser
     7. 返回 `BiddingCollectionResult`
8. `MockRssPlatformParser.cs`：解析固定 RSS fixture
9. `HttpBiddingNoticeCollectorTests.cs`：
   - robots 允许/拒绝
   - 限速间隔
   - 大小超限
   - 超时
   - 重定向到非 allowlist
   - 解析失败返回 `notice_parse_failed`
   - User-Agent 回显断言
   - 取消

### Phase 4: 聚合层（0.5 天）

10. `IPlatformParser.cs` + `UnconfiguredPlatformParser.cs`
11. `CompositeBiddingNoticeCollector.cs`：
    - 构造函数注入 `IEnumerable<HttpBiddingNoticeCollector>`（通过 keyed DI 或工厂）
    - 扇出到所有已配置平台
    - 单平台失败隔离（记录 FailureCode，继续其他）
    - 合并结果 → 去重（按 Fingerprint）→ 排序（PublishedAt desc）→ 截断至 MaxResults
12. `CompositeBiddingNoticeCollectorTests.cs`：
    - 多平台聚合
    - 单平台失败隔离
    - 去重验证
    - 结果排序与截断

---

## 5. DI 注册

在 `Infrastructure` 层新增扩展方法（或扩展现有方法）：

```csharp
public static IServiceCollection AddBiddingCollector(
    this IServiceCollection services,
    IConfiguration configuration)
{
    services.Configure<BiddingCollectorOptions>(
        configuration.GetSection("Bidding:Collector"));
    
    services.AddHttpClient<HttpBiddingNoticeCollector>()
        .ConfigureHttpClient((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<BiddingCollectorOptions>>().Value;
            client.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
            client.Timeout = Timeout.InfiniteTimeSpan;  // 手动超时控制
        });
    
    services.AddSingleton<RobotsTxtCache>();
    services.AddSingleton<BiddingRateLimiter>();
    
    // Platform parsers
    services.AddSingleton<IPlatformParser, MockRssPlatformParser>();
    services.AddSingleton<IPlatformParser, UnconfiguredPlatformParser>();
    
    // Collectors
    services.AddTransient<HttpBiddingNoticeCollector>();  // 一个实例 per platform
    services.AddSingleton<IBiddingNoticeCollector, CompositeBiddingNoticeCollector>();
    
    return services;
}
```

**注意：** 如果 `IBiddingNoticeCollector` 已注册为 `FakeBiddingNoticeCollector`，需要按配置条件切换：
```csharp
var options = configuration.GetSection("Bidding:Collector").Get<BiddingCollectorOptions>();
if (options?.Enabled == true)
{
    services.AddSingleton<IBiddingNoticeCollector, CompositeBiddingNoticeCollector>();
}
else
{
    services.AddSingleton<IBiddingNoticeCollector, UnconfiguredBiddingNoticeCollector>();
}
```

---

## 6. 配置示例

`appsettings.json`（新增 `Bidding:Collector` 节）：

```json
{
  "Bidding": {
    "LedgerRoot": "data/bidding",
    "Collector": {
      "Enabled": true,
      "AllowedHosts": [
        "ccgp.gov.cn",
        "cebpubservice.com",
        "jsggzy.jiangsu.gov.cn"
      ],
      "MinIntervalSeconds": 2,
      "GlobalQpsLimit": 5,
      "MaxResponseBytes": 2097152,
      "Timeout": "00:00:30",
      "MaxRedirects": 3,
      "UserAgent": "MarketIntelligenceAgent/1.0 (+https://github.com/Sliencelove/market-intelligence-agent)",
      "RobotsCacheTtl": "1.00:00:00"
    }
  }
}
```

---

## 7. 验收标准

1. **构建通过**：`dotnet build` 0 警告 0 错误
2. **测试通过**：`dotnet test` 全绿，新增测试约 30-40 项
3. **robots.txt 处理**：
   - 允许/禁止路径匹配测试覆盖
   - fetch 失败 → deny（失败关闭）有测试
   - 缓存命中/过期有测试
4. **限速器**：
   - 单平台串行有时序断言
   - 最小间隔有 `Stopwatch` 验证
   - 全局 QPS 有测试
5. **HTTP 采集器**：
   - User-Agent 断言通过（mock 服务器回显验证）
   - 重定向到非 allowlist 被拒绝
   - 大小超限拒绝
   - 超时有测试
   - 取消传播有测试
6. **聚合层**：
   - 单平台失败隔离有测试
   - 去重、排序、截断有测试
7. **日志脱敏**：
   - 日志不含完整 URL query string
   - 日志不含 PII
8. **配置安全**：
   - 默认 `Enabled=false`
   - 未配置时返回 `collector_not_configured`
9. **文档就位**：
   - `docs/ops/bidding-robots-txt-handling.md` 记录失败语义与决策
10. **无网络请求**：
    - 测试全部使用 mock 服务器
    - 不访问真实招投标平台

---

## 8. 禁止项（强制约束）

1. **不访问真实平台**：所有测试使用 local mock HTTP server
2. **不提交凭据**：不写死平台 API key、cookies、tokens
3. **不提交 PII**：测试 fixture 不含真实联系人、手机号
4. **不修改阶段二 Workflow**：`phase2-routing-v1` 保持 22 节点/22 边
5. **不修改阶段三 draft**：`phase3-content-generation` 与 `phase3-image-generation-api2img` 保持只读
6. **不绕过合规**：robots.txt 禁止的路径必须返回 `robots_disallowed`，不尝试访问

---

## 9. 交付方式

**委派 Codex 实施：**
- Phase 1-4 按顺序推进，每个 phase 独立 commit
- 每个 commit 必须 `dotnet build && dotnet test` 通过
- Commit message 格式：`feat(p5-05a): <phase-description>`

**Claude 独立验收：**
- 复核 diff，确认无凭据、无 PII、无真实平台请求
- 运行 `dotnet test`，确认新增测试数量与通过率
- 检查 User-Agent 断言、限速时序断言、robots 失败关闭测试
- 确认 `Enabled=false` 时返回 `collector_not_configured`
- 验收通过后合并到 `feat/p5-bidding-collection`

---

## 10. 风险与缓解

| 风险 | 等级 | 缓解 |
|---|---|---|
| robots.txt 解析复杂度被低估 | 中 | RFC 9309 只实现核心规则（User-agent, Disallow, Allow, Crawl-delay），不支持 Sitemap 等扩展指令 |
| 限速器 token bucket 实现有 bug | 中 | 先实现最简单的 semaphore + 间隔时间，全局 QPS 用滑动窗口计数而非 token bucket |
| 本地 mock 服务器与真实平台行为差异 | 高 | 在 P5-05b 真实平台接入时补充真实 smoke test，mock 层只验证契约 |
| Composite 聚合层依赖注入复杂 | 低 | 使用 `IEnumerable<HttpBiddingNoticeCollector>` 注入，每个平台一个实例 |

---

## 11. 下一步

P5-05a 完成后：
1. **P5-05c**：按需采集 API 端点（0.5 天）
2. **P5-Review**：对抗评审 P5-03/04/05a（1 天）
3. **P5-06**：五层全链路联调与演示（1 天）
4. **P5-05b**：真实平台接入（待业务方确认清单）

---

**计划状态：** 待用户确认后开始 Phase 1
