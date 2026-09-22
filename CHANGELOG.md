# Changelog

本项目遵循语义化版本（major.minor.patch）。里程碑：M0 骨架 → M1 核心素材管理 →
M2 组织与可靠性 → M3 AI 双轨 → M4 专业化与发布（v1.0.0）。

## [Unreleased]

## [0.5.0] — M3-E2 本地 CLIP 文本编码

- 本地文→图语义搜索打通：`LocalAiService.EmbedTextAsync` 从 M3-E1 的 null
  占位实现改为真实链路——BPE tokenize → `text_model.onnx`（Xenova/
  clip-vit-base-patch32，254 MB，fp32）推理 → 512 维 CLIP 文本向量；
  Local 模式语义搜索与 Cloud 模式行为对齐
- `ClipTokenizer`（E-A）：从 M3-D Spike 移植的 CLIP BPE tokenizer（77 context，
  SOT/EOT，GPT-2 预分词正则，BytesToUnicode 字节映射，BPE 合并缓存）；
  `vocab.json`（49408 条）+ `merges.txt` 作为 EmbeddedResource 分发，
  `EnsureDistributed` 首次使用时解压到 models 目录（幂等）
- 模型包捆绑下载（E-C）：`ModelDownloadService` 重构为多文件 bundle——
  vision_model.onnx（351 MB）+ text_model.onnx（254 MB）顺序下载、单进度条
  跨两阶段（0–50 / 50–100）、各自 SHA-256 校验；任一文件失败回滚已下载文件，
  不留半安装包；`CheckLocalState`/`DeleteModel` 覆盖整个 bundle
- 向后兼容：仅存在 vision_model.onnx 的旧安装，图像分析 + 图→图搜索照常，
  文→图降级为 null + 通知（设置面板提示下载完整模型包）；手动导入仍仅
  支持 vision 单文件，文本模型经下载通道获取
- 设置 W3/A3：本地区块信息行扩展显示两模型体积；Verifying 阶段展示
  当前校验文件名；语义搜索不可用通知文案更新（关闭档/缺文本模型两种指引）
- 测试：189 → 204（新增 ClipTokenizerTests 6 用例：BPE token 数回归、
  BytesToUnicode 守护、资源解压幂等等；LocalAiService EmbedText 4 用例：
  stub text session 512 维输出/缺模型降级/session 异常/旧版兼容；
  ModelDownloadService bundle 6 用例：双文件下载/失败回滚/组合状态/删除/
  进度边界；MiniOnnx 新增 text stub 图构造）

## [0.4.0] — M3 第二阶段（本地 ONNX + 颜色分析 + 语义搜索）

### D-A：颜色分析（无新依赖）

- `ColorExtractor`：纯静态 `Extract(Image) → IReadOnlyList<string>`；降采样 ≤64×64
  → K-means（k=4，固定种子确定性）→ 3–5 个 `"#RRGGBB"`
- `ColorAnalysisService`（服务 #14）：`RunPendingAsync(batchLimit=500, ct)`
  后台查询 `dominant_colors IS NULL OR ''` 逐张回填；仅回填 NULL/空不覆盖
  （确定性算法回填即幂等）；文件缺失/解码失败跳过记日志
- 导入完成 + FileWatcher 自动导入后 fire-and-forget 触发；手动批量按钮
  「分析颜色」(W1/A1-8)，与 AI 档位无关
- 测试：`ColorExtractorTests`（纯色→单 hex、上下分区→多簇、确定性、灰度/透明）、
  `ColorAnalysisServiceTests`（NULL 回填/非 NULL 不动/缺文件不崩）

### D-B：模型下载器 + 设置 UI

- `ModelManifest`：常量（ModelId=`clip-vit-b32-fp32`、SHA-256、体积 351,685,709 字节、
  License MIT、SourceUrl + hf-mirror.com 镜像、PromptTemplateVersion）
