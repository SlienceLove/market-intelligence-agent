# M4-05a：FFmpeg 运行时接入实施计划

> **日期：** 2026-08-10
> **归属：** 阶段四 M4-03 / M4-05 的运行时补齐项
> **上游计划：** [`2026-08-07-phase4-multimodal-modules.md`](2026-08-07-phase4-multimodal-modules.md)
> **风险分类：** **高风险**。涉及本机进程执行、用户可控输入到命令行参数的映射、文件系统路径解析和临时文件清理。任一环节失守都可能导致任意命令执行或越界文件读写。

## 1. 目标

把已在本机验证过的 FFmpeg 能力真正沉入 .NET 服务层，使 `IProcessRunner` 与 `IVideoFrameSampler` 具备可通过 DI 解析的真实实现，并让 OCR 具备受控的视频帧上游。

本次**不**包含：TTS adapter、音色授权、云 provider 接入、持久化队列、字幕烧制与转场特效。

## 2. 现状与核心缺口

已就位：

- `FfmpegArgumentBuilder.Build` 与 `FfmpegFrameSamplingArgumentBuilder.Build` 已产出参数数组，拒绝非 `asset://`/`fixture://` scheme、绝对路径、盘符和 `..`。
- `FakeProcessRunner`、`FakeVideoCompositionService` 及 4 项帧采样契约测试已覆盖参数与边界。
- FFmpeg 已通过用户范围 `Gyan.FFmpeg.Essentials 8.1.1` 安装，抽帧 smoke 通过。

**核心缺口（本次要解决的根本问题）：** 两个参数构建器把 `asset://...` / `fixture://...` 原样作为 ffmpeg 的 `-i` 实参，把 `media/{jobId}.mp4` 这类相对路径原样作为输出实参。真实 ffmpeg 无法解析这些自定义 scheme，相对路径也依赖进程工作目录。ASR/OCR 走的是「URI 透传给 Python sidecar，由 sidecar 的 `--allowed-root` 解析」，而 FFmpeg 由 .NET 直接拉起进程，因此**受控资产路径解析必须在 .NET 侧新建**。这是本次工作的安全核心，不是附带项。

次要缺口：

- 无 ffprobe 能力，因此输出时长与音画偏移无法测量，M4-05 对应验收项无法达成。
- ffmpeg 可执行文件位置未纳入配置，用户范围安装不在系统 PATH 的稳定位置。

## 3. 实施顺序

### 步骤 1：受控资产路径解析（安全基座，必须先做）

在 `MarketIntelligence.Agent.Infrastructure/Media/` 新增 `MediaAssetPathResolver`，作为唯一的 URI → 物理路径出口。

- 配置新增受控根目录（沿用 `Media` 配置节，如 `Media:AssetRoot`）。根目录必须存在、必须是目录，启动时校验。
- 仅接受 `asset://` 与 `fixture://`。scheme 之外一律拒绝。
- 解析后必须做**规范化后的包含性校验**：对解析结果与根目录同时取 `Path.GetFullPath`，再校验前缀，且前缀比较要带目录分隔符，避免 `root-evil` 命中 `root` 前缀。
- 显式拒绝：`..` 段、绝对路径注入、盘符、UNC 路径、以 `-` 开头的段（防止被 ffmpeg 当作选项解析）。
- **符号链接与重解析点**：解析到最终真实路径后再做包含性校验，防止根目录内的符号链接指向外部。Windows 下需处理 junction 与 reparse point。
- 输出路径同样经此解析器产出绝对路径，不再依赖进程工作目录。

此步骤单独出测试，覆盖每一类拒绝场景。**这一步的测试不通过，后续步骤不要开始。**

### 步骤 2：`FfmpegProcessRunner`

- 使用 `ProcessStartInfo` + `ArgumentList`（逐项添加），`UseShellExecute = false`，`CreateNoWindow = true`。**禁止**拼接 `Arguments` 字符串。
- 可执行文件路径从配置读取（如 `Media:Ffmpeg:ExecutablePath`），未配置或文件不存在时返回明确的未配置失败，不回退到 PATH 猜测。
- 异步读取 stdout/stderr，避免管道缓冲区填满导致死锁。stderr 只保留有界摘要（建议上限 4 KB），不写入完整输出。
- 超时与取消：超时后 kill 整个进程树（`Kill(entireProcessTree: true)`），并区分 `TimedOut` 与 `Cancelled` 两种结果。
- 进程退出后回收，确保取消路径不留孤儿进程。

### 步骤 3：`FfmpegVideoFrameSampler`

