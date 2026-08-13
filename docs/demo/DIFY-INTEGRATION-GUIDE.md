# Dify 集成配置指南（图文步骤）

## 前提条件
- ✅ Dify 运行中：http://127.0.0.1:18080
- ✅ .NET API 运行中：http://localhost:5294
- ✅ API Key: `demo-key-2026`

---

## 方法 1：通过 OpenAPI 文件导入（推荐）

### Step 1: 打开浏览器
访问 `http://127.0.0.1:18080`

### Step 2: 进入工具配置
1. 点击左侧菜单 **「工具」**
2. 点击 **「自定义工具」**
3. 点击 **「+ 创建工具」** 或 **「导入」**

### Step 3: 导入 OpenAPI 配置
1. 选择 **「从 OpenAPI 导入」**
2. 点击 **「上传文件」** 
3. 选择文件：`docs/demo/dify-openapi-spec.yaml`
4. 或者复制文件内容粘贴到文本框

### Step 4: 配置鉴权
导入后会自动识别 `ApiKeyAuth`，填写：
- **Header 名称**: `X-Agent-Api-Key` （已自动填充）
- **Header 值**: `demo-key-2026`

### Step 5: 保存并测试
点击 **「测试」**，参数留空 `{}`

**预期结果：**
```json
{
  "plansExecuted": 1,
  "totalNoticesCollected": 2,
  "status": "success"
}
```

---

## 方法 2：手动配置（如果导入失败）

### Step 1-2: 同上

### Step 3: 手动填写基本信息
- **工具名称**: `BiddingCollector`
- **描述**: `招标信息采集API`
- **基础URL**: `http://host.docker.internal:5294`

### Step 4: 添加接口
点击 **「+ 添加接口」**：

**接口配置：**
- **方法**: `POST`
- **路径**: `/api/bidding/collect`
- **操作ID**: `collectBidding`
- **描述**: `触发招标信息采集`

**请求参数（Body - JSON）：**
```json
{
  "type": "object",
  "properties": {
    "planIds": {
      "type": "array",
      "items": {"type": "string"},
      "description": "计划ID列表，留空=全部"
    },
    "asOf": {
      "type": "string",
      "format": "date",
      "description": "执行日期(yyyy-MM-dd)"
    }
  }
}
```

**响应格式（200 OK）：**
```json
{
  "type": "object",
  "properties": {
    "plansExecuted": {"type": "integer"},
    "totalNoticesCollected": {"type": "integer"},
    "status": {"type": "string", "enum": ["success", "partial", "failed"]}
  }
}
```

### Step 5: 配置鉴权
在 **「鉴权」** 标签：
- **类型**: `Custom Header`
- **Header 名称**: `X-Agent-Api-Key`
- **Header 值**: `demo-key-2026`

### Step 6: 测试
参数输入：`{}` 或留空

---

## 在 Workflow 中使用

### 创建测试 Workflow

1. 进入 **「工作室」** → **「创建应用」** → **「Workflow」**
2. 命名：`招标采集测试`

### 添加节点

1. 点击 **「+」** 添加节点
2. 选择 **「工具」** → **「BiddingCollector」**
3. 配置参数：
   - **planIds**: 留空（执行所有）或输入 `["demo-plan-001"]`
   - **asOf**: 留空（使用今天）

### 连接输出

添加 **「结束」** 节点，输出变量：
```
采集计划数: {{BiddingCollector.plansExecuted}}
采集公告数: {{BiddingCollector.totalNoticesCollected}}
执行状态: {{BiddingCollector.status}}
```

### 运行测试

点击右上角 **「运行」**，查看输出：
```
采集计划数: 1
采集公告数: 2
执行状态: success
```

---

## 故障排查

### 401 Unauthorized
**原因**: API Key 错误
**检查**:
1. Dify 工具配置中 Header 值是否为 `demo-key-2026`
2. .NET API 是否在 Development 环境运行（`dotnet run --environment Development`）

### 连接失败 / Timeout
**原因**: URL 配置错误
**检查**:
1. Dify 在 Docker 里必须用 `host.docker.internal` 而非 `localhost`
2. 端口是否为 `5294`（查看 API 启动日志）
3. Windows 防火墙是否阻止了连接

### 测试工具显示 "Could not connect"
**解决**:
```bash
# 确认 API 运行中
curl http://localhost:5294/health

# 确认从 WSL2 能访问（模拟 Dify 容器环境）
wsl curl http://$(cat /etc/resolv.conf | grep nameserver | awk '{print $2}'):5294/health
```

---

## 下一步

配置完成后，可以：
1. ✅ 在任何 Workflow 中调用 `BiddingCollector` 工具
2. ✅ 结合 Dify 的调度功能实现定时采集
3. ✅ 将采集结果接入 Dify Knowledge Base
4. ✅ 用 Dify 的 LLM 对采集的公告进行摘要/分析

**完整 OpenAPI 配置文件位置:**
`docs/demo/dify-openapi-spec.yaml`
