# M3 实现计划 — AI 分析（第一阶段：配置 + 云端 + 批量管道）

## Context

M2 已交付图库核心能力（导入/标签/收藏/设置/旧库迁移），92 测试绿，UIA 冒烟通过。M3 实现 PRD 第 4 节「AI 双轨需求」。

本计划为 **M3 第一阶段**：完成 AI 配置基础设施（W3/A3）、云端推理实现、批量分析管道与 UI 启用。使产品从「AI 完全不可用」跃迁至「用户配置云端服务后可对全库批量打标」。本地 ONNX 离线推理（M3-D）与语义搜索（M3-E）列为第二阶段，待第一阶段稳定后启动。

**核心约束（PRD + GEMINI）**：
- AI 为纯增量能力：关闭/未配置/断网/鉴权失败时基础功能 100% 正常，仅 AI 入口置灰并说明原因
- API Key 用 Windows DPAPI（CurrentUser）加密存储，不明文、不进日志
- 手动标签（source='manual'）永不被 AI 覆盖（TagService 已实现该契约）
- 批量分析后台运行、可取消、进度通知；AI 标签带 source='ai' + confidence
- 颜色分析不依赖 AI 档位（M3-D 统一实现）
- AI 网络调用经接口假对象测试（不依赖真实网络）
- 所有 P/Invoke 放 `Interop/`

---

## M3-A：AI 配置层 + DPAPI + W3/A3 UI

### 新建文件
- `src/QL.World/Interop/DpApiProtector.cs` — `Protect(string plaintext)` / `Unprotect(string ciphertext)`，返回 Base64 字符串，`CryptProtectData` CurrentUser scope，失败返回 null（不抛）
- `src/QL.World/Models/AiEnums.cs` — `AiMode` 枚举：`Disabled | Local | Cloud`
- `src/QL.World/Models/CloudAiConfig.cs` — `BaseUrl` / `ModelName` / `EncryptedApiKey` / `EmbeddingModelName`

### 修改文件
- `src/QL.World/Models/AppSettings.cs` — 新增 `AiMode`（默认 Disabled）、`CloudAiConfig CloudAi`、`string? LocalModelPath`
- `src/QL.World/Services/AppSettingsService.cs` — 新增 AI 配置属性：
  - `AiMode`（SetAndSave，Normalize 校验枚举）
  - `CloudBaseUrl` / `CloudModelName` / `CloudEmbeddingModelName`（SetAndSave，HTTPS 校验）
  - `SetCloudApiKey(string plaintext)` → DPAPI 加密后存 `CloudAi.EncryptedApiKey`
  - `GetCloudApiKey()` → DPAPI 解密返回明文（仅运行期内存，不进日志）
  - `ClearCloudApiKey()`
  - NormalizeLoadedSettings 补 AI 字段默认值
- `src/QL.World/ViewModels/SettingsViewModel.cs` — A3 页签：
  - `AiModeOptions`、`SelectedAiMode`（setter 写 settings + CanExecute 刷新云端控件可用性）
  - `CloudBaseUrl` / `CloudModelName` / `CloudEmbeddingModelName` 双向绑定
  - `CloudApiKey`（PasswordBox 绑定或明文框但不入日志；保存时调用 SetCloudApiKey）
  - `IsCloudConfigured` 计算属性
  - `TestCloudConnectionCommand`（M3-B 实现时调用 AiServiceFactory 创建 CloudAiService 测试）
  - `AiStatusText`（"AI：关闭" / "AI：云端模型 xxx" / "AI：本地（未下载模型）"）
- `src/QL.World/Views/SettingsWindow.xaml` — A3 页签：三档 RadioButton + 云端配置区（base_url/model_name/api_key/测试连接按钮）+ 合规提示 + 本地模型状态（M3-D 占位）
- `src/QL.World/ViewModels/StatusBarViewModel.cs` — `AiStatusText` 改为绑定 AppSettings.AiMode 的动态文本

### 测试
- `tests/QL.World.Tests/DpApiProtectorTests.cs` — 加解密往返、空串/超长、解密篡改返回 null
- `tests/QL.World.Tests/AppSettingsServiceAiTests.cs` — AiMode 持久化/枚举校验、API Key 加密存储后 config.json 不含明文、GetCloudApiKey 往返、Normalize 默认值

---

## M3-B：AI 服务抽象 + 工厂 + 云端实现

### 新建文件
- `src/QL.World/Services/Ai/IAiService.cs` — 接口：
  - `string Mode { get; }`
  - `bool IsAvailable { get; }`
  - `Task<AiAnalysisResult> AnalyzeImageAsync(string imagePath, CancellationToken ct)`
  - `Task<bool> TestConnectionAsync(CancellationToken ct)`
  - `bool SupportsEmbeddings { get; }`
- `src/QL.World/Models/Ai/AiAnalysisResult.cs` — `string? PrimaryCategory`、`IReadOnlyList<AiTag> Tags`（Tag + Confidence）、`float[]? Embedding`、`string? ErrorMessage`
- `src/QL.World/Models/Ai/AiTag.cs` — `string Tag`、`double Confidence`
- `src/QL.World/Services/Ai/DisabledAiService.cs` — IsAvailable=false，AnalyzeImage 返回 ErrorMessage="AI 未启用"
- `src/QL.World/Services/Ai/CloudAiService.cs` — OpenAI 兼容：
  - 构造接收 `CloudAiConfig config, HttpMessageHandler? handler = null`（handler 注入便于测试）
  - `TestConnectionAsync`: GET `{base_url}/models` 校验鉴权
  - `AnalyzeImageAsync`: 
    - 标签：POST `{base_url}/chat/completions`，system prompt 要求输出 JSON `{category, tags:[{tag,confidence}]}`
    - Embedding（若配置了 embedding 模型）：POST `{base_url}/embeddings`
  - 仅 HTTPS；超时 30s；失败返回 ErrorMessage 不抛