- `ModelDownloadService`：`CheckLocalState() → NotDownloaded/Corrupted/Ready`
  （stat + SHA 流式校验）；`DownloadAsync(IProgress<ModelDownloadProgress>, ct)`
  使用 `.part` 临时文件 + HTTP Range 断点续传（416/200 重启）；主源连续失败切镜像；
  完成后 SHA 校验失败删 `.part`；`ImportManualAsync(sourceFile)` 复制+校验；
  `DeleteModel()`
- `ModelDownloadProgress`：record(Phase, Percent, BytesReceived, TotalBytes, Message)
- `SettingsViewModel` 本地区块：`LocalModelStateText`、`IsDownloading`、
  `DownloadPercent`、`DownloadModelCommand`/`CancelDownloadCommand`/
  `SelectModelFileCommand`/`DeleteModelCommand`；进度走 `IProgress<T>` 自动回 UI 线程；
  完成失败发 NotificationService
- `SettingsWindow.xaml` W3/A3：替换占位 → 状态文本 + ProgressBar + 四按钮 +
  体积/许可/来源小字行（PRD 硬性要求展示）
- 测试 `ModelDownloadServiceTests`：HttpMessageHandler stub 模拟 Range/416/断流/
  镜像切换/校验失败/手动导入
- `AppSettingsService` 新增只读 `DataRoot` 属性；`LocalModelPath` 收窄为手动导入源

### D-C：LocalAiService + 工厂 + 管道集成

- `ClipPreprocessor`：纯静态 Resize 短边 224 → 中心裁剪 224×224 → CLIP mean/std
  归一化 → NCHW float32
- `ClipTextIndex`：加载 `Assets/Ml/text-embeddings-clip-vit-b32.json`；
  `RankGroups(imageEmb)` 分类组 argmax+阈值 → PrimaryCategory；四类标签组内
  top-k(≤3)+阈值 → Tags；纯预计算向量余弦，无 ONNX
- `LocalAiService`（IAiService 实现）：惰性创建 `InferenceSession`，`lock` 包裹
  `session.Run`；intra-op = 核数一半；`IsAvailable` 动态 stat 模型文件（会话中途下载
  完成即生效）；`ModelId = ModelManifest.ModelId`；输出 512 维 embedding；
  `internal` 构造重载注入 `Func<string, InferenceSession>` 供测试 fake；全异常捕获 →
  ErrorMessage；`TestConnectionAsync` = CheckLocalState==Ready
- `QL.World.csproj` 加 `Microsoft.ML.OnnxRuntime` 1.22.1（仅 CPU 包）
- `AiServiceFactory`：`AiMode.Local => new LocalAiService(settings, log)` 不抛异常
  （未下载模型时 IsAvailable=false，TopBar 置灰自动生效）
- 测试：`ClipPreprocessorTests`、`ClipTextIndexTests`、`LocalAiServiceTests`
  （fake session：正常路径/异常→ErrorMessage/文件缺失→IsAvailable=false）；
  `AiAnalysisServiceTests` 增补 Local 持久化用例（dim=512）
- **M3-D Spike 结论**（决定默认档）：fp32（vision_model.onnx 351,685,709 字节，
  sha256 fd6e1402…7d40，ModelId=clip-vit-b32-fp32）；int8 四判据中质量两项 FAIL
  （分类一致率 54.2%<95%，余弦 0.938<0.98），速度 1.62×/体积 25% 两项 PASS；
  完整决策表见 `tools/spike/ClipSpike/README.md`

### E1：语义搜索（图→图 + 文→图 Cloud 模式）

- 新增 `SemanticSearchService`（服务 #15）：内存缓存 `image_embeddings` 全表，
  余弦相似度排序，top-50 返回；千级库 <100ms；`Invalidate()` 由
  `AiAnalysisService.EmbeddingsUpdated` 事件触发失效缓存
- 图→图相似搜索：图卡右键「找相似图」(W1/B2-3-CM1) + 详情栏「找相似」按钮
  (W1/B3-8)；任意 AI 模式可用（依赖已存 embedding，不需实时模型调用）