- 复用步骤 1 的解析器与步骤 2 的 runner。
- 采样前校验输入媒体类型与 `VideoFrameSamplingOptions`（沿用现有构建器的校验）。
- 采样后枚举实际产出帧文件，按序号映射为 `VideoFrameSample`，时间戳按 `SampleInterval` 推算。
- 强制 `MaxFrames` 上限：即便 ffmpeg 多产出也只登记上限内的帧，超出部分删除。
- 补齐上游计划中缺的两项：最大分辨率（通过 `-vf scale` 限制）与重复帧去重阈值。去重可先做文件大小加内容哈希的保守判定，避免引入图像库依赖。
- 成功与失败路径都清理临时帧目录；失败时不留孤立文件。

### 步骤 4：ffprobe 时长探测与音画偏移

- 新增受控 ffprobe 调用，返回容器时长与音视频流时长。
- 合成后校验输出非空、时长合理、音视频时长差在阈值内；超阈值返回稳定错误码（如 `composition_av_drift`）。
- 阈值可配置，默认建议 200 ms。

### 步骤 5：DI 注册与真实 smoke

- 在 `Infrastructure/ServiceCollectionExtensions.cs` 注册 `IProcessRunner`、`IVideoFrameSampler` 与解析器，绑定新增 options 节。
- 未配置 ffmpeg 路径或根目录时，保持服务可启动，相关能力返回 `provider_not_configured`，**不得假成功**。
- 真实 smoke：本地合成短视频，经 API → 队列 → sampler → OCR sidecar 全链路，记录状态、帧数、时长与偏移，不提交媒体文件。

## 4. 验收标准

功能：

1. `MediaAssetPathResolver` 对根目录内合法资产返回绝对路径；对 `..`、绝对路径、盘符、UNC、`-` 前缀段、非法 scheme 和指向外部的符号链接全部拒绝。
2. `FfmpegProcessRunner` 正常退出、非零退出、超时、取消四类路径均返回正确的 `ProcessRunResult`；stderr 摘要有界。
3. `FfmpegVideoFrameSampler` 按间隔产出帧、遵守 `MaxFrames` 与最大分辨率、去重生效、成功与失败均清理临时目录。
4. ffprobe 可测量输出时长；音画偏移超阈值返回 `composition_av_drift`。
5. DI 可解析全部新实现；未配置时返回 `provider_not_configured` 且服务正常启动。

安全（高风险项，需逐条验证）：

6. 全链路无 shell 调用，无字符串参数拼接。
7. 任何用户可控输入都无法逃出受控根目录，符号链接场景已覆盖。
8. 日志不含完整 stderr、媒体内容或绝对路径中的敏感段。
9. 取消与超时后无孤儿 ffmpeg 进程。

回归：

10. `dotnet test` 全量通过，且不低于现有 63 项；新增测试默认不依赖本机 FFmpeg（真实调用需显式开关）。
11. `phase2-routing-v1` 保持 22 节点/22 边未修改；阶段三 draft 独立。

## 5. 测试矩阵

| 编号 | 场景 | 预期 |
|---|---|---|
| F-T01 | 合法 `asset://` 资产 | 返回根目录内绝对路径 |
| F-T02 | `..` 穿越、绝对路径、盘符、UNC | 解析前拒绝 |
| F-T03 | `-` 开头路径段 | 拒绝，防选项注入 |
| F-T04 | 根目录内符号链接指向外部 | 真实路径校验后拒绝 |
| F-T05 | 非 `asset`/`fixture` scheme | 拒绝 |
| F-T06 | runner 正常/非零/超时/取消 | 四类结果可区分 |
| F-T07 | stderr 超长 | 截断为有界摘要 |
| F-T08 | 超时后进程树 | 无孤儿进程 |
| F-T09 | 采样间隔与 `MaxFrames` | 帧数受限，超出删除 |
| F-T10 | 重复帧 | 按阈值去重 |
| F-T11 | 采样失败 | 临时目录已清理 |
| F-T12 | 音画偏移超阈值 | 返回 `composition_av_drift` |
| F-T13 | 未配置 ffmpeg 路径 | `provider_not_configured`，服务可启动 |

命令：

```powershell
dotnet build MarketIntelligence.Agent.sln --configuration Release -m:1 -p:NuGetAudit=false
dotnet test MarketIntelligence.Agent.sln --configuration Release -m:1 -p:NuGetAudit=false
```

## 6. 约束

- 不修改 `phase2-routing-v1` 与阶段三 draft。
- 不提交音频、视频、图片、模型文件；临时产物留在仓库外临时目录。
- 不引入重量级图像或媒体库；去重用保守的哈希判定即可。
- 不在业务层写死 ffmpeg 路径、模型名或凭据。
- 保留现有 `FakeProcessRunner` 与全部既有测试，不为通过新测试而放宽既有断言。
