# Changelog

本项目遵循语义化版本（major.minor.patch）。里程碑：M0 骨架 → M1 核心素材管理 →
M2 组织与可靠性 → M3 AI 双轨 → M4 专业化与发布（v1.0.0）。

## [Unreleased]

## [1.1.0] — 2026-09-28 U1 UI 优化补丁

### U1-A：选中态边框色（§4.A.1）

- 网格卡片：GridCardTemplate 外层 Border 加 Style.Triggers，
  IsSelected 时 BorderBrush=Accent + BorderThickness=2（1px→2px 外描边）
- 列表行：ListRowTemplate 外层 Border 加 Style.Triggers，
  IsSelected 时 BorderThickness=3,0,0,1（左竖条+底线）+ Padding 8→5 抵消
  layout shift（内容位置不变，避免选中时横向抖动）
- 底色提示仍由 ListBoxItem 默认 AccentBg 提供（Generic.xaml 未改）
- 新增 UI 编号 B2-3-SEL（选中态视觉锚点）

### U1-B：§4.D.2 顶栏计数显式化（验证节）

- 经核实 B1 四类树节点 Count 绑定已落地（M1–M4 各阶段逐步实现）：
  TagTree.CategoryNodes / CollectionTree.CollectionNodes /
  TagTree.TopTagNodes / PbrGroupTree.GroupNodes
- ViewModel 通过 SQLite GROUP BY 查询填充 Count/MemberCount
- 本节不写新代码，仅文档同步：ui_optimization_target.md 标注状态；
  UI_NUMBERING.md B1-1/B1-2/B1-3/B1-4 行补「含子项计数显示」

### U1-C：底部快捷键提示（§4.E.1）

- StatusBarViewModel 新增 ShortcutHint 属性 + UpdateShortcutHint 方法，
  按上下文切换（空库/无选中/单选/多选）；仅显示 InputBindings 已绑定的
  快捷键，不引入新的（F2 重命名未绑定，暂不显示）
- ShellViewModel 订阅 Gallery.SelectionChanged 事件，选中态变化时
  更新 ShortcutHint；OnGalleryRefreshed 也同步更新（totalCount 变化）
- ShellWindow.xaml C1 状态栏拆为 C1-L 状态 / C1-C 计数 / C1-R 快捷键+AI 档位
- UI_NUMBERING 新增 C1-L/C1-C/C1-R 子编号

## [1.0.1] — 2026-09-24 补丁

### 修复

- VirtualizingTilePanel 在 Grid↔List 视图切换时偶发 NRE 崩溃：
  ItemsPanel 切换期被换下的旧面板仍会收到 pending Measure/Arrange，
  此时 `ItemContainerGenerator` 已被新面板接管返回 null，
  直接调用 `GeneratorPositionFromIndex` 抛 NRE 且
  DispatcherUnhandledException Handled=false 直接崩进程。
  MeasureOverride/ArrangeOverride 在取 generator 后判 null 提前 return，
  CleanupContainers 用捕获的局部 generator 而非重读属性

### 改进

- 主题对比度达 WCAG AA 基线：
  - TextMuted Dark #6E6E7A → #9696A2（对 Surface 6.1:1，对 SurfaceVariant 5.5:1）
  - TextMuted Light #9A9AA6 → #6E6E78（对 Surface 5.0:1，对 SurfaceVariant 4.5:1）
  - Light Warning #B07700 → #8F6400（对 Surface/SurfaceVariant/WarningBg 均 ≥ 4.5:1）
- 缩略图遮罩统一走主题 OverlayBrush：
  Dark 新增 Overlay=#CC17171C；Light 新增 Overlay=#CCFFFFFF；
  ShellWindow.xaml 缺失态遮罩从硬编码 `#CC17171C` 改为 `DynamicResource OverlayBrush`；
  后续新增遮罩态一律走 OverlayBrush，禁止硬编码 hex

## [1.0.0] — M4 专业化与发布

