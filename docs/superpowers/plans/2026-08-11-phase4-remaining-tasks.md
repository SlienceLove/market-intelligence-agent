# 阶段四剩余任务规划

> **日期：** 2026-08-11  
> **阶段窗口：** 2026-08-11 ~ 2026-09-04（剩余 24 天）  
> **前置完成：** M4-05a FFmpeg 运行时已合并到 main（带已知约束）  
> **目标：** 完成 TTS sidecar 接入、真实音画合成验证，为阶段五整体联调做好准备

## 1. 当前状态基线

### 已完成模块（✅）

| 模块 | 状态 | 验收证据 |
|---|---|---|
| 采集（M4-01） | ✅ 完成 | 本地 mock HTTP 验证，受控 allowlist/超时/幂等全通过 |
| ASR（M4-02） | ✅ 完成 | faster-whisper sidecar + API → 队列 → adapter 全链路 smoke |
| OCR（M4-03） | ✅ 完成 | RapidOCR sidecar + 视频抽帧 + 全链路 smoke |
| FFmpeg 运行时（M4-05a） | ✅ 完成 | 140 项测试（136 通过 + 4 跳过），已合并到 main |
| API/Worker（M4-06） | ✅ 完成 | 受理/查询/取消/服务间鉴权/幂等，内存队列验证通过 |

**测试基线：** 140 项测试，136 通过 + 4 跳过（FFmpeg 真实二进制需配置）

### 阻塞点与待完成项（🔧）

| 任务 | 阻塞原因 | 优先级 | 预计耗时 |
|---|---|---|---|
| TTS sidecar 接入（M4-04） | 音色商业授权与人工试听待确认 | **P0** | 2-3 天 |
| 真实音画合成验证 | 需授权音频（依赖 TTS） | **P0** | 1-2 天 |
| 持久化队列部署 | 生产级部署，非功能验证阻塞项 | P1 | 2-3 天 |
| M4-05 架构安全加固 | 已决策延后至阶段五后 | P2（后续） | 7-11 天 |

### 技术债与已知约束

- **M4-05a 安全约束**（已记录于 `docs/ops/m4-05-security-constraints.md`）：
  - TOCTOU 链接替换竞争（需媒体目录独占）
  - 进程树终止不保证（需人工确认）
  - 帧时间戳不准确（变帧率/非零起始 PTS）

- **未实现的阶段四功能**：
  - 字幕烧制
  - 静音填充
  - 裁切策略
  - 目标分辨率限制
  - 转场特效

---

## 2. 核心任务规划

### M4-04B：TTS Sidecar 接入（P0，2-3 天）

**目标：** 基于已完成的 sherpa-onnx 中文 VITS 基准，实现生产可用的 TTS HTTP sidecar。

**前置条件：**
- ✅ sherpa-onnx `zh-ll` 模型已验证优于 AISHELL3
- ✅ 应用层契约已就位（`ISpeechSynthesisService`、输入限制、fake service）
- ⚠️ 音色商业授权待确认（阻塞项）

#### 任务分解

**Step 1：音色授权确认（0.5 天）**
- [ ] 确认 `zh-ll` 模型的许可证和商业使用条款
- [ ] 如 `zh-ll` 不可用，选择备选方案：
  - 选项 A：使用开源许可的轻量 VITS 模型（音质可能降级）
  - 选项 B：接入云 TTS（阿里云/腾讯云，需授权和成本评估）
  - 选项 C：暂用 fake TTS + 固定时长占位音频（仅供流程验证）
- [ ] 记录授权决策和使用约束

**Step 2：TTS Sidecar 实现（1 天）**

参考 `scripts/media/asr_sidecar.py` 和 `scripts/media/ocr_sidecar.py` 的模式：

- [ ] 创建 `scripts/media/tts_sidecar.py`：
  - 仅监听回环地址（`127.0.0.1`）
  - 服务间 API key 鉴权（`X-Service-Key` header）
  - 请求/响应规范化（JSON schema 验证）
  - 文本长度限制（单段 ≤ 500 字符，总计 ≤ 5000 字符）
  - 输出音频格式：WAV 16kHz 16bit mono（便于 FFmpeg 拼接）
  - 超时控制（单段 ≤ 30 秒）
  - 错误分类：`invalid_input`、`text_too_long`、`synthesis_failed`、`timeout`

- [ ] 创建 `scripts/media/requirements-tts-sidecar.txt`
- [ ] 创建 `scripts/media/test_tts_sidecar.py`（基础 contract test）

