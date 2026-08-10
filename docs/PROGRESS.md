# 智能体项目开发进度跟踪

> 本文档为固定的进度记录文件,不归档、不重写历史,只在任务状态变化时原地更新。
> 依据:《智能体项目开发流程计划》(2026-08-03 启动,预计 2026-09-18 完成整体联调,共 7 周)。
> 详细实施计划见 `docs/superpowers/plans/` 下对应阶段文件;本文件只记录状态,不重复计划细节。

**最后更新:2026-08-10**

## 总体进度

| 阶段 | 时间范围 | 状态 | 计划文档 |
|---|---|---|---|
| 阶段零:.NET 扩展服务基座 | 2026-07-31(先行完成) | ✅ 已完成 | `docs/superpowers/plans/2026-07-31-dotnet-agent-foundation.md` |
| 阶段一:基础环境搭建 | 08-03 ~ 08-07 | ✅ 已完成 | `docs/superpowers/plans/2026-08-03-phase1-infrastructure-setup.md` |
| 阶段二:工作流编排与知识库运营 | 08-10 ~ 08-14 | 🔄 进行中 | `docs/superpowers/plans/2026-08-05-phase2-workflow-and-knowledge-ops.md` |
| 阶段三:内容生成能力接入 | 08-17 ~ 08-21 | ✅ 已完成 | `docs/superpowers/plans/2026-08-06-phase3-content-generation.md` |
| 阶段四:多模态自研模块开发 | 08-24 ~ 09-04 | 🔧 进行中 | `docs/superpowers/plans/2026-08-07-phase4-multimodal-modules.md` |
| 阶段五:招投标采集与整体联调 | 09-07 ~ 09-18 | ⬜ 未开始 | 待创建 |

状态图例:✅ 已完成 / 🔄 进行中 / ⏳ 已排期未开始 / ⬜ 未排期 / ⏭️ 暂缓且不阻塞后续阶段 / ⚠️ 阻塞或延期

---

## 阶段零:.NET 扩展服务基座(先行完成)

- [x] .NET 8 Rider 解决方案与五项目依赖图搭建完成
- [x] API 暴露 `GET /health`,内存态契约测试通过
- [x] 后台 Worker 主机、安全配置默认值、`.gitignore`、README 就位
- [x] `dotnet test` 全量通过,代码已推送至 `main` 分支

**说明:** 此阶段不在原始《开发流程计划》五阶段范围内,是提前完成的代码基座(.NET 扩展服务骨架),为后续阶段的采集、ASR/OCR、招投标等自研模块提供落地位置。

---

## 阶段一:基础环境搭建(08-03 ~ 08-07)

**核心目标:** Dify 环境可用,知识库完成导入,联网搜索可用;IMA 笔记同步暂缓,不作为阶段一完成和阶段二启动的前置条件。

| 任务 | 目标日期 | 状态 | 备注 |
|---|---|---|---|
| Dify 私有化部署(服务器、环境、账号权限) | 08-03 | ✅ 已完成 | Ubuntu 24.04、Docker Engine 和 Compose 已安装;Dify Compose 已启动并通过 HTTP 验证;管理员初始化和 `market-intelligence` 工作空间已完成 |
| Ubuntu 24.04 + Docker Engine/Compose 安装与验证 | 08-04 | ✅ 已完成 | WSL2 内 Docker Engine 29.7.1、Compose v5.3.1;普通用户、Ubuntu 容器和 Compose 流程均已验证 |
| 已分类资料按主题建知识库并导入 | 08-04 | ✅ 已完成 | 已创建 `行业与市场研究`、`产品与解决方案` 两个知识库;市场部 5 份正式资料已导入 `产品与解决方案`,索引完成且逐一召回测试成功;临时测试文档已删除 |
| 未分类资料导入"待分类"知识库 | 08-04 | ✅ 已完成 | 已创建空的 `待分类` 知识库,当前未上传资料 |
| 联网搜索插件接入与调试 | 08-05 | ✅ 已完成 | Tavily 插件已安装并配置;通过 Workflow 冒烟测试成功返回中国工业自动化市场趋势结果;优化后 LLM 耗时约 5.8 秒、输出已精简 |
| IMA 笔记同步至 Dify 高优先级知识库流程打通 | 08-06~08-07 | ⏭️ 暂缓 | 按当前安排先跳过,不阻塞阶段二;后续恢复时直接导入 `.md`、Word `.doc/.docx` 或 `.pdf` 文件;阶段二验证沿用已导入的 5 份市场部正式资料 |