### M4-A：PBR 贴图成组（schema v2）

- 数据层 schema v2：新增 `pbr_groups` / `pbr_group_images` 两表
  （`role` 字段标识 basecolor/normal/roughness/metallic/ao/height/other），
  `PRIMARY KEY (group_id, image_id)` + `idx_pbr_group_images_image` 索引；
  `DbMigrator.CURRENT_VERSION` 1 → 2，`Schema.v2.sql` 嵌入资源
- `Services/Pbr/PbrRuleSet.cs`：first-match、大小写不敏感的尾缀规则集
  （`BaseColor|Albedo|Diffuse|_D` → basecolor，`Normal|_N` → normal，
  `Rough` → roughness，`Metal` → metallic，`AO|Occlusion` → ao，
  `Height|Bump|Disp` → height）；剥尾部匹配段得基础名
- `Services/Pbr/PbrGroupingService.cs`：`RescanAllAsync` 全库重扫
  （按「目录 + 基础名」分组，≥2 张才成组；事务化全量重建 + 空组清理），
  `ScanNewImportsAsync` 导入钩子增量路径；规则从 AppSettings 配置读取，
  AtomicTextFile 原子写 + 损坏回退默认规则
- `Services/ImportService.cs` 批量落库成功后调 `ScanNewImportsAsync`，
  异常不阻断导入
- 测试：规则匹配（命中/大小写/优先级/无后缀裸图）、单图不成组、
  跨目录同名不成一组、RescanAll 重建幂等、空组清理、规则损坏回退默认、
  迁移 v1→v2 幂等

### M4-B：B1-4 贴图组树 + W3/A6 规则编辑

- `ViewModels/PbrGroupTreeViewModel`：复刻 CollectionTreeViewModel 模式，
  分组节点列表 + 点击切换筛选 + 再点清除 + 节点显示成员数
- `ViewModels/PbrRulesViewModel`：A6 页签规则列表增删改
  （Pattern/Role/Enabled/删除）、添加规则、保存并重建、仅重建、恢复默认、
  状态文本
- `Models/LibraryFilter` 新增 `PbrGroupId`；
  `ImageQueryService.AppendClauses` 新增
  `EXISTS (SELECT 1 FROM pbr_group_images ...)` 分支（语义分支优先级约定）
- W1 XAML（B1 区）B1-4「贴图组」区块（ItemsControl 列组名 + 成员数）；
  W3 XAML 新页签 A6「贴图组规则」
- `docs/UI_NUMBERING.md`：新增 B1-4（B1-4-1 起）与 W3/A6 及其叶子编号
- 测试：点组名画廊只显示组成员；规则编辑保存后重扫生效；
  config.json 含 .backup；12 项新测试

### M4-C：Magick.NET 专业格式接入

- 新增依赖 `Magick.NET-Q8-AnyCPU` 14.17.1（Q8 非 HDRI——自动 8bit，
  AnyCPU 与现有 win-x64 发布兼容）
- `Services/MagickDecoder.cs`（internal）：封装 Magick 读取，
  `TryReadMetadata`（宽高/格式名）+ `TryLoadFrame`（返回 8bit RGBA 像素；
  PSD 用 `MagickImageCollection` flatten 取合成帧）
- `Services/ImageInspector.cs`：ImageSharp `Identify` 抛
  `UnknownImageFormatException` 时 fallback 到 `MagickDecoder`；
  导入白名单扩展 tga/exr/hdr/psd（代码真源还含 tif/tiff）
- `Services/ThumbnailService.cs` `ProcessJob`：`Image.Load` 失败时
  走 `MagickDecoder.TryLoadFrame` → `Image.LoadPixelData` →
  复用现有 WebP 缩略图管线（命名/目录约定不变）
- 测试：用 Magick 生成 TGA/EXR/HDR/PSD 样例 → 元数据正确 + 缩略图成功；
  损坏文件两解码器都失败 → 导入跳过计数正确；ImageSharp 原格式回归；
  12 项新测试

