# Phase 1: Dify Infrastructure Setup Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stand up a private Dify environment with imported classified/unclassified knowledge bases and a working web-search plugin, so later phases can build workflows and content generation on top of it. The IMA-to-Dify note sync path is deferred and is not a prerequisite for later phases.

**Architecture:** Dify is deployed as a self-hosted Docker Compose stack reachable only on the internal network. Source material is split into two knowledge-base tracks — topic-classified libraries for material that is already organized, and a single "待分类" (unclassified) library as a landing zone — so retrieval routing in Phase 2 has a clean priority order to build on. A web search plugin covers queries the knowledge bases can't answer. Phase 2 uses the five formally imported and retrieval-validated files as its initial validation baseline; future files can be imported directly into Dify.

**Tech Stack:** Dify (self-hosted, Docker Compose), Dify's built-in knowledge-base import (Markdown, Word `.doc/.docx`, PDF), and a Dify-marketplace web search plugin.

## Global Constraints

- This repository (`market-intelligence-agent`) is the .NET extension-services codebase; Dify itself is a separate, external system per `docs/superpowers/specs/2026-07-31-market-intelligence-agent-design.md` — Dify deployment and knowledge-base operations happen outside this repo and are not committed as code here.
- No credentials, API keys, server addresses, or scraped content are committed to this repository, per the same design spec.
- Target completion window: 2026-08-03 ~ 2026-08-07, per the approved 智能体项目开发流程计划.docx schedule.
- Acceptance criteria for each task below are fixed by that approved schedule and must not be silently loosened.

---

## Planned Artifacts

These are the Dify-side objects created during this phase. Nothing in this list is committed to the `market-intelligence-agent` repo — they live inside the Dify instance.

| Artifact | Responsibility |
|---|---|
| Dify Docker Compose stack | Self-hosted platform; runs API, worker, db, vector store |
| Dify admin account + team workspace | Access boundary for the whole project |
| Topic-classified knowledge bases (N bases, one per topic) | Material that is already organized; used in Phase 2 high-priority retrieval |
| "待分类" knowledge base | Landing zone for unorganized material; lowest retrieval priority |
| Web search plugin (Dify marketplace) | Covers queries not answered by any knowledge base |
| IMA → Dify sync SOP (Markdown doc committed here) | Deferred reference procedure; not a Phase 1 or Phase 2 gate |

SOP location in this repo: `docs/ops/ima-to-dify-sync.md` (created in Task 5).

---

## Task 1: Dify Private Deployment — Server, Environment, Accounts

**Target date:** 2026-08-03

**Files:**
- Create: `docs/ops/dify-deployment.md` (server address recorded here as a placeholder label `DIFY_HOST`, not the real IP)

**Interfaces:**
- Consumes: a Linux server with Docker and Docker Compose installed (minimum 4 vCPU / 8 GB RAM recommended by Dify docs), internal network access.
- Produces: Dify reachable at `http://DIFY_HOST` from within the internal network; at least one admin account active; one project workspace created.

- [ ] **Step 1: Clone the official Dify Docker Compose repo onto the server**

```bash
git clone https://github.com/langgenius/dify.git --branch main --depth 1
cd dify/docker
cp .env.example .env
```

Open `.env` and set at minimum:
- `SECRET_KEY` — generate with `openssl rand -hex 32`
- `INIT_PASSWORD` — initial admin password (store this in your team password manager, never commit it)

- [ ] **Step 2: Start the stack**

```bash
docker compose up -d
# Wait ~2 minutes for the db migration to complete
docker compose ps        # all services should show "running"
docker compose logs api | tail -30   # look for "Application started"
```

Expected: `dify-api-1`, `dify-worker-1`, `dify-db-1`, `dify-redis-1`, `dify-nginx-1` all show state `running`.

- [ ] **Step 3: Verify intranet access**

From a machine on the same internal network (not the server itself):

```
Open browser → http://DIFY_HOST
Expected: Dify login page loads
```

If unreachable: confirm the server's firewall allows port 80 from internal subnet. Do not expose port 80 to the internet at this stage.

- [ ] **Step 4: Create admin account and workspace**

1. Navigate to `http://DIFY_HOST/install` and complete the initial admin setup form.
2. Log in with the admin credentials.
3. Create a new workspace named `market-intelligence`.
4. Invite any team members who need access (Settings → Members).

- [ ] **Step 5: Record deployment metadata**

Create `docs/ops/dify-deployment.md` in this repo:

```markdown
# Dify Deployment Notes

- Host label: DIFY_HOST  (real address stored in team password manager, not here)
- Workspace: market-intelligence
- Stack version: (record git tag or commit SHA from step 1)
- Deployed: 2026-08-03
- Admin account: (username only — password in team password manager)
```

Commit:

```bash
git add docs/ops/dify-deployment.md
git commit -m "docs: add Dify deployment metadata (host redacted)"
```

**Acceptance criteria:**
- Dify login page reachable from internal network.
- Admin account logs in successfully.
- `market-intelligence` workspace visible in workspace list.

---

## Task 2: Topic-Classified Knowledge Bases — Import

**Target date:** 2026-08-04

