# Dify 工具配置 - 复制粘贴版（30秒完成）

## 🚀 一键配置步骤

### Step 1: 打开 Dify
浏览器访问: `http://127.0.0.1:18080`

### Step 2: 进入工具管理
左侧菜单 → **工具** → **自定义工具** → **+ 创建工具**

---

## 📋 配置表单（直接复制粘贴）

### 基本信息
```
工具名称: BiddingCollector
描述: 招标信息采集API - 触发招标公告采集并返回结果
图标: 📋 (可选)
```

### OpenAPI Schema
**点击"从 Schema 导入"或"粘贴 OpenAPI Schema"，复制下面全部内容：**

```yaml
openapi: "3.1.0"
info:
  title: Bidding Collection API
  version: "1.0"
servers:
  - url: http://172.30.144.1:5294
paths:
  /api/bidding/collect:
    post:
      summary: 触发招标采集
      operationId: collectBidding
      requestBody:
        content:
          application/json:
            schema:
              type: object
              properties:
                planIds:
                  type: array
                  items:
                    type: string
                asOf:
                  type: string
      responses:
        '200':
          description: 成功
          content:
            application/json:
              schema:
                type: object
                properties:
                  plansExecuted:
                    type: integer
                  totalNoticesCollected:
                    type: integer
                  status:
                    type: string
      security:
        - ApiKeyAuth: []
components:
  securitySchemes:
    ApiKeyAuth:
      type: apiKey
      in: header
      name: X-Agent-Api-Key
```

### 鉴权配置
**在"鉴权"标签页：**
```
鉴权类型: API Key
位置: Header
Header 名称: X-Agent-Api-Key
Header 值: <与 Bidding:BridgeApiKey 一致；仅在 Dify 中填写>
```

---

## ✅ 测试工具

点击 **"测试"** 按钮：

**输入参数：**
```json
{}
```
或留空

**预期输出：**
```json
{
  "plansExecuted": 1,
  "totalNoticesCollected": 2,
  "status": "success",
  "plans": [{
    "planId": "demo-plan-001",
    "noticesCollected": 2,
    "outcome": "completed"
  }]
}
```

看到上面的输出就说明配置成功！✅

---

## 🔧 在 Workflow 中使用

### 创建测试 Workflow

1. **新建 Workflow**
   - 工作室 → + 创建应用 → Workflow
   - 名称: `招标采集测试`

2. **添加工具节点**
   - 点击 "+" → 工具 → BiddingCollector
   - 参数留空（执行所有计划）

3. **添加输出节点**
   ```
   执行计划数: {{BiddingCollector.plansExecuted}}
   采集公告数: {{BiddingCollector.totalNoticesCollected}}
   执行状态: {{BiddingCollector.status}}
   ```

4. **运行测试**
   - 点击右上角"运行"
   - 查看输出：`采集公告数: 2`

---

## 📊 高级用法

### 指定计划采集
```json
{
  "planIds": ["demo-plan-001"]
}
```

### 指定日期采集
```json
{
  "asOf": "2026-08-13"
}
```

### 结合其他节点

**示例 Workflow：关键词搜索 → 采集招标 → LLM 摘要**

```
[开始]
  ↓
[代码节点: 设置关键词]
  keywords = ["云计算", "大数据"]
  ↓
[BiddingCollector]
  planIds: []
  ↓
[LLM 节点: 分析招标公告]
  输入: {{BiddingCollector.plans}}
  Prompt: "分析以下招标公告，提取关键信息..."
  ↓
[结束: 输出摘要]
```

---

## ❓ 故障排查

### 测试失败："Could not connect"
**检查：**
1. .NET API 是否在运行
   ```bash
   curl http://localhost:5294/health
   # 应返回: {"status":"ready"}
   ```

2. API 监听地址是否为 0.0.0.0
   ```bash
   netstat -ano | grep :5294
   # 应显示: 0.0.0.0:5294
   ```

3. 从 WSL2 能否访问
   ```bash
   wsl curl http://172.30.144.1:5294/health
   ```

### 401 Unauthorized
**原因：** API Key 错误
**解决：** 确认 Dify Secret 与 API 的 `Bidding:BridgeApiKey` 一致

### 返回 0 条公告
**原因：** 今天已执行过该计划（去重生效）
**解决：** 
- 等到明天自动恢复
- 或为隔离演示配置新的临时 `Bidding:LedgerRoot` 和执行日期；不要删除共享演示台账

---

## 🎯 配置完成清单

- [ ] 工具名称设置为 `BiddingCollector`
- [ ] OpenAPI Schema 已粘贴
- [ ] 鉴权 Header 设置为 `X-Agent-Api-Key`，值仅保存在 Dify Secret
- [ ] 测试通过，首次运行返回 `totalNoticesCollected: 2`
- [ ] 在 Workflow 中成功调用

全部勾选即配置完成！🎉