**阻塞/风险:** 当前无代码构建阻塞;阶段二路由验证版已发布;5 份 P1 正式资料、空 P3 降级和 Tavily 分支均已验证;`行业与市场研究` 当前为 0 文件,因此 P2 正向命中只能待真实资料进入后补测;阶段二发布验收时曾因 LLM channel 不可用采用证据直出,2026-08-06 阶段三已用可用文本模型完成独立 Workflow smoke test;文生图已通过独立 API2img Workflow 验证;IMA 笔记联通暂缓且不阻塞后续阶段
**网络问题记录:** Docker Hub 直连出现 443 超时/connection refused,已通过 Docker 镜像加速恢复;复用步骤见 [`docs/ops/network-troubleshooting.md`](ops/network-troubleshooting.md)。

---

## 阶段二:工作流编排与知识库运营(08-10 ~ 08-14)

**核心目标:** 检索路由逻辑跑通,知识库运营机制建立。

| 任务 | 目标日期 | 状态 | 备注 |
|---|---|---|---|
| 检索路由逻辑搭建(先查高优先级库,再查主题库/待分类库) | 08-10~08-11 | ✅ 已完成 | 已发布 `phase2-routing-v1`;P1 命中后短路,P1/P2/P3 全部未命中后进入 Tavily;P2 正向命中待真实资料进入主题库后补测 |
| 联网搜索与知识库检索的条件分支判断 | 08-12 | ✅ 已完成 | 已验证明确/强制联网直达 Tavily,一般问题仅在知识库未命中后联网;当前终端使用带来源证据直出,不依赖暂不可用的 LLM channel |
| 待分类资料整理迁移机制建立(责任人、节奏明确) | 08-13 | 🔧 进行中 | 已完成角色/节奏、现场路由复核、版本回滚闭环和 R9 故障路径验证;R2 仍待真实 P2 资料补测,本阶段不以 IMA 实际联通或新增笔记迁移作为验收条件 |
| 知识库收录规则第一轮校正 | 08-14 | 🔧 进行中 | 已建立首版收录规则和迁移台账模板,覆盖 R1、R2-C、R3-R9 已验证样本;待真实 P2 资料补测后完成最终校正 |

---

## 阶段三:内容生成能力接入(08-17 ~ 08-21)

**核心目标:** 文档/PPT/文章/话术/图片生成能力可用。

| 任务 | 目标日期 | 状态 | 备注 |
|---|---|---|---|
| 文档生成能力接入 | 08-17 | ✅ 已完成 | 独立 `phase3-content-generation` draft 已通过 `document` 结构化文本 smoke test;前置类型校验、P1 检索和证据不足分支已验证;`.docx` 文件导出未接入 |
| PPT 大纲与内容生成能力接入 | 08-18 | ✅ 已完成 | `ppt` 已通过逐页大纲/要点结构验证;`.pptx` 文件导出未接入 |
| 公众号文章生成能力接入 | 08-19 | ✅ 已完成 | `wechat_article` 已通过标题、导语、分段正文和行动引导结构验证 |
| 视频号脚本、销售话术生成能力接入 | 08-20 | ✅ 已完成 | `video_script`、`sales_talk` 文本契约均已通过;ASR/TTS/视频合成仍属于阶段四 |
| 文生图能力接入 | 08-21 | ✅ 已完成 | 独立未发布的 `phase3-image-generation-api2img` Dify Workflow 已通过验证；`size=auto`、JPEG 50% 压缩后返回可访问图像 URL，浏览器加载成功且尺寸为 1024x1024；持久 Secret 仅配置在 Dify 中 |

---

## 阶段四:多模态自研模块开发(08-24 ~ 09-04)

**核心目标:** 视频号采集、ASR、OCR、口播视频模块开发完成。