**Files:** No changes to this repo. All work is inside the Dify UI.

**Interfaces:**
- Consumes: the classified source documents already organized by topic.
- Produces: one Dify knowledge base per topic, each containing the relevant documents and returning results on a test retrieval query.

- [ ] **Step 1: List all topic categories from your existing classified material**

Before touching Dify, write down every topic name on paper or in a scratch doc. This becomes the exact set of knowledge base names. Aim for 5–15 topics; if you have more, consider grouping related ones. Example names: `项目案例`, `竞品分析`, `行业报告`, `产品资料`, `合规文件`.

- [ ] **Step 2: Create one knowledge base per topic in Dify**

For each topic:

```
Dify sidebar → Knowledge → + Create knowledge base
  Name: [topic name exactly as listed in step 1]
  Description: [one sentence summary]
  Indexing: High Quality (uses embedding model — confirm a model is configured
            in Settings → Model Provider before this step)
  Retrieval: Vector Search
```

- [ ] **Step 3: Import documents into each knowledge base**

For each knowledge base:

```
Open the knowledge base → + Add file
  Upload: drag in all documents for that topic (MD, DOC/DOCX, PDF supported)
  Chunk size: 500 tokens (Dify default — keep unless retrieval quality is poor)
  Click Save and process
```

Wait for the indexing status to show `Completed` for every document before moving on.

- [ ] **Step 4: Verify retrieval works for each knowledge base**

For each knowledge base, use the built-in test retrieval panel:

```
Open knowledge base → Retrieval test (top-right tab)
  Query: type a question you would expect this knowledge base to answer
  Expected: at least one chunk returned with a relevance score > 0.5
```

If a knowledge base returns nothing or low scores: check that indexing completed, that the model provider is configured, and that documents are not empty or image-only.

**Acceptance criteria:**
- Every topic-classified knowledge base exists in Dify with `Completed` indexing status.
- Retrieval test returns relevant chunks for each base.

---

## Task 3: "待分类" (Unclassified) Knowledge Base — Import

**Target date:** 2026-08-04

**Files:** No changes to this repo.

**Interfaces:**
- Consumes: all source documents that have not yet been classified by topic.
- Produces: a single `待分类` knowledge base containing all unclassified documents, queryable by retrieval test.

- [ ] **Step 1: Create the unclassified knowledge base**

```
Dify sidebar → Knowledge → + Create knowledge base
  Name: 待分类
  Description: 尚未按主题分类的资料，检索优先级最低
  Indexing: High Quality
  Retrieval: Vector Search
```

- [ ] **Step 2: Import all unclassified documents**

```
Open 待分类 knowledge base → + Add file
  Upload: all unclassified documents in one batch
  Wait for indexing status: Completed for every document
```

- [ ] **Step 3: Verify retrieval works**

```
待分类 → Retrieval test
  Query: a generic phrase likely to appear in the unclassified material
  Expected: at least one chunk returned
```

**Acceptance criteria:**
- `待分类` knowledge base exists; all unclassified documents have `Completed` indexing.
- Retrieval test returns at least one result.

---

## Task 4: Web Search Plugin — Install and Smoke Test

**Target date:** 2026-08-05

**Files:** No changes to this repo.

**Interfaces:**
- Consumes: a Dify workspace with a usable LLM model configured (required for plugin execution).
- Produces: a web search plugin installed and returning results for a test query via a minimal Dify chatflow or the plugin test panel.

- [ ] **Step 1: Install the web search plugin from the Dify marketplace**

```
Dify → Plugins (sidebar or top-nav) → Marketplace
  Search: "web search" or "SerpAPI" or "Bing Search" — pick the plugin that
          matches the search API your team has access to
  Click Install → follow prompts
```

If your team does not yet have a search API key: sign up for a free tier key (SerpAPI, Brave Search, or Bing Search API) and store it in the team password manager.

- [ ] **Step 2: Configure the plugin with the API key**

```
Plugins → Installed → [web search plugin] → Configure
  API Key: [paste your key — this is stored in Dify's secret store, not committed]
  Save
```

- [ ] **Step 3: Create a minimal test chatflow and verify search returns results**

```
Dify → Studio → Create app → Chatflow
  Name: web-search-smoke-test
  Add a Tool node: pick the installed web search plugin
  Input: question variable
  Search query: {{question}}
  Wire: Start → Tool (web search) → End
  Save and Publish (draft mode is fine)
```

Open the debug/preview panel:

```
Type: "2024年中国光伏行业市场规模"  (or any current-events query)
Expected: tool node executes, returns ≥ 1 search result snippet in the output
```

- [ ] **Step 4: Delete the smoke-test app**

The smoke-test chatflow is only needed to confirm the plugin works. Delete it:

```
Studio → web-search-smoke-test → Settings → Delete app
```

**Acceptance criteria:**
- Web search plugin installed and configured.
- Search query executed via the plugin returns at least one result snippet without error.

---

## Task 5: IMA Notes → Dify Sync Path — Deferred

**Target date:** Deferred by project decision on 2026-08-06

