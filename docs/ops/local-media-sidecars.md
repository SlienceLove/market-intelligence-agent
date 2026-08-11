# 本地媒体 Sidecar 运行手册

本手册说明如何把已验证的本地模型接到现有媒体 HTTP adapter。Sidecar 只应在受控内网或同机回环地址运行；默认关闭，且不自动下载模型或读取仓库内媒体。

## ASR：faster-whisper

### 安装

在仓库外创建 Python 环境，模型和缓存也保存在仓库外：

```powershell
py -3 -m venv <venv-path>
<venv-path>\Scripts\python.exe -m pip install -r scripts\media\requirements-asr-sidecar.txt
New-Item -ItemType Directory -Force <media-root>
```

`<media-root>` 是 sidecar 唯一可读取的媒体目录，并被映射为 `temp://media/`。例如 `<media-root>\clip.wav` 对应 `temp://media/clip.wav`。请求不能使用 `file://`、HTTP URL、绝对路径、查询参数或目录穿越。

### 启动

```powershell
<venv-path>\Scripts\python.exe scripts\media\asr_sidecar.py `
  --allowed-root <media-root> `
  --host 127.0.0.1 `
  --port 8091 `
  --model base `
  --device cpu `
  --compute-type int8 `
  --cpu-threads 8 `
  --api-key <service-key>
```

服务提供 `GET /health` 和 `POST /v1/transcriptions`。首次转写会加载或下载模型；正常情况下只记录作业标识、状态和片段数量，不记录音频路径、文本或密钥。

### 应用配置

仅在 sidecar 已启动、共享媒体根目录已部署且服务间密钥由 secret store 提供时设置下列环境变量：

```powershell
$env:Media__Asr__Enabled = "true"
$env:Media__Asr__Endpoint = "http://127.0.0.1:8091/v1/transcriptions"
$env:Media__Asr__ApiKeyHeaderName = "X-Agent-Api-Key"
$env:Media__Asr__ApiKey = "<service-key>"
$env:Media__Asr__Model = "faster-whisper-base"
```

保持 `Media__Asr__Enabled=false` 是未部署或故障时的安全默认值。现有 `HttpTranscriptionService` 会继续执行请求校验、响应大小限制、超时、有限重试和时间轴规范化。

### 验证与限制

```powershell
<venv-path>\Scripts\python.exe scripts\media\test_asr_sidecar.py
Invoke-RestMethod http://127.0.0.1:8091/health
```

sidecar 仅接受 `audio/*` 或 `video/*`、100 MB 以内、声明时长不超过 20 分钟的资源；默认单并发，忙碌时返回 `429`。它不是对象存储或下载器，采集、落盘和临时文件清理由上游受控媒体管道负责。

## OCR：RapidOCR

在独立 Python 环境安装 `scripts\media\requirements-ocr-sidecar.txt`，然后复用 ASR 的 `<media-root>`：

```powershell
<venv-path>\Scripts\python.exe -m pip install -r scripts\media\requirements-ocr-sidecar.txt
<venv-path>\Scripts\python.exe scripts\media\ocr_sidecar.py `
  --allowed-root <media-root> `
  --host 127.0.0.1 `
  --port 8092 `
  --api-key <service-key>
```

服务提供 `GET /health` 和 `POST /v1/ocr`，当前只接受 `image/*`，将单张图片映射为带时间戳、坐标框、语言和置信度的 `frames`。视频帧抽取不在 sidecar 内完成，必须由上游受控采样层提供图片资源。

应用配置使用 `Media:Ocr`：

```powershell
$env:Media__Ocr__Enabled = "true"
$env:Media__Ocr__Endpoint = "http://127.0.0.1:8092/v1/ocr"
$env:Media__Ocr__ApiKeyHeaderName = "X-Agent-Api-Key"
$env:Media__Ocr__ApiKey = "<service-key>"
```

验证：

```powershell
<venv-path>\Scripts\python.exe scripts\media\test_ocr_sidecar.py
Invoke-RestMethod http://127.0.0.1:8092/health
```

## TTS

TTS sidecar 默认使用许可安全的 `placeholder` 后端，只输出显式标记为占位的 WAV 音频。待选定模型的商用音色授权和人工质量验收完成后，再切换到 `sherpa` 后端；不要以 ASR 回读 CER 代替授权或试听。

启动占位后端：

```powershell
<venv-path>\Scripts\python.exe scripts\media\tts_sidecar.py `
  --allowed-root <media-root> `
  --host 127.0.0.1 `
  --port 8093 `
  --backend placeholder `
  --api-key <service-key>
```

服务提供 `GET /health` 和 `POST /v1/speech-synthesis`。请求中的 `outputUri` 必须映射到 `temp://media/` 下的受控路径，响应会返回每段的 `durationSeconds`、`sampleRate`、`bytes`、`backend` 和 `index`，其中占位后端的 `backend` 固定为 `placeholder`。
## 视频帧采样

视频帧抽取由上游受控采样层负责，OCR sidecar 只接受 `image/*`。应用层通过 `IVideoFrameSampler` 生成受控的 FFmpeg 请求，限制采样间隔、最大帧数、最大时长和超时，并拒绝非 `asset://`/`fixture://` 输入及路径穿越。

Windows 开发机可使用用户范围安装，生产环境应固定版本并记录许可证：

```powershell
winget install --id Gyan.FFmpeg.Essentials --exact --scope user
ffmpeg -version
```

抽取出的图片必须写入受控临时根目录，再以 `temp://media/<relative-file>` 传给 OCR；不要将 FFmpeg 输出目录或本地绝对路径直接交给 sidecar。真实抽帧到 OCR smoke 已使用本地合成视频验证，生成 3 张 JPEG，OCR 返回 3 个文本框，最低置信度 `0.7991`。