**实施计划:** [`docs/superpowers/plans/2026-08-07-phase4-multimodal-modules.md`](superpowers/plans/2026-08-07-phase4-multimodal-modules.md)。本阶段沿用现有 .NET 8 基座,按采集 → ASR → OCR → TTS → FFmpeg 合成顺序推进;provider、权限、FFmpeg 环境和真实平台 smoke test 需单独确认。

> ⚠️ 文档标注为全周期不确定性最高阶段:视频号反爬限制、ASR/OCR 准确率、音画同步调试均可能超预期,排期已含缓冲。

| 任务 | 目标日期 | 状态 | 备注 |
|---|---|---|---|
| 视频号链接采集模块开发 | 08-24~08-26 | ✅ 已完成 | 已完成 allowlist、端口/重定向/大小/超时/媒体类型限制、受控 HTTP adapter、幂等和稳定错误码；仅本地 mock HTTP 验证,未访问真实平台 |
| ASR 语音转文字模块开发与对接 | 08-27~08-28 | ✅ 已完成（本地 sidecar smoke） | 已完成 provider-neutral HTTP adapter、请求/响应规范化、有限重试、超时/取消和离线 contract test；`faster-whisper/base` CPU `int8` 已通过受限本地 sidecar、API 与队列全链路 smoke，真实部署和授权真人语料验收仍是后续项，见 [`media-model-benchmark.md`](ops/media-model-benchmark.md) 与 [`local-media-sidecars.md`](ops/local-media-sidecars.md) |
| OCR 截图文字识别模块开发与对接 | 08-31 | ✅ 已完成（本地 sidecar smoke） | 已完成帧结果模型、排序/去重/置信度裁剪、fake service 和清理边界；RapidOCR / PaddleOCR ONNX 单图片 sidecar 已通过 API 与队列全链路 smoke；视频帧采样边界已定义，授权视频帧验收仍是后续项，见 [`media-model-benchmark.md`](ops/media-model-benchmark.md) 与 [`local-media-sidecars.md`](ops/local-media-sidecars.md) |
| TTS 语音合成服务搭建 | 09-01~09-02 | 🔧 进行中（本地基准已完成） | 已完成声音/语言 allowlist、文本切分、长度/时长限制和 fake service；sherpa-onnx 中文 VITS 已跑通，当前候选仍待音色商业授权、人工试听和 sidecar 接入，见 [`media-model-benchmark.md`](ops/media-model-benchmark.md) |
| 视频合成服务开发与音画同步调试 | 09-03~09-04 | 🔧 进行中 | 已完成受控 FFmpeg 参数构建、fake process runner、取消/失败边界和资产引用；FFmpeg 已在用户范围安装并完成抽帧 smoke，真实音画合成仍待授权音频验证 |
| API/Worker 作业入口与 Dify 调用边界 | 08-10 | ✅ 已完成（本地） | 已提供受理/查询/取消、服务间 API key、幂等和有界内存队列；生产持久化队列/状态恢复仍需单独部署 |

---

## 阶段五:招投标采集与整体联调(09-07 ~ 09-18)

**核心目标:** 招投标采集与定时推送上线,全流程联调完成。

| 任务 | 目标日期 | 状态 | 备注 |
|---|---|---|---|
| 重点区域/行业招投标平台调研与采集规则梳理 | 09-07~09-08 | ⬜ 未排期 | |
| 招投标资料采集模块开发 | 09-09~09-11 | ⬜ 未排期 | |
| 定时关键词采集与推送功能接入(群/邮箱) | 09-14 | ⬜ 未排期 | |
| 全流程联调测试(五层任务全链路) | 09-15~09-17 | ⬜ 未排期 | |
| 汇报演示准备 | 09-18 | ⬜ 未排期 | |

---

## 更新记录