**Status:** ⏭️ This task is intentionally skipped for now. It does not block Phase 1 closure, Phase 2 startup, Workflow configuration, or Phase 2 acceptance. The five formally imported market-department files from Task 2 are the validation baseline instead.

When this task is resumed, import prepared `.md`, Word `.doc/.docx`, or `.pdf` files directly into the appropriate Dify knowledge base. Do not add an IMA direct integration or automatic synchronization implementation in Phase 1/2 without a separate approval.

**Files:**
- Create: `docs/ops/ima-to-dify-sync.md` — repeatable SOP committed to this repo.

**Interfaces:**
- Consumes: IMA (腾讯ima.qq.com) notes organized under one or more workspace notebooks; at least one note with substantive text content (not just images).
- Produces when resumed: the note content retrievable from a Dify knowledge base; `docs/ops/ima-to-dify-sync.md` remains the repeatable reference procedure.

> **Deferral reason:** IMA does not have a direct Dify integration. The export/import loop is intentionally postponed so it does not delay Phase 2; Phase 2 uses the five files already imported and validated in Task 2.

- [ ] **Step 1: Export a batch of notes from IMA (when the deferred task is resumed)**

In IMA:

```
Select notes that should eventually live in the high-priority knowledge bases
Export → Markdown (.md), Word (.doc/.docx), or PDF (.pdf) — whichever produces cleaner output
Download the export archive
```

Inspect the exported files: confirm the text content is readable and not garbled (some IMA exports embed content as images — if so, you may need the DOCX path).

- [ ] **Step 2: Decide target knowledge base and import**

Pick the topic knowledge base most relevant to the exported notes (created in Task 2). If the notes span multiple topics, split the files by topic first.

```
Dify → Knowledge → [target knowledge base] → + Add file
  Upload the prepared .md, .doc/.docx, or .pdf files
  Wait for indexing: Completed
```

- [ ] **Step 3: Verify the imported notes are retrievable**

```
[target knowledge base] → Retrieval test
  Query: a phrase you know appears in one of the exported notes
  Expected: the note's chunk appears in results with recognizable content
```

- [ ] **Step 4: Document the SOP**

Create `docs/ops/ima-to-dify-sync.md`:

```markdown
# IMA → Dify 知识库同步 SOP

## 触发条件
- IMA 中积累了新的、已整理好的笔记，需要迁移到 Dify 高优先级知识库。
- 频率：恢复后按团队确定的责任人和节奏执行；本次暂缓，不作为阶段一/二验收条件。

## 步骤

### 1. 在 IMA 中选择并导出笔记
1. 打开 [ima.qq.com](https://ima.qq.com)，进入目标工作区。
2. 勾选需要迁移的笔记（按主题批量选择）。
3. 导出格式：优先选 Markdown；也可使用 Word (.doc/.docx) 或 PDF (.pdf)。
4. 下载导出压缩包，解压到本地临时目录。

### 2. 检查导出文件
- 用文本编辑器打开 2–3 个文件，确认正文可读，无乱码。
- 若文件是纯图片（无可检索文字），先用 OCR 工具转文字后再导入（阶段四 OCR 模块上线后可自动化）。

### 3. 按主题分拣文件
- 将文件归入对应主题子目录（与 Dify 知识库名称对应）。
- 无法确定主题的文件放入 `待分类` 目录。

### 4. 导入 Dify 知识库
1. Dify → 知识库 → 选择对应主题知识库（或「待分类」）。
2. 点击「+ 添加文件」，直接上传该主题目录下的 `.md`、`.doc/.docx` 或 `.pdf` 文件。
3. 等待索引状态变为「已完成」。

### 5. 验收
- 在知识库「检索测试」中输入笔记中的关键句，确认能命中对应片段。

### 6. 清理
- 删除本地临时导出目录。
- 在 IMA 中为已迁移笔记打标签「已同步至Dify」，避免重复迁移。

## 注意事项
- 不要将 IMA 导出文件提交到 Git 仓库（.gitignore 中已排除 `data/` 目录）。
- Dify 知识库一次性上传文件数量建议不超过 50 个，分批导入更稳定。
```

- [ ] **Step 5: Commit the SOP**

```bash
git add docs/ops/ima-to-dify-sync.md
git commit -m "docs: add IMA to Dify sync SOP"
```

**Current-phase acceptance criteria:**
- IMA connectivity is explicitly recorded as deferred and is not required for Phase 1 completion or Phase 2 entry.
- The five formally imported market-department files remain the validation baseline for Phase 2.
- `docs/ops/ima-to-dify-sync.md` remains available as a future reference and is not evidence that an end-to-end IMA run has been completed.

---

## Self-Review

**Spec coverage:**
- Dify私有化部署 → Task 1 ✓
- 已分类资料建库导入 → Task 2 ✓
- 未分类资料导入待分类库 → Task 3 ✓
- 联网搜索插件接入调试 → Task 4 ✓
- IMA笔记同步流程打通 → Task 5 暂缓，不阻塞后续阶段

**Placeholder scan:** No TBD, TODO, or "similar to Task N" placeholders present. All steps have explicit commands or UI paths.

**Type consistency:** No code interfaces in this phase — task boundaries are acceptance criteria and file paths only, consistent throughout.