- `src/QL.World/Services/Ai/AiServiceFactory.cs` — `Create(AppSettingsService settings, HttpMessageHandler? handler)` 根据 AiMode 返回 Disabled/Cloud（Local 抛 NotSupportedException，M3-D 实现）

### 修改文件
- `src/QL.World/Services/ServiceRegistry.cs` — 12 参，新增 `IAiService Ai` 属性
- `src/QL.World/App.xaml.cs` — OnStartup 中 `new AiServiceFactory().Create(settings)` 注入 registry
- `src/QL.World/ViewModels/SettingsViewModel.cs` — 构造加 `IAiService? ai` 或工厂，`TestCloudConnectionCommand` 调用 `ai.TestConnectionAsync`

### 测试
- `tests/QL.World.Tests/Ai/DisabledAiServiceTests.cs` — IsAvailable=false、AnalyzeImage 返回错误
- `tests/QL.World.Tests/Ai/CloudAiServiceTests.cs` — mock HttpMessageHandler：
  - TestConnection 200 → true，401 → false
  - AnalyzeImage 解析 JSON 响应
  - 非 HTTPS base_url 拒绝
  - 超时/网络错误返回 ErrorMessage
- `tests/QL.World.Tests/Ai/AiServiceFactoryTests.cs` — Disabled/Cloud 路由正确

---

## M3-C：批量分析管道 + 写入 + UI 启用

### 新建文件
- `src/QL.World/Services/Ai/AiAnalysisService.cs` — 批量分析编排：
  - 构造接收 `IAiService ai, DatabaseService db, TagService tags, ImageQueryService query, NotificationService notifications, Action<string> log`
  - `Task RunBatchAsync(int batchSize = 100, CancellationToken ct)`：
    1. 查询未分析图片（`NOT EXISTS (SELECT 1 FROM image_embeddings WHERE image_id=images.id)` 或 AI 标签为空）
    2. 每批 100 张，循环 `ai.AnalyzeImageAsync`
    3. 写入 `image_tags`（source='ai'，复用 TagService.AddTags）
    4. 写入 `image_embeddings`（model_id + dim + BLOB，小端 float 自描述头）
    5. 更新 `images.primary_category`（仅当 NULL 时）
    6. 进度通过 notifications 发布（NotificationKind.AiBatchProgress / AiBatchCompleted）
    7. 可取消，完成后发通知
  - `bool IsRunning`、`int ProcessedCount`、`int TotalCount`
  - 颜色分析（dominant_colors）也在此管道中触发（不依赖 AI 档位）

### 修改文件
- `src/QL.World/Services/ServiceRegistry.cs` — 13 参，新增 `AiAnalysisService AiAnalysis`（或让 AiAnalysis 内部持有 IAiService，registry 只暴露 AiAnalysis）
- `src/QL.World/App.xaml.cs` — 组装 AiAnalysisService
- `src/QL.World/ViewModels/TopBarViewModel.cs` — `AiCommand` 改为启用（当 Ai.IsAvailable 时），触发 `AiAnalysis.RunBatchAsync`；`AiDisabledReason` 动态显示原因
- `src/QL.World/ViewModels/ShellViewModel.cs` — 传递 AiAnalysisService 给 TopBar
- `src/QL.World/Models/NotificationModels.cs` — 新增 `AiBatchProgress` / `AiBatchCompleted` kind
- `src/QL.World/Data/Schema.v1.sql` — 无需修改（image_embeddings 已在 v1）

### 测试
- `tests/QL.World.Tests/Ai/AiAnalysisServiceTests.cs` — mock IAiService：
  - 批量写入 image_tags (source=ai)
  - manual 标签不被覆盖（TagService 已保证）
  - embedding 写入 image_embeddings
  - primary_category 仅 NULL 时更新
  - 取消能停止
  - 进度通知触发

---

## 第二阶段（后续，不在本次执行）

### M3-D：本地 ONNX 推理
- 引入 `Microsoft.ML.OnnxRuntime`
- `Services/Ai/LocalAiService.cs`：模型加载、推理、embedding 提取
- 模型下载/校验（SHA-256、进度、断点重试）
- 颜色分析（dominant_colors，始终可用）

### M3-E：语义搜索
- `ImageQueryService` 语义搜图（embedding 余弦相似度，千级 <100ms）
- Gallery 搜索模式加「语义」
- 以图搜相似图

---

## 版本与文档

- `QL.World.csproj`：`<Version>0.3.0</Version>`
- `docs/UI_NUMBERING.md`：W1/A1-2（AI 按钮启用）、W3/A3（已实现）、W1/C1（AI 档位动态）
- `CHANGELOG.md`：新增 `## [0.3.0] — M3 AI 分析（第一阶段）`
- `GEMINI.md`：里程碑状态更新为 M3

## 验证

1. **单元测试**：`dotnet test -c Release` 全绿 + 0 警告（M3-A/B/C 各 3-5 测试文件）
2. **门禁**：`build.ps1 -Mode Prepare` 通过
3. **UIA 冒烟**：
   - W3/A3 三档单选切换即时生效
   - 云端配置输入 + 测试连接（mock 服务或真实 OpenAI 兼容端点）
   - AI 按钮从置灰变可用
   - 批量分析触发后通知中心有进度/完成条目
   - DB 验证：image_tags 含 source='ai' 行、image_embeddings 有数据
4. **安全验证**：config.json 中 API Key 为 Base64 密文，error.log 不含明文 Key