| 日期 | 更新内容 |
|---|---|
| 2026-07-31 | 创建本文档;记录阶段零(.NET 基座)已完成;阶段一实施计划已生成,状态置为"未开始" |
| 2026-08-03 | 阶段一进行中;补充 Dify 部署记录模板和 IMA 同步 SOP;外部 Dify 部署与验收待具备 Linux/Docker 环境后执行;.NET 基座回归测试通过 |
| 2026-08-03 | 本机部署前置环境推进:管理员已启用 WSL 和 Virtual Machine Platform,安装并验证微软官方 WSL2 kernel MSI;当前等待 Windows 重启,重启后继续 Ubuntu 24.04 与清华 Docker CE 源配置 |
| 2026-08-04 | Ubuntu 24.04 (WSL2) 中完成 Docker Engine 29.7.1、Docker Compose v5.3.1 安装;配置 Docker 镜像加速并验证普通用户 Docker 权限、Ubuntu 容器和 Compose 流程;Dify 固定 Commit `5456d4d` 已部署,本地 HTTP 和 API 健康检查通过,待浏览器完成初始化;网络故障复用记录见 `docs/ops/network-troubleshooting.md` |
| 2026-08-04 | 用户已完成 Dify 浏览器管理员初始化并创建 `market-intelligence` 工作空间;复验 Compose 全部服务运行、API/PostgreSQL/Redis/Sandbox 健康、根路径认证跳转和 API 日志正常;进入知识库导入与联网搜索配置 |
| 2026-08-04 | 已创建 `行业与市场研究`、`产品与解决方案` 两个主题知识库;在无 Embedding/Rerank 模型的条件下选择经济索引,保留默认分段设置,关闭 Score Threshold,Top K 使用 3;通过召回测试命中 `industry-test.md` 的 `分段-04`,准确返回关键词“市场智能系统试运行”;知识库基础上传、解析、分段、索引和召回流程验证通过,下一步删除临时测试文档并导入腾讯 IMA 正式资料 |
| 2026-08-04 | 删除临时测试文档;将市场部 5 份正式资料导入 `产品与解决方案` 知识库,5 份文件均完成索引并逐一通过召回测试 |
| 2026-08-04 | Tavily 插件已安装并完成 API Key 配置;在 `web-search-smoke-test` Workflow 中执行 Tavily Search,成功返回中国工业自动化市场趋势结果,联网搜索链路验证通过 |
| 2026-08-04 | 优化联网搜索 Workflow: Tavily 结果数量和 LLM 输出长度收敛;测试输出精简至约 4.7K tokens,LLM 耗时约 5.8 秒,满足当前响应要求 |
| 2026-08-05 | 已生成阶段二工作流编排与知识库运营实施计划,明确 P1/P2/P3 dataset 隔离决策、检索短路、联网分支、IMA 迁移机制、收录规则与回滚验收;阶段二改为已排期未开始 |
| 2026-08-06 | 按当前安排暂缓 IMA 笔记联通,不阻塞阶段二;后续直接导入 `.md`、Word `.doc/.docx` 或 `.pdf` 文件;阶段二路由、召回和导入验证统一沿用已导入的 5 份市场部正式资料 |
| 2026-08-06 | 开始阶段二任务规划:先完成启动基线盘点,再依次推进优先级登记、Workflow 草稿、路由/联网验证和运营验收;IMA 联通不纳入依赖链 |
| 2026-08-06 | P2-00 已开始执行;完成仓库侧基线记录和非敏感测试模板;WSL/Dify 可启动但 API 仍处于启动阶段,两个主题库、5 份文件当前索引状态及 P3 文档数待现场复核 |
| 2026-08-06 | P2-00 现场复核确认 Dify 1.16.1 控制台 API 可达,知识库接口返回 `401 Unauthorized` 需要登录认证;未读取或保存凭据,因此暂不进入 P2-01 |
| 2026-08-06 | 用户确认 P2-00 基线:两个主题库存在,5 份文件均为 `Completed`,当前没有 `待分类` 知识库;P2-00 完成并进入 P2-01 优先级登记 |
| 2026-08-06 | 用户确认将 `产品与解决方案` 中 5 份正式资料提升为 P1;源码核查确认 Dify 1.16.1 Knowledge Retrieval 支持元数据过滤;P2-01 优先级登记完成,进入 P2-02 Workflow 草稿搭建 |
| 2026-08-06 | P2-02 已完成仓库侧 Workflow 操作清单;实际 Dify UI 仍需具备登录、dataset 元数据管理和 Workflow 编辑权限后执行,不绕过认证 |
| 2026-08-06 | 已通过浏览器调试入口继续执行 P2-02/P2-03:确认 5 份文件元数据为 `priority=P1/topic=产品与解决方案/status=approved`,创建空 `待分类` 库;保存 22 节点/22 边 Workflow,验证空问题、P1 命中短路、P1/P2/P3 全部未命中降级、明确/强制联网直达和 Tavily 失败提示;发布 `phase2-routing-v1`;因 LLM channel 无可用通道暂采用证据直出 |
| 2026-08-06 | 进入 P2-04 现场验收:发现 `127.0.0.1` 页面与 `localhost` Socket.IO 主机不一致导致 Studio 卡在“同步数据中”;临时统一浏览器会话主机后恢复交互;复核已发布 22 节点/22 边版本,空问题无运行请求,直接联网和全库未命中降级路径均通过;版本历史可见,恢复/重新发布闭环待继续验证 |
| 2026-08-06 | 完成 P2-04 回滚闭环:恢复已验证的 `phase2-routing-v1` 发布版本并重新发布同名版本;发布后 `draft/run` 返回 `200 text/event-stream`,联网 smoke run 以 `succeeded` 完成,实际路径为 Start → Normalize Question → Web Intent IF → Tavily Search → Normalize Web → END - WEB;R2 正向 P2 命中继续暂缓,R9 受控故障注入随后完成 |
| 2026-08-06 | 完成 R9 受控规范化故障验证:仅在 draft 临时注入非法 JSON,`Normalize P1` 返回 `hit=false/quality_note=empty/failure_reason=empty_result`,随后按 P1 → P2 → P3 → Tavily 降级并成功;已恢复 draft,hash、代码、22 节点/22 边与发布基线一致;R2 因 P2 知识库为空继续待真实资料补测 |
| 2026-08-06 | 完成 R1 P1 命中回归:现有 P1 文件关键词触发 `Retrieve P1` 返回 3 条结果,`Normalize P1` 返回 `hit=true/count=3/quality_note=ok`;随后短路至 `End - P1`,未执行 P2、P3 或 Tavily,Workflow 以 `succeeded` 完成 |
| 2026-08-06 | 补齐阶段二运营文档首版:`docs/ops/knowledge-intake-rules.md` 和 `docs/ops/knowledge-migration-ledger.md`;仅记录非敏感规则、字段和当前 5 份 P1/空 P2/空 P3 状态,最终规则校正仍待真实 P2 资料 |
| 2026-08-06 | 完成受控 P2 分支复用验证:仅在 draft 临时让 P1 过滤不匹配并让 P2 读取现有 5 份 P1 文件;`Normalize P2` 返回 `hit=true/count=3/quality_note=ok`,路径短路至 `End - P2`;已恢复 draft 原 hash 和配置,发布版本未改变;真实 P2 数据集命中仍待补测 |
| 2026-08-06 | 进入阶段三内容生成能力接入:已读取原始开发流程与建设方案,创建 `docs/superpowers/plans/2026-08-06-phase3-content-generation.md`;阶段三先检查 LLM channel、模型调用权限、文生图插件和文件输出能力,生成 Workflow 与已发布阶段二 Workflow 隔离;IMA 联通、真实 P2 命中和新增测试文件继续不作为本阶段前置条件 |
| 2026-08-06 | 完成阶段三 G0 现场预检:独立 Dify 标签页确认可进入模型供应商和工具插件;`Gptpro` 显示 52 个模型,默认系统推理模型为 `gpt-5.6-luna`,文本生成具备真实 smoke test 前置条件;工具插件当前看到 Tavily,未发现文生图工具;下一步创建独立阶段三文本生成 Workflow,不修改 `phase2-routing-v1` |
| 2026-08-06 | 创建独立未发布 `phase3-content-generation` draft,使用 `gpt-5.6-sol` 完成 `document`、`ppt`、`wechat_article`、`video_script`、`sales_talk` 五类结构化文本验证;测试沿用既有 5 份 P1 正式资料,记录仅保留测试编号/类型/路径/状态,未写入问题原文或生成全文 |
| 2026-08-06 | 阶段三 draft 增加请求/内容类型前置校验:有效五类文本进入 P1 检索和 LLM,空白请求、未知类型与 `image_prompt` 在检索前失败;无证据用例返回资料不足且不编造事实;文生图和 `.docx`/`.pptx` 文件导出仍未接入 |
| 2026-08-06 | 阶段三回归核对完成:阶段二 `phase2-routing-v1` 发布版和 draft 均保持 22 节点/22 边;阶段三保持独立未发布 draft;`gpt-5.6-luna` 曾返回 `503 model_not_found`,当前可用文本模型为 `gpt-5.6-sol` |
| 2026-08-07 | 进入下一阶段规划:创建阶段四多模态自研模块计划,固定采集 → ASR → OCR → TTS → FFmpeg 合成顺序、应用层接口边界、授权/安全约束和测试矩阵;阶段二与阶段三保持只读基线,IMA 和 `.docx/.pptx` 导出继续作为后续项 |
| 2026-08-07 | 按顺序继续核查文生图:用户确认 `Gptpro` 没有 GPT Image 2 图像生成能力;现场确认 Dify `Gemini 0.9.3` 供应商仅显示 LLM/文本嵌入且需要 API Key,因此下一步为配置授权后的 Gemini/Imagen 能力验证,尚未将文本结果计为图片生成成功 |
| 2026-08-07 | 完成独立未发布 `phase3-image-generation-api2img` 草稿验证，保存 `prompt` 输入、图像 HTTP 请求和 `image_response` 输出的三节点链路；初始 PNG Base64 超过 Dify 1 MB 文本上限，改为 `size=auto`、JPEG 50% 压缩后运行 `succeeded`，返回 URL 并实际加载成功，图像尺寸为 1024x1024；阶段二 `phase2-routing-v1` 未修改。 |
| 2026-08-07 | 持久化 Dify 图像工作流 Secret，仅保存于 Dify 环境变量中，未写入仓库或日志；阶段四 M4-00 开始执行，新增媒体领域契约、五类 provider-independent 接口、未配置 provider 的安全失败适配器和 Media 配置边界；本机未安装 FFmpeg，保留后续可替换 runner。 |
| 2026-08-07 | 阶段四 M4-01 开始执行：新增授权来源 URL 策略，拒绝 `file://`、IP/本机地址、用户信息和未 allowlist 域名；当前只完成本地校验与 contract test，不发起真实采集请求。 |
| 2026-08-07 | 阶段四 M4-01 补充 fake collector：模拟授权来源成功、幂等重试和取消，不发起网络请求；非法来源在采集前失败且不返回资产。 |
| 2026-08-07 | 阶段四 M4-02 开始执行：新增音频/视频输入限制、时间轴排序与重叠修正、置信度裁剪、空转写和文本长度限制；fake ASR 测试通过，未接入真实 provider。 |
| 2026-08-07 | ASR provider 方案登记：默认优先本地 `faster-whisper` 以控制隐私和长期成本；阿里云/腾讯云/火山引擎 ASR 作为国内云备选；OpenAI transcription 仅用于独立质量对照，不在业务层写死模型或凭据。 |
| 2026-08-10 | 按推荐顺序并行推进 M4-00/M4-01/M4-02：补齐 Accepted/Running 作业生命周期、失败分类与输入边界；完成受控 HTTP 采集 adapter、ASR HTTP adapter 及离线 contract tests；HTTP provider 默认关闭，配置缺失时返回 `provider_not_configured`。 |
| 2026-08-10 | 完成 OCR/TTS/FFmpeg 的应用层契约、fake 边界和安全测试；新增内存有界作业协调器与 `/api/media/jobs` 受理/查询/取消接口，启用服务间 API key 鉴权；fake 全链路与全量 `dotnet test` 通过 55 项。 |
| 2026-08-10 | 按 ASR → OCR → TTS 顺序完成无云授权的本地真实模型验证：`faster-whisper/base` CPU `int8`、RapidOCR / PaddleOCR ONNX 与 sherpa-onnx 中文 VITS 均跑通；ASR/OCR 可进入本地 PoC，TTS 的 `zh-ll` 候选优于轻量 AISHELL3 smoke 模型但仍待音色授权和人工质量验收。详细指标、成本与许可证边界见 [`docs/ops/media-model-benchmark.md`](ops/media-model-benchmark.md)。 |
| 2026-08-10 | 完成本地 faster-whisper ASR sidecar：仅监听回环地址，只读取映射到 `temp://media/` 的受控根目录，支持服务间 API key、请求/输入上限和单并发；路径隔离单测及 API → 队列 → HTTP adapter → sidecar 的真实 smoke 均通过。运行手册见 [`docs/ops/local-media-sidecars.md`](ops/local-media-sidecars.md)。 |
| 2026-08-10 | 完成 RapidOCR OCR sidecar：仅接受受控 `temp://media/` 图片资源，返回时间戳、坐标框、语言和置信度；路径隔离、服务间鉴权、真实截图识别及 API → 队列 → HTTP adapter → sidecar 全链路 smoke 通过。当前不在 sidecar 内抽取视频帧。 |
| 2026-08-10 | 完成视频帧采样边界：新增 `IVideoFrameSampler`、采样选项和受控 FFmpeg 参数构造；限制采样间隔、最大帧数、最大时长和超时，拒绝非视频、非 `asset://`/`fixture://` 输入及路径穿越；4 项契约测试和全量 63 项 .NET 测试通过。当前机器未安装 FFmpeg，真实视频抽取 smoke 待环境具备后执行。 |
| 2026-08-10 | 通过用户范围 `Gyan.FFmpeg.Essentials 8.1.1` 完成真实抽帧验证：本地合成 3 秒视频抽取 3 张 JPEG，并送入 RapidOCR sidecar；OCR 返回 3 个文本框，最低置信度 `0.7991`。模型、视频和图片均保存在仓库外临时目录。 |
| 2026-08-10 | M4-05a FFmpeg 运行时落地到分支 `feat/m4-05-ffmpeg-runtime`，共 7 个提交，每个提交单独构建/测试验证。新增 `MediaAssetPathResolver`、`FfmpegProcessRunner`、`FfprobeMediaProbe`、`FfmpegVideoFrameSampler`、`FfmpegVideoCompositionService` 及 DI 注册。测试自 63 项增至 135 项：未配置 FFmpeg 时 131 通过 + 4 跳过，配置真实 FFmpeg 时 135 全通过。 |
| 2026-08-10 | 测试过程中修复三个由失败测试定位的真实缺陷：① `ResolveFinalPath` 仅解析末端节点，操作系统透明穿越目录链接导致根目录包含性检查形同虚设，改为自顶向下逐分量解析；② 帧去重漏掉第一对重复帧（长度不同即丢弃前一帧哈希）；③ 丢弃重复帧后时间戳整体前移。 |
| 2026-08-10 | 修正冒烟测试门控缺陷：原早退（early return）写法在未配置 FFmpeg 时报告为“通过”，导致空转与真实验证无法区分——此前 131 通过的结果中 4 项冒烟实际 9 毫秒内未调用 FFmpeg。改用 `RequiresRealFfmpegFact` 在发现阶段设置 `Skip`，惰性状态现可见。 |
| 2026-08-10 | `/codex:adversarial-review` 结论为 **needs-attention**（4 项：2 高 2 中），尚未验收。未修项：① 包含性对 TOCTOU 链接替换竞争仍可绕过，且分量解析异常时退化为词法检查（失败开放）；② `KillProcessTree` 丢弃 kill/reap 结果，取消后不保证子孙进程已终止；③ 帧时间戳取自 JPEG muxer 序号而非源 PTS，变帧率/非零起始 PTS 素材会算错；④ 符号链接包含性测试在无建链权限机器上静默通过。①② 的彻底修复需 OS 级进程/文件句柄约束（Windows Job Object、no-follow 描述符），属架构决策，待确认后执行。 |

---

## 使用说明(如何维护本文档)

- **每完成一个任务:** 把对应表格行的复选框改为 `[x]`(如有)、状态列改为 ✅,并在"更新记录"追加一行,写明日期和变化。
- **遇到风险或延期:** 状态改为 ⚠️,在备注列写明原因和调整后的日期,不要直接删除原计划行。
- **进入下一阶段:** 如果该阶段还没有对应的实施计划文档,先用 writing-plans 技能生成 `docs/superpowers/plans/YYYY-MM-DD-phaseN-*.md`,再把本文档"总体进度"表里的链接从"待创建"改为实际路径。
- **不要重写历史行:** 本文档是进度快照,不是日志;更新已完成任务的状态直接改,不新增重复行。真正的时间线记录放在"更新记录"表里。