### M4-D：10 万级压测与服务端分页优化

- 服务端分页：`LibraryFilter` 新增 `Limit`/`Offset`，
  `ImageQueryService` 实现 `LIMIT/OFFSET` + `CountFiltered`；
  `GalleryViewModel` `PageSize=1000` 增量加载；
  `ShellWindow` endless scroll 触发翻页；schema v3 复合索引
- 压测工具 `tools/StressGen/`（console 工程，不入主 sln 发布链）：
  参数化生成合成库（`--count 100000 --out <dir>`），直插 SQLite
  + 程序化生成真实小尺寸缩略图文件
- 优化前后对比：万级画廊首次绘制 598ms → 4.7ms（≈127×）
- 冒烟回收（M3 遗留）：`tools/smoke/UiSmokeB1.ps1` 覆盖
  W1/B2-3-CM1 右键菜单 + W1/B3-8 找相似按钮（子代理未产出，deferred
  不阻塞验收）
- 测试：6 项新测试，总计 251 全绿

### M4-E：用户手册

- `docs/USER_GUIDE.md`：面向非技术最终用户的中文手册，12 章覆盖
  安装/导入/画廊/筛选搜索/详情栏/AI 三档/语义搜索/设置窗口/消息中心/
  数据安全/常见问题；43 处 UI 编号交叉引用（W1–W3 全部用户可见功能），
  与 `docs/UI_NUMBERING.md` 真源逐项核对；导入白名单按代码真源补列 TIFF

### M4-F：Finalize 五件套发布工具 + 契约测试

- `tools/ReleaseTool/`（Exe 工程，net10.0-windows，加入 sln）：
  `ReleaseArtifacts.Produce` 实现 GEMINI.md §7 五件套契约——
  app.zip / source.zip(git archive) / 更新公告.txt / 使用说明.txt /
  manifest.json 最后原子落地（tmpDir → Directory.Move）；
  dist 永不覆盖；manifestSha 计算顺序：先序列化（manifestSha256 字段
  null 被忽略）→ 算 SHA → 回填 → 重写；返回 manifest 文件自身 SHA
  用于 annotated tag message
- `build.ps1` Finalize 实现：工作区干净校验 → 复用 `Invoke-PrepareGate`
  门禁 → 构建 ReleaseTool Exe → 调用解析 `MANIFEST_SHA` →
  打印 annotated tag 命令（只读 Git 不执行）。Prepare 重构为
  `Invoke-PrepareGate` 函数复用
- `tests/QL.World.Tests/Release/ReleaseContractTests.cs`：8 项契约测试
  覆盖五件套齐全/命名规范/manifest schema/SHA 一致/重复发布报错/
  DryRun 无残留/app.zip 含 staging 内容/注入 source.zip/失败清理 tmp
- `docs/release/RELEASE_NOTES.md`：更新公告模板（`v{x.y.z}` 占位）
- PS 5.1 兼容：Exe 子进程调用避开 `Add-Type` 加载 .NET 10 程序集的
  不兼容
- 验证：dotnet build 0 警告；dotnet test 259 全绿（含 8 项新契约测试）；
  CLI dry-run 端到端 EXIT=0、五件套齐全、manifest sha256 输出正确

### M4-Z：收尾与真实发布

- `QL.World.csproj` `<Version>` 0.5.0 → 1.0.0
- `GEMINI.md` 里程碑状态更新为「M0–M4 全部完成，v1.0.0 已发布」
- `docs/UI_NUMBERING.md` 全表冻结走查（B1-4 / W3/A6 已实现状态确认）
- 门禁：`build.ps1 -Mode Prepare` 0 警告 + 全测试绿 + R2R publish
- 真实发布：`build.ps1 -Mode Finalize` 产出 dist/ 五件套 +
  annotated tag `v1.0.0`（消息含 `release-manifest-sha256:`）
- staging 体积对比记录（0.5.0 → 1.0.0）

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
