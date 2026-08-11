# M4-05a FFmpeg 运行时 — 对抗性评审结论

- 日期：2026-08-10
- 范围：`feat/m4-05-ffmpeg-runtime` 相对 `main` 的分支 diff（7 个提交）
- 执行方式：`/codex:adversarial-review --base main`
- **结论：needs-attention（未通过验收）**

评审摘要（原文要点）：资产包含性仍可被文件系统竞争绕过；进程取消不保证进程树终止；
采样时间戳未锚定媒体 PTS；包含性测试可能报告假绿色。

## 未修项

### 1. [高] 包含性对链接替换竞争仍可绕过

`src/MarketIntelligence.Agent.Infrastructure/Media/MediaAssetPathResolver.cs:246-310`

**状态:部分修复**

两个独立问题：

- **失败开放(已修)**：逐分量解析过程中捕获 `IOException` / `UnauthorizedAccessException`
  后返回原始词法路径，等同于把”解析失败”降级为”仅词法检查”通过。**已改为
  `TryResolveFinalPath`，解析不完整时返回 false，调用方拒绝请求。**链接环耗尽
  深度限制也改为抛出 `IOException` 而非返回最后一跳。
- **校验与使用未绑定(未修)**：解析只产出一个时点的路径字符串，不保留文件句柄。可写祖先目录
  可在校验通过之后、FFmpeg 打开输入或创建输出之前被替换为 junction/symlink，从而读写
  `AssetRoot` 之外的位置。

建议：解析不完整时一律失败关闭(已完成)；并将校验绑定到实际使用——用 no-follow、基于描述符的
相对打开，或把媒体经安全句柄暂存到服务自有的非 reparse 目录，或对 FFmpeg 做沙箱隔离。

### 2. [高] 取消后子孙进程可能仍在运行

`src/MarketIntelligence.Agent.Infrastructure/Media/FfmpegProcessRunner.cs:144-158`

`KillProcessTree` 丢弃了树终止与 5 秒等待的成功与否，并吞掉信号失败，然后照常返回
取消/超时结果。`WaitForExit` 只回收关联进程，不能证明子孙已退出。持有输出句柄的子孙
进程可能在调用方已开始清理、作业已标记终态之后继续写文件。

建议：用 OS 强制的生命周期容器承载进程（Windows Job Object 或 Unix 进程组/cgroup），
终止容器并确认其为空后再返回；显式暴露 kill/reap 失败；补一个被测进程创建长寿孙进程
的测试。

### 3. [中] 帧序号不等于源时间戳

`src/MarketIntelligence.Agent.Infrastructure/Media/FfmpegVideoFrameSampler.cs:205-209`

JPEG 序号只是 image muxer 连续的输出索引，不是所选帧的源 PTS。`fps` filter 会按时间戳
丢帧或复制帧，输入可能是变帧率或起始 PTS 非零，而文件名始终从 `frame-000001` 开始。
用该索引乘 `SampleInterval` 会标错时间，去重只是在这个人造序列里保留空洞，并未保留媒体时间。

建议：从 FFmpeg 取真实输出 PTS（机器可读的 frame metadata 或受控 timebase + `frame_pts`），
在去重之前把每个文件关联到其 PTS；补测非零起始与变帧率素材，并验证去重后的时间戳。

### 4. [中] 链接包含性测试在无权限时静默通过

`tests/MarketIntelligence.Agent.Tests/MediaAssetPathResolverLinkTests.cs:55-75`

**状态:已修复**

无法创建符号链接时，测试在任何断言之前正常返回，xUnit 记为通过。在常见的非特权
Windows CI runner 上，整个套件可以全绿而从未验证过文件链接或目录链接的包含性。

**已改为 `RequiresSymbolicLinkFact`，在能力探测失败时设置 `Skip`，报告为跳过而非通过。**
增加 `MI_REQUIRE_SYMLINK_TESTS=1` 环境变量门控，在至少一条 CI 通道中将跳过变为硬失败。
补充链接环测试，验证深度限制耗尽时拒绝。

另外新增 junction 测试 4 项：Windows junction 无需管理员权限即可创建（`mklink /J` 在
`IsUserAnAdmin() == 0` 时成功，本机已验证），因此它是比符号链接更现实的攻击面。解析器
同样拦截逃逸 root 的 junction、嵌套 junction 和输出路径下的逃逸 junction。

测试数:135 → 140 (+4 符号链接 +1 环 +4 junction)，配置真实 FFmpeg 时全通过。

## 风险接受决策（2026-08-11）

**决策**: 接受当前实现的已知限制，合并到 main 并继续推进阶段四剩余任务。

**理由**:
1. 阶段四计划 09-04 完成，剩余 24 天；架构改动可能需要 5-7 天，会挤压后续进度
2. 当前实现已修复失败开放和测试假阳性，在受控环境下功能可用
3. 剩余问题需要跨平台架构方案设计，不适合在时间紧张时仓促实施
4. 业务流程验证优先于架构完美化

**约束条件**（见 `docs/ops/m4-05-security-constraints.md`）:
- 媒体资产目录必须由服务独占，无其他进程写入
- 不接受来自不可信来源的资产路径
- 取消操作后需要人工确认无孤儿进程（生产环境需补齐监控）
- 变帧率或非零起始 PTS 素材的帧时间戳可能不准确

**后续架构加固计划**（阶段五后独立安排）:
1. 用失败关闭 + 绑定使用的文件系统处理替换纯字符串包含性校验
2. 用 OS job/进程组原语约束进程生命周期，并补子孙进程测试
3. 从 FFmpeg PTS 元数据推导帧时间戳
4. 让具备建链能力的包含性测试在至少一条 CI 通道中成为强制项

第 1、2 项的彻底修复会改变进程与文件访问的整体架构，需单独排期。