**Step 3：Infrastructure HTTP Adapter（1 天）**

- [ ] 在 `Infrastructure/Media/` 创建 `HttpSpeechSynthesisService.cs`：
  - 实现 `ISpeechSynthesisService` 接口
  - HTTP 客户端配置（超时、重试、取消）
  - 请求序列化和响应反序列化
  - 错误映射（HTTP 状态码 → 稳定错误码）
  - 音频引用生成（`asset://media/{jobId}/audio-{segmentIndex}.wav`）

- [ ] 创建 `Infrastructure/Media/TtsHttpOptions.cs`（配置绑定）
- [ ] 在 `ServiceCollectionExtensions.cs` 中注册 DI

**Step 4：集成测试（0.5 天）**

- [ ] 创建 `tests/.../HttpSpeechSynthesisServiceTests.cs`：
  - Fake HTTP 响应验证
  - 文本切分验证
  - 段落顺序验证
  - 超时/取消验证
  - 错误分类验证

- [ ] 全链路 smoke test（需启动 sidecar）：
  - API → 队列 → HTTP adapter → sidecar
  - 输入短文本（< 100 字符）
  - 验证输出音频引用存在且可读
  - 验证时长合理（文本长度 / 语速 ≈ 时长）

**验收标准：**
1. TTS sidecar 可独立启动并响应健康检查
2. Infrastructure HTTP adapter 通过离线 contract test
3. 全链路 smoke test 生成可访问的音频文件
4. 音色授权决策已记录于 `docs/ops/media-model-benchmark.md`
5. 测试数从 140 项增至 ~155 项（+15 TTS 相关）

---

### M4-05B：真实音画合成验证（P0，1-2 天）

**目标：** 使用授权音频和视频素材，验证 FFmpeg 合成的端到端流程。

**前置条件：**
- ✅ FFmpeg 运行时已实现（`FfmpegVideoCompositionService`）
- ✅ 视频抽帧已验证（3 秒视频 → 3 帧 JPEG）
- ⚠️ 需授权音频（依赖 M4-04B TTS sidecar）

#### 任务分解

**Step 1：准备测试素材（0.5 天）**

- [ ] 创建或获取授权测试素材：
  - **视频素材**：3-5 秒纯色或简单动画（避免版权问题）
    - 可用 FFmpeg 生成：`ffmpeg -f lavfi -i testsrc=duration=5:size=1280x720:rate=30 test-video.mp4`
  - **音频素材**：通过 M4-04B TTS sidecar 生成
    - 输入文本："这是一段测试音频，用于验证视频合成功能。"
    - 预期时长：~3-5 秒

- [ ] 将素材保存到仓库外临时目录（不提交到 git）
- [ ] 记录素材元数据（时长、分辨率、编码格式）

**Step 2：配置 FFmpeg 环境（0.5 天）**

- [ ] 更新 `appsettings.json` 配置：
  ```json
  {
    "Media": {
      "AssetRoot": "C:\\Temp\\MarketIntelligence\\MediaAssets",
      "Ffmpeg": {
        "ExecutablePath": "C:\\Users\\33206\\...\\ffmpeg.exe"  // 实际 Gyan.FFmpeg.Essentials 路径
      }
    }
  }
  ```

- [ ] 确保 `AssetRoot` 目录存在且权限正确
- [ ] 验证 FFmpeg 可执行文件路径正确

**Step 3：端到端合成测试（1 天）**

- [ ] 创建集成测试或手动测试脚本：
  - 启动 API（`dotnet run --project src/MarketIntelligence.Agent.Api`）
  - 提交合成作业：
    ```json
    {
      "type": "video_composition",
      "inputs": {
        "video": "fixture://test-video.mp4",
        "audio": "asset://media/test-job-001/audio-001.wav"
      },
      "options": {
        "output_format": "mp4",
        "video_codec": "h264",
        "audio_codec": "aac"
      }
    }
    ```
  - 查询作业状态直至完成
  - 验证输出文件存在且可播放

- [ ] 验证项：
  - ✅ 作业状态为 `succeeded`
  - ✅ 输出文件大小 > 0
  - ✅ 输出时长 ≈ 输入视频或音频的较短者（`-shortest` 生效）
  - ✅ 音画同步（人工播放验证，无明显偏移）
  - ✅ 临时文件已清理

