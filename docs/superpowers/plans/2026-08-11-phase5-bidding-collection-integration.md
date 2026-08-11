# 阶段五：招投标采集与整体联调实施计划

> **日期：** 2026-08-11
> **原定阶段窗口：** 2026-09-07 ~ 2026-09-18
> **实际启动：** 2026-08-11（提前 27 天，阶段四核心验收已完成）
> **风险分级：** **高风险**——涉及外部平台依赖、凭据管理、不可逆外部动作（邮件/群消息推送）、采集合规边界，且首批平台清单尚未确定。按 CLAUDE.md 要求，本阶段必须走 Superpowers 流程，最终验收前必须通过 `/codex:adversarial-review`。

## 1. 权威验收标准

以下为《智能体项目开发流程计划》第六节原文口径，本计划不改写、不降低：

| 任务项 | 原定时间 | 验收标准 |
|---|---|---|
| 重点区域/行业招投标平台调研与采集规则梳理 | 09-07 ~ 09-08 | 确定首批接入的平台清单和采集规则 |
| 招投标资料采集模块开发 | 09-09 ~ 09-11 | 首批平台可正常采集公告和标书资料 |
| 定时关键词采集与推送功能接入（群/邮箱） | 09-14 | 定时任务可按设定时间自动执行并推送 |
| 全流程联调测试（五层任务全链路走一遍） | 09-15 ~ 09-17 | 从关键词搜索到内容产出全链路无阻塞 |
| 汇报演示准备 | 09-18 | 演示材料就绪，可现场展示核心场景 |

《建设方案》补充口径：招投标采集为"五层里技术难度最高"的一层，**数据源分散是最大难点**；定时采集推送的目标是"无人值守的日常情报收集"。

"五层任务全链路"指《建设方案》1.2 的五层：列关键词 → 搜资料入库 → 沉淀成知识 → 按任务生成内容 → 收集全国招标信息。

## 2. 起点基线

阶段四已交付、本阶段可直接复用的构件：

| 构件 | 位置 | 复用方式 |
|---|---|---|
| 来源 URL 授权策略 | `Application/Media/MediaSourceUriPolicy.cs` | 提升为共享策略，招投标采集复用 allowlist、拒绝 `file://`/IP/回环/userinfo |
| 受控 HTTP 采集器 | `Infrastructure/Media/HttpChannelMediaCollector.cs` | 作为招投标采集器的实现范式（大小/重定向/超时/媒体类型上限、幂等 inflight 表） |
| 作业协调器 | `Application/Media/InMemoryMediaJobCoordinator.cs` | 扩展作业种类，或按同构模式新建招投标作业协调器 |
| 稳定失败码目录 | `Application/Media/MediaContracts.cs` | 沿用 `MediaFailureCategory` 分类法，新增招投标专用码 |
| 服务间鉴权 | `Api/ServiceAuthorization.cs` | 新端点直接复用 `X-Agent-Api-Key` 固定时间比较 |
| 本地 sidecar 范式 | `scripts/media/*_sidecar.py` | 如需解析类外部依赖，沿用回环绑定 + allowed-root + 服务间 key |

**测试基线：** 152 项 .NET 测试全通过（0 跳过），Python contract test 5 项。

## 3. 核心策略：先纵切一刀，再横向铺平台

"越早看到效果越好"决定了任务顺序。数据源分散是最大难点，但它是**横向铺量**问题；而"关键词 → 采集 → 去重 → 摘要 → 推送"是**纵向链路**问题。先用本地 mock 招投标平台把纵向链路一次打通，效果立即可见；平台清单确认后再逐个接入，每接一个平台只是新增一个 provider 配置，不动链路。

这与阶段四已验证的路径一致：ASR/OCR/TTS 都是先 fake adapter 定契约、再接真实 sidecar。

**最小可见成果（目标 08-14）：** 输入一组关键词，系统按 mock 平台采集出结构化公告清单，完成去重和摘要，生成一份可预览的推送内容（dry-run 不真发），全过程有作业状态可查。

## 4. 目标架构

保持现有依赖方向，新增 `Bidding` 与 `Notifications` 两个领域，与 `Media` 平级：

```text
Dify Workflow / 定时调度器
        |
        v
  Api（鉴权、校验、作业入口）
        |
        v
Application（Bidding 契约、Notifications 契约、去重台账接口）
        |
        v
Infrastructure（HTTP 采集 provider、SMTP/Webhook 通道、台账存储）
        ^
        |
Worker（定时触发、可取消的采集与推送作业）
```

**应用层新增接口（provider-independent）：**

- `IBiddingNoticeCollector`：按关键词/地区/行业采集公告清单，返回结构化 `BiddingNotice`，不返回原始 HTML
- `IBiddingNoticeLedger`：公告去重台账，判定"是否已推送过"，跨重启保持
- `INotificationChannel`：推送通道抽象，实现包含 SMTP 邮件与群 Webhook
- `IScheduledCollectionPlanner`：定时任务定义与触发判定，与具体调度库解耦