- 文→图语义搜索：搜索框下拉新增「语义」档 (W1/A1-5)，复用 300ms 防抖；
  Cloud 模式经 `IAiService.EmbedTextAsync` 调 `/embeddings` 端点
- Local/Disabled 模式文→图暂不可用（M3-E2 实现本地 CLIP text encoder）：
  `EmbedTextAsync` 返回 null，UI 显示通知后回退 Tag 模式
- `LibraryFilter` 新增 `SemanticResultIds` + `SimilarToImageId` 字段；
  `ImageQueryService.AppendClauses` 优先走 `i.id IN (...)` 分支
- `NotificationKind.SemanticSearchUnavailable` 新增
- 测试：169 → 189（新增 SemanticSearchServiceTests 9 用例 +
  ImageQueryServiceSemanticTests 4 用例 + Cloud/Disabled/Local EmbedTextAsync
  扩展）

## [0.3.0] — M3 AI 分析（第一阶段）

- AI 配置（W3/A3）：三档单选（关闭 / 本地 ONNX / 云端服务），切换即时生效
- DPAPI 加密：API Key 经 Windows DPAPI（CurrentUser）加密后 Base64 存储，
  config.json 不含明文，日志不含明文；PasswordBox 直接转发加密不落绑定属性
- 云端推理：`CloudAiService` 实现 OpenAI 兼容 chat/completions（分类+标签，
  JSON 输出）与 embeddings；仅 HTTPS；失败返回错误结果不抛异常；
  HttpMessageHandler 注入便于 mock 测试
- AI 服务抽象：`IAiService` 接口 + `AiServiceFactory` 按模式路由；
  `DisabledAiService` 保证 AI 未启用时基础功能不受影响
- 批量分析（A1-2 AI 按钮）：`AiAnalysisService` 后台批量处理未分析图片，
  写 AI 标签（source='ai'，手动标签永不覆盖）+ embedding + primary_category；
  可取消；进度/完成通知进消息中心；始终写 embedding 行标记已分析避免重处理
- 状态栏 AI 档位动态显示（关闭/云端/本地）
- 安全：云端 URL 强制 HTTPS，非 HTTPS 静默拒绝

## [0.2.0] — M2 组织与可靠性

- 浅色主题：`Light.xaml`（17 键与 Dark 一一对应）+ Dark 补齐 `Background`
  键（修复 ShellWindow 引用未定义资源的隐患）；设置中即时切换
- 收藏夹（B1-2）：`CollectionService` 全套 CRUD（封面/去重加入/排序/级联删除）；
  收藏夹树单击筛选、再点清除；右键 新建/重命名/删除/设封面/加入选中；
  画廊卡片拖拽到收藏夹（DragDrop）加入
- 文件缺失重定位（B3-2）：`ImageQueryService.RelocateFile` 重算 hash/尺寸、
  清缺失标记；内容与库内其它记录重复时拒绝；`InputDialog` 通用输入对话框
- 消息中心（A1-6）：`NotificationService`（会话级，未读计数/批量聚合/按类清除）；
  铃铛红点徽标 + 弹层列表（全部已读/清空）；导入/缩略图/监控/缺失/旧版导入
  事件发布通知
- 文件监控（W3/A4）：`FileWatcherService`（2s 防抖聚合；新增自动导入、
  删除标记缺失并通知、重命名跟随；目录 diff 热更新）；`IFileWatcher`
  抽象便于测试；设置中管理目录列表与自动导入开关
- 设置窗口（W3）：常规（主题/完整性）、缩略图（边长/质量/全部重建）、
  AI 占位（M3）、监控目录、关于（版本/统计/旧版导入/日志）；全部即时保存
- 旧版 Python 库导入器（W3/A5）：`LegacyPythonImporter` 只读连接读取
  Python 库，列差异映射（补 sortable_name/updated_at/is_missing，空分类→NULL），
  路径+hash 双去重，迁移标签/收藏夹关联，跳过 embeddings 与 dominant_colors