**Step 4：ffprobe 时长校验（可选，0.5 天）**

- [ ] 使用 `FfprobeMediaProbe` 测量输出视频：
  - 容器时长
  - 视频流时长
  - 音频流时长
- [ ] 验证音画偏移 < 200ms（默认阈值）
- [ ] 如超阈值，返回 `composition_av_drift` 错误码

**验收标准：**
1. 真实音画合成测试至少通过 1 个完整用例
2. 输出视频可用标准播放器打开且音画同步
3. 测试素材和输出文件保存在仓库外，不提交到 git
4. 合成过程日志无敏感信息泄露
5. 4 项跳过的 FFmpeg smoke test 转为通过（测试数 136 → 140）

---

### M4-07：持久化队列部署（P1，2-3 天，可选）

**目标：** 将内存队列替换为生产级持久化方案，支持重启后状态恢复。

**风险评估：** 非阶段四核心验收条件，可延后至阶段五或独立部署阶段。

#### 选项 A：基于文件的轻量持久化（推荐）

**适用场景：** 单机部署、低并发（< 10 作业/分钟）

- [ ] 在 `Application/Media/` 实现 `FileBasedMediaJobStore`：
  - 作业状态序列化为 JSON，存储到 `{AssetRoot}/jobs/{jobId}.json`
  - 启动时扫描并恢复 `Accepted`/`Running` 状态的作业
  - 完成/失败/取消的作业保留 24 小时后自动清理

- [ ] 配置项：
  ```json
  {
    "Media": {
      "JobStore": {
        "Type": "File",
        "RetentionDays": 1
      }
    }
  }
  ```

**工作量：** 1-2 天

#### 选项 B：基于 SQLite 的本地队列

**适用场景：** 需要查询、统计、并发控制

- [ ] 引入 `Microsoft.Data.Sqlite` NuGet 包
- [ ] 创建 schema（jobs 表：id, type, status, created_at, updated_at, payload）
- [ ] 实现 `SqliteMediaJobStore`

**工作量：** 2-3 天

#### 选项 C：延后至阶段五

**理由：** 当前内存队列已满足功能验证，生产部署可与阶段五整体联调同步进行。

**决策建议：** 选择**选项 C**，将持久化队列作为阶段五的部署准备项，不阻塞阶段四验收。

---

## 3. 时间线与优先级

### 高优先级（阻塞阶段四验收）

| 任务 | 开始日期 | 预计完成 | 负责人 | 依赖 |
|---|---|---|---|---|
| M4-04B Step 1: 音色授权确认 | 2026-08-11 | 2026-08-11 | 产品/法务 | 无 |
| M4-04B Step 2-3: TTS Sidecar + Adapter | 2026-08-12 | 2026-08-13 | 开发 | Step 1 |
| M4-04B Step 4: 集成测试 | 2026-08-13 | 2026-08-13 | 开发 | Step 2-3 |
| M4-05B: 真实音画合成验证 | 2026-08-14 | 2026-08-15 | 开发 | M4-04B |

**里程碑：** 2026-08-15 完成阶段四核心功能验收

### 中优先级（可选增强）

| 任务 | 开始日期 | 预计完成 | 备注 |
|---|---|---|---|
| M4-07: 持久化队列（选项 A） | 2026-08-16 | 2026-08-17 | 可延后至阶段五 |
| 真实视频号采集 smoke test | 待授权 | 待定 | 需平台授权和合规确认 |
| ASR/OCR 真实语料验收 | 待授权 | 待定 | 需授权真人语音和视频 |

### 低优先级（后续阶段）

| 任务 | 计划阶段 | 备注 |
|---|---|---|
| M4-05 架构安全加固 | 阶段五后 | 7-11 天，需 Job Object/文件描述符绑定 |
| 字幕烧制 | 阶段五或独立需求 | 需 ASS/SRT 渲染 |
| 云 TTS/ASR 接入 | 独立需求 | 需商业授权和成本评估 |
| 文生图集成到合成流程 | 独立需求 | 当前已有独立 Workflow |

---

## 4. 风险与缓解

### 风险 1：音色授权不明确（高）

**影响：** TTS sidecar 无法使用 `zh-ll` 模型，阻塞真实音画合成验证。

**缓解措施：**
- **Plan A（推荐）：** 使用开源许可的轻量 VITS 模型（如 `vctk` 英文或 `aishell3` 中文），音质可能降级但不阻塞流程验证
- **Plan B：** 使用 fake TTS + 固定时长占位音频（静音或白噪声），仅验证合成逻辑
- **Plan C：** 接入云 TTS（阿里云/腾讯云），需 1-2 天额外时间配置授权