**新增稳定失败码（沿用 `MediaFailureCategory` 分类法）：**

| 失败码 | 分类 | 触发条件 |
|---|---|---|
| `bidding_source_not_allowed` | Authorization | 平台不在 allowlist |
| `bidding_source_not_configured` | ProviderUnavailable | 未配置任何平台 provider |
| `keyword_required` / `invalid_keyword` | Validation | 关键词为空或超长 |
| `notice_parse_failed` | Transient | 页面结构变化导致解析失败 |
| `notice_limit_exceeded` | LimitExceeded | 单次采集公告数超上限 |
| `robots_disallowed` | Authorization | robots.txt 明确禁止该路径 |
| `notification_not_configured` | ProviderUnavailable | 推送通道未配置 |
| `notification_rejected` | ProviderUnavailable | 通道返回拒绝 |
| `duplicate_notice_suppressed` | None（正常去重） | 台账命中，非失败 |

## 5. 关键设计决策

### 5.1 去重台账必须持久化——M4-07 由"可选"升为"前置依赖"

阶段四把持久化队列判为 P1 可延后，前提是"内存队列已满足功能验证"。**定时推送场景下这个前提不再成立**：验收标准要求"无人值守"，服务重启后如果台账丢失，下一次定时任务会把已推送过的公告重新推一遍。推送是不可逆外部动作，重复推送直接损害可用性和使用者信任。

**决策：** 不等 M4-07 全量替换队列，先落一个最小持久化台账（公告指纹 + 首次发现时间 + 推送状态），JSON 文件或 SQLite 均可。作业队列本身仍可保持内存态。这样既解除重复推送风险，又不把 2-3 天的队列改造拖进关键路径。

### 5.2 推送默认 dry-run，真实发送需显式开启

推送是本阶段唯一的不可逆外部动作。所有通道默认 `Enabled=false`，并提供 `DryRun` 模式：渲染完整推送内容、写入日志与作业结果，但不实际投递。真实投递需要显式配置且经人工确认收件人清单后开启。

### 5.3 采集合规边界（不可越过）

- 只采集**公开可访问**的招标公告页面，不需要登录即可查看的内容
- 严格遵守 `robots.txt`；明确禁止的路径直接返回 `robots_disallowed`，不尝试绕过
- 请求限速，单平台默认串行 + 最小间隔，不做并发压测式抓取
- 不绕过登录、验证码、访问控制、反爬策略或付费墙——与阶段四同一约束
- User-Agent 如实标识，不伪装成浏览器规避识别
- 不采集、不落库个人信息（联系人姓名、手机号）；只保留公告标题、发布方、时间、金额区间、公告 URL 等公开要素
- 首批平台清单需逐个确认其服务条款允许程序化访问，确认结论记入运营文档

### 5.4 模型与凭据无关

沿用阶段四结论：`src`/`tests`/`scripts`/配置中不写死平台域名以外的任何凭据、SMTP 密码、Webhook 地址。凭据只来自环境变量或本地密钥库。日志不含完整公告正文、收件人地址、Webhook URL、SMTP 凭据。

## 6. 任务分解

### P5-00：合规边界与平台候选盘点（0.5 天）

不需要网络即可完成的部分先做完，避免被网络波动阻塞。

- [ ] 产出 `docs/ops/bidding-collection-compliance.md`：记录 5.3 的合规边界为强制约束，列出逐平台确认清单模板（平台名、公开性、robots 结论、服务条款结论、限速设定、确认人、确认日期）
- [ ] 列出候选平台类型：全国公共资源交易平台、省级公共资源交易中心、行业性招标网站；标注哪些通常提供 RSS/公开列表页
- [ ] 明确"首批"定义：3 个平台起步，不追求覆盖全国

**验收：** 合规文档就位；候选清单与确认模板可交给业务方填写。

### P5-01：招投标应用层契约与 mock 采集器（1 天）

- [ ] `Application/Bidding/BiddingContracts.cs`：`BiddingNotice`（标题、发布方、发布时间、地区、行业、金额区间、公告 URL、来源平台、指纹）、`BiddingCollectionRequest`（关键词集合、地区/行业过滤、时间窗、条数上限）、`BiddingCollectionResult`
- [ ] 输入边界：关键词数量与长度上限、时间窗上限、单次公告条数上限、URL 长度上限
- [ ] `IBiddingNoticeCollector` 接口 + `UnconfiguredBiddingNoticeCollector`（未配置返回 `bidding_source_not_configured`，与阶段四未配置适配器同构）
- [ ] `FakeBiddingNoticeCollector`：本地固定 fixture，覆盖命中/空结果/超上限/取消
- [ ] 公告指纹算法：来源平台 + 公告 URL 规范化 + 标题归一化，抗同一公告多次抓取产生不同指纹
- [ ] 失败码注册进 `MediaFailureCatalog`（或抽出共享 catalog）
- [ ] contract tests：契约边界、指纹稳定性、排序、去重、取消