- ServiceRegistry 7→11 参（+Collections/Notifications/Watcher/LegacyImporter）
- 修复：`CollectionService.GetAll` 列索引错位（count 误读 cover 列）导致
  收藏夹计数错误
- 测试：40 → 92 项（主题资源/收藏夹/收藏夹树/通知/监控/重定位/旧版导入/设置）
- 门禁：Release 0 警告 0 错误

## [0.1.0] — M1 核心素材管理

- 数据层：SQLite（WAL）六张表（images / image_tags / collections /
  collection_images / image_embeddings / meta），状态原子写 + `.backup`
- 服务层（不引用 UI，ServiceRegistry 七参构造注入）：`DatabaseService`、
  `ImageInspectorService`（SixLabors.ImageSharp 3.1.x 元数据）、`ImportService`
  （递归扫描/格式白名单/隐藏文件过滤/跳过重复/进度与取消/导入报告）、
  `ThumbnailService`（两级分片目录 WebP + 占位图）、`ImageQueryService`
  （筛选/排序/搜索/分页计数）、`TagService`
- ViewModel 层：Shell/TopBar/Gallery/TagTree/Detail/StatusBar/Import，
  纯手写 MVVM，`AsyncRelayCommand` 接入 `CommandManager.RequerySuggested`
- W1/W2 界面：ShellWindow（左栏标签树、画廊网格+列表双视图与缩放、
  详情栏、状态栏、搜索框）、导入对话框（来源选择、选项、进度条、滚动日志、
  取消/完成）、Dark 主题与通用控件样式
- 虚拟化瓦片面板 `VirtualizingTilePanel`（IScrollInfo 自绘，按 IScrollInfo
  契约空库上报 `Size(0,0)`，禁止上报 `Size.Empty`——其值为负无穷）
- UIA 端到端实测：空库启动 → 导入 12 张 → 列表/网格切换 → 搜索过滤 0/恢复 12
  → 详情选中 → 硬杀进程；WAL 恢复完好，重开列出全部 12 张，优雅退出码 0
- 修复：空库启动 ArgumentException（IScrollInfo 上报 `Size.Empty`）；
  ImportDialog 缺少 `NullVis` 资源导致 XamlParseException；
  ProgressBar.Value 双向绑定只读属性抛 InvalidOperationException；
  AsyncRelayCommand 未接入 CommandManager 导致"开始导入"按钮永不启用
- 测试：40 项（数据层/导入/缩略图/查询/标签/VM，xUnit 纯 Assert）
- 门禁：Release 0 警告 0 错误；win-x64 ReadyToRun 自包含发布

## [0.1.0-M0] — M0 工程骨架

- 解决方案 `QL.World.sln`：`src/QL.World`（WPF, net10.0-windows）+ `tests/QL.World.Tests`（xUnit）
- 基础设施：`ObservableObject`/`RelayCommand`/`AsyncRelayCommand`、`AppInfo`
  （版本号唯一来自 csproj）、`AtomicTextFile`（原子替换 + `.backup` 恢复）、
  `AppDataRootResolver`（`%APPDATA%\Qianlan_App\QL_World\`）、
  `DebugUiTestMode`、`AppSettingsService`（config.json）、手写 `ServiceRegistry`
- W1 主窗口空壳：A1 顶栏 / B1 左栏 / B2 画廊 / B3 详情栏 / C1 状态栏分区占位，
  深色主题基线，PerMonitorV2 高 DPI，1400×850（最小 1100×700）
- 单实例互斥体 + 命名事件优雅重启；异常落盘 `logs\error.log`
- run 三件套（run.bat / run-hidden.vbs / run.ps1）；build 骨架（Prepare/Finalize）
- 工程文档：GEMINI.md / AGENTS.md / README.md / docs/UI_NUMBERING.md / PRD 基线
- 测试：14 项冒烟测试（数据根解析、原子写与备份恢复、设置持久化/钳制/损坏恢复、Shell VM）