**决策点：** 2026-08-11 EOD，如授权未明确则启用 Plan A

### 风险 2：音画合成偏移超阈值（中）

**影响：** 合成视频音画不同步，影响口播视频质量。

**缓解措施：**
- 调整 FFmpeg 参数（`-async 1`、`-vsync cfr`）
- 预处理音频（重采样到统一 sample rate）
- 如仍超阈值，放宽阈值到 500ms 并记录为已知限制

### 风险 3：FFmpeg 环境配置问题（低）

**影响：** 真实 smoke test 失败。

**缓解措施：**
- 使用已验证的 `Gyan.FFmpeg.Essentials 8.1.1` 路径
- 提供配置验证脚本（`scripts/verify-ffmpeg.ps1`）
- 文档化环境要求（Windows 10+、用户范围安装）

---

## 5. 验收标准

### 阶段四最终验收条件

1. **功能完整性：**
   - ✅ 视频号采集（本地 mock）
   - ✅ ASR（faster-whisper sidecar）
   - ✅ OCR（RapidOCR sidecar）
   - ✅ TTS（sherpa-onnx sidecar，音色授权已确认）
   - ✅ 视频合成（FFmpeg，真实音画合成至少 1 个用例通过）

2. **测试覆盖：**
   - ✅ 测试数 ≥ 155 项（140 现有 + 15 TTS）
   - ✅ 通过率 ≥ 95%（跳过项需明确标注）
   - ✅ 真实 FFmpeg smoke test 至少 1 项通过

3. **文档完整性：**
   - ✅ TTS 音色授权决策已记录
   - ✅ 真实合成测试结果已记录
   - ✅ 已知限制和约束已更新
   - ✅ 运维手册已补齐（sidecar 启动、配置验证）

4. **回归基线：**
   - ✅ `phase2-routing-v1` 保持 22 节点/22 边
   - ✅ 阶段三 draft 未被修改
   - ✅ .NET 8 基座健康检查通过

5. **安全与合规：**
   - ✅ 无真实凭据、媒体文件或敏感内容提交到 git
   - ✅ 日志无完整音视频内容或文本
   - ✅ 授权素材使用已记录并符合条款

### 不作为验收条件的事项

- 持久化队列（可延后至阶段五）
- 云 TTS/ASR 接入（需独立授权）
- 真实视频号采集（需平台授权）
- 架构安全加固（已决策延后）
- 字幕烧制/转场特效（非核心功能）

---

## 6. 阶段五准备

### 交接给阶段五的资产

1. **代码基座：**
   - .NET 8 五项目解决方案（140+ 测试）
   - API/Worker 作业协调器
   - ASR/OCR/TTS/FFmpeg 全链路

2. **运维文档：**
   - Sidecar 启动手册（ASR/OCR/TTS）
   - FFmpeg 配置验证脚本
   - 安全约束和已知限制
   - 模型基准和授权决策

3. **测试素材：**
   - 脱敏 fixture（仓库外临时目录）
   - 合成测试结果和元数据

4. **待接入功能：**
   - 持久化队列部署
   - 招投标采集模块
   - 定时推送功能
   - 全流程联调

### 阶段五关键任务（预览）

1. **招投标采集**（09-07 ~ 09-11，5 天）
   - 重点区域/行业平台调研
   - 采集规则梳理
   - 模块开发与测试

2. **定时推送**（09-12 ~ 09-14，3 天）
   - 关键词订阅
   - 群/邮箱推送
   - 推送模板和频率控制

3. **全流程联调**（09-15 ~ 09-17，3 天）
   - 五层任务全链路测试
   - 性能基准测试
   - 故障恢复验证

4. **汇报演示准备**（09-18，1 天）
   - 演示脚本和素材
   - 功能演示录屏
   - 技术文档整理

---

## 7. 后续追踪

- **进度记录：** 每日更新 `docs/PROGRESS.md` 的"更新记录"表
- **阻塞上报：** 音色授权、真实素材、环境问题需在 24 小时内上报
- **周会复核：** 每周一复核进度、风险和决策点
- **文档维护：** 所有决策、测试结果和已知问题记录到对应 ops 文档

**负责人：** 开发团队  
**最后更新：** 2026-08-11