**验收：** `dotnet test` 全绿；未配置 provider 时安全失败；不发起任何网络请求。

### P5-02：去重台账持久化（1 天）

- [ ] `Application/Bidding/IBiddingNoticeLedger.cs`：`TryRegisterAsync`（首次登记返回 true，重复返回 false）、`MarkNotifiedAsync`、`PruneAsync`
- [ ] `Infrastructure/Bidding/FileBiddingNoticeLedger.cs`：JSON 持久化到受控目录，启动时加载，保留期可配置（默认 90 天），写入走临时文件 + 原子替换避免半写
- [ ] 台账路径复用 `IMediaAssetPathResolver` 的受控根目录约束，不接受用户可控路径
- [ ] 并发安全：单进程内加锁，跨进程假设单实例部署（写入文档）
- [ ] tests：重复登记被抑制、重启后台账仍生效、损坏文件不导致启动崩溃且不静默清空台账

**验收：** 重启后重复公告不再登记；台账损坏时行为明确（拒绝启动或隔离损坏文件，不静默丢弃）。

### P5-03：推送通道（1.5 天）

- [ ] `Application/Notifications/NotificationContracts.cs`：`NotificationMessage`（标题、摘要正文、条目清单、生成时间）、`NotificationResult`（含稳定失败码）
- [ ] `INotificationChannel` + `UnconfiguredNotificationChannel`
- [ ] `Infrastructure/Notifications/SmtpNotificationChannel.cs`：SMTP 邮件，凭据仅从配置/环境读取，超时与重试有界
- [ ] `Infrastructure/Notifications/WebhookNotificationChannel.cs`：群机器人 Webhook，URL 仅从配置读取并做 allowlist 校验（防 SSRF：拒绝内网地址、IP 直连、非 HTTPS）
- [ ] `DryRun` 模式：渲染内容但不投递，结果标记 `dryRun=true`
- [ ] 内容渲染：Markdown/纯文本双形态，条目数上限，正文长度上限
- [ ] tests：未配置返回 `notification_not_configured`；dry-run 不发请求；SSRF 拒绝用例（内网 IP、回环、非 HTTPS）；日志不含收件人、Webhook URL、SMTP 凭据

**验收：** 默认配置下不可能真发；SSRF 边界有测试；脱敏有真实断言。

### P5-04：定时采集任务（1 天）

- [ ] `Application/Bidding/ScheduledCollectionPlan.cs`：关键词集合、执行时刻、目标通道、启用开关
- [ ] `IScheduledCollectionPlanner`：给定当前时间判定应否触发，与调度库解耦，便于测试注入时间
- [ ] `Worker` 内 `ScheduledBiddingCollectionService`：按计划触发采集 → 台账去重 → 渲染 → 推送；每步失败分类记录
- [ ] **按 (计划 ID, 执行日期) 幂等**：同一天重复触发不产生第二次推送
- [ ] 可取消：Worker 停止时正在执行的作业及时终止
- [ ] tests：注入固定时间验证触发判定；同日重复触发被抑制；采集失败时不推送空内容；全部公告都被去重时不推送空邮件

**验收：** 时间注入测试覆盖触发与幂等；无人值守语义成立。

### P5-05：真实平台接入（2-3 天，需业务方确认清单）

- [ ] `Infrastructure/Bidding/HttpBiddingNoticeCollector.cs`：复用 `MediaSourceUriPolicy` allowlist，实现限速、robots.txt 检查、响应大小上限、超时、有界重试
- [ ] 每平台一个解析适配器，解析失败返回 `notice_parse_failed` 且不影响其他平台
- [ ] 优先接入提供 RSS/结构化列表的平台，降低解析脆弱性
- [ ] 首批 3 个平台的真实采集 smoke（受限速、小样本、不入库敏感字段）
- [ ] 逐平台在合规文档登记确认结论

**验收：** 首批平台可正常采集公告清单；单平台解析失败不影响整体；合规确认已登记。

### P5-06：五层全链路联调与演示准备（1.5 天）

- [ ] 全链路走一遍：关键词 → 招投标采集 → 去重 → 摘要（复用阶段三文本生成）→ 推送；并串联阶段二检索路由与阶段四媒体链路，确认五层无阻塞
- [ ] 阶段二 `phase2-routing-v1` 只读回归：22 节点/22 边未变
- [ ] 演示脚本：核心场景、预期输出、失败兜底话术
- [ ] 产出 `docs/ops/phase5-integration-runbook.md`

