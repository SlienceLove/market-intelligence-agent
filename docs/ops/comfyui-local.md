# ComfyUI 本地文生图后端

## 当前状态

- ComfyUI 源码目录：`D:\AI\ComfyUI`
- 运行方式：独立 Windows Python 3.13 虚拟环境，CPU fallback
- ComfyUI 版本：`0.30.0`
- 当前模型：`v1-5-pruned-emaonly.safetensors`，来自 ModelScope 的 Stable Diffusion v1.5 EMA-only checkpoint
- 模型文件大小：`4265146304` bytes
- 模型 SHA-256：`6CE0161689B3853ACAA03779EC93EAFE75A02F4CED659BEE03F50797806FA2FA`
- 当前硬件：AMD 集成显卡；没有 CUDA/ROCm，未启用 DirectML
- 当前验证：ComfyUI `/system_stats` 返回 200；`/prompt`、`/history` 和 `/view` 已完成一次低分辨率真实图片生成 smoke test

模型和输出均在工作区外，不进入 Git 仓库。Stable Diffusion v1.5 只用于验证链路；CPU 模式下图片质量和速度不作为最终生产标准。

## 临时启动

ComfyUI 不做永久启动配置。为让 WSL 中的 Dify 容器访问，同时避免监听所有网卡，当前只监听 WSL 虚拟网卡：

```powershell
$root = 'D:\AI\ComfyUI'
$python = Join-Path $root '.venv\Scripts\python.exe'
& $python (Join-Path $root 'main.py') --cpu --listen 172.30.144.1 --port 8188 --disable-auto-launch --disable-api-nodes
```

实际 WSL 虚拟网卡地址可能变化；启动前用 `Get-NetIPAddress -AddressFamily IPv4` 复核。不要改成 `--listen 0.0.0.0`，ComfyUI 原生 API 没有服务间鉴权。

## Dify 调用边界

Dify 不直接调用 ComfyUI 的无鉴权 `/prompt` 接口，而是调用仓库中的 .NET bridge：

```text
POST /api/image/generate
X-Agent-Api-Key: <runtime-only-key>
Content-Type: application/json

{
  "prompt": "<non-sensitive prompt>",
  "negativePrompt": "<optional negative prompt>",
  "width": 256,
  "height": 256,
  "steps": 4
}
```

bridge 负责固定 checkpoint、校验尺寸/步数、提交 ComfyUI、轮询完成状态并返回受控的图片资产 URL。API Key 只通过运行时环境变量或本机 secret 配置，不写入仓库、Workflow 文档或日志。

推荐运行时配置：

```text
ComfyUi__BaseUrl=http://172.30.144.1:8188
ComfyUi__CheckpointName=v1-5-pruned-emaonly.safetensors
ComfyUi__BridgeApiKey=<runtime-only-key>
```

## 限制与后续

- 当前只有 CPU 生成；Flux/Qwen 等大模型不适合在本机 16GB 内存和 AMD 集显环境直接运行。
- 阶段三的 `image_prompt` 只有在 Dify → .NET bridge → ComfyUI → 图片资产 URL 全链路通过后，才计为图片能力验收通过。
- 真实业务提示词、客户资料、知识库原文和生成图片不写入仓库。
- 图片输出需要单独的保留、访问控制和清理策略；ComfyUI 服务停止后，临时图片 URL 不保证继续可用。
