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

两个独立问题：

- **失败开放**：逐分量解析过程中捕获 `IOException` / `UnauthorizedAccessException`
  后返回原始词法路径，等同于把“解析失败”降级为“仅词法检查”通过。
- **校验与使用未绑定**：解析只产出一个时点的路径字符串，不保留文件句柄。可写祖先目录
  可在校验通过之后、FFmpeg 打开输入或创建输出之前被替换为 junction/symlink，从而读写
  `AssetRoot` 之外的位置。

建议：解析不完整时一律失败关闭；并将校验绑定到实际使用——用 no-follow、基于描述符的
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

无法创建符号链接时，测试在任何断言之前正常返回，xUnit 记为通过。在常见的非特权
Windows CI runner 上，整个套件可以全绿而从未验证过文件链接或目录链接的包含性。

（同类缺陷已在 `FfmpegRealBinarySmokeTests` 上修复，改用 `RequiresRealFfmpegFact` 设置 `Skip`。）

建议：把能力缺失报告为真正的 skipped，并要求至少一条具备特权/开发者模式的 CI 通道必须
实际执行这些测试；补充确定性的 junction/reparse point 与链接替换测试，断言真正被打开或
写入的目标仍在 `AssetRoot` 内。

## 后续动作

1. 用失败关闭 + 绑定使用的文件系统处理替换纯字符串包含性校验。
2. 用 OS job/进程组原语约束进程生命周期，并补子孙进程测试。
3. 从 FFmpeg PTS 元数据推导帧时间戳。
4. 让具备建链能力的包含性测试在至少一条 CI 通道中成为强制项。

第 1、2 项的彻底修复会改变进程与文件访问的整体架构，需先确认方案再执行。