**验收：** 五层全链路无阻塞；阶段二基线未被修改；演示材料就绪。

## 7. 时间线

按提前启动重排，里程碑前移，不改变原定验收标准：

| 任务 | 计划日期 | 依赖 | 产出可见性 |
|---|---|---|---|
| P5-00 合规边界与候选盘点 | 08-11 | 无 | 文档 |
| P5-01 契约与 mock 采集器 | 08-12 | P5-00 | 测试可见 |
| P5-02 去重台账持久化 | 08-13 | P5-01 | 测试可见 |
| **纵向链路首次打通（dry-run）** | **08-14** | P5-01/02 | **效果可见** |
| P5-03 推送通道 | 08-14 ~ 08-15 | P5-01 | 可预览推送内容 |
| P5-04 定时采集任务 | 08-16 | P5-02/03 | 无人值守可演示 |
| P5-05 真实平台接入 | 08-18 ~ 08-20 | 业务方确认清单 | 真实公告可见 |
| P5-06 全链路联调与演示 | 08-21 ~ 08-22 | 全部 | 完整演示 |

**关键里程碑：** 08-14 纵向链路 dry-run 打通（最早可见效果）；08-22 阶段五全部验收标准满足，比原定 09-18 提前 27 天。

**阻塞点：** P5-05 依赖业务方确认首批平台清单与服务条款结论。清单未到位时 P5-01~P5-04 全部可正常推进，不停工。

## 8. 风险与缓解

| 风险 | 等级 | 缓解 |
|---|---|---|
| 平台清单未确认，真实接入无法开始 | 高 | mock 采集器先行，链路与平台解耦；清单到位后只新增 provider |
| 页面结构变化导致解析失败 | 高 | 优先 RSS/结构化源；单平台失败隔离；`notice_parse_failed` 可观测 |
| 重复推送损害信任 | 高 | 持久化台账 + (计划, 日期) 幂等 + dry-run 默认 |
| 推送凭据泄露 | 高 | 凭据只走环境变量/密钥库；日志脱敏有测试断言 |
| Webhook URL 被用作 SSRF 跳板 | 中 | Webhook 目标 allowlist + 拒绝内网/IP/非 HTTPS，有测试 |
| 限速不足触发平台封禁 | 中 | 单平台串行 + 最小间隔；小样本 smoke；不做并发抓取 |
| 本环境网络出口不稳定 | 中 | 已实测出现连接重置与超时；真实采集 smoke 需在网络可用时段执行，失败不计为功能缺陷 |
| 阶段二 Workflow 被误改 | 中 | 只读回归核对 22 节点/22 边 |

## 9. 阶段五整体验收标准

1. 首批 3 个平台可正常采集公告清单，产出结构化数据，无原始 HTML 落库
2. 定时任务可按设定时间自动执行并推送，服务重启后不重复推送
3. 五层任务全链路走通一遍，从关键词到内容产出无阻塞
4. 推送默认 dry-run；真实投递需显式开启且收件人经人工确认
5. 合规边界逐平台登记确认，无绕过登录/验证码/反爬的实现
6. 日志与测试不含凭据、收件人、完整公告正文、个人信息
7. 阶段二 `phase2-routing-v1` 保持 22 节点/22 边未修改
8. `dotnet test` 全量通过；新增测试覆盖去重、幂等、SSRF、脱敏、取消
9. 演示材料就绪，可现场展示核心场景
10. 最终验收前通过 `/codex:adversarial-review`

## 10. 交付方式

按 CLAUDE.md 委派策略：Claude 负责计划、契约设计、验收；实现委派 Codex（`/codex:rescue --background`），每个 P5-0x 作为独立委派单元，附验收标准与禁止项。Claude 独立复核 diff、跑构建与测试、核对验收标准，不以 Codex 自述为准。

本阶段为高风险，最终验收前必须 `/codex:adversarial-review`。

**统一禁止项：** 不提交媒体/模型/凭据文件；不写死凭据与收件人；不绕过平台访问控制；不修改 `phase2-routing-v1` 与阶段三 draft；不在日志写入敏感内容；未经确认不真实投递推送。

## 11. 技术债与本阶段不做的事

按"技术债后续优化"的决策，以下项不进入阶段五关键路径：

- M4-05 架构加固 M5-SEC-01..04（TOCTOU、进程树终止），约束已记录于 `docs/ops/m4-05-security-constraints.md`
- M4-07 作业队列全量持久化替换——仅落最小去重台账（见 5.1）
- 真实视频号采集 smoke、ASR/OCR 真实语料验收（待平台与语料授权）
- TTS 真实音色（待商业授权与人工试听），当前 placeholder 后端已显式标记
- 字幕烧制、转场特效、云 TTS/ASR 接入


