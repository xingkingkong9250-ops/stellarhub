# QL World（C# WPF .NET 10 版）产品需求与重写实施方案

> 文档性质：先产品需求（用户视角），后技术方案（工程视角）。
> 版本：v1.0，已评审批准，作为 M0–M4 实施基线；后续变更走 docs/superpowers 变更记录。
> 标准对标：QianLan_YinWu v1.10.1（GEMINI.md 为规范真源）。项目规范真源见根目录 `GEMINI.md`。

---

# 第一部分　产品需求

## 1. 产品定位

- **产品名**：QL World —— 3D 美术图片素材管理系统
- **目标用户**：建模师、材质师、概念设计师、灯光师等需要管理海量参考图/贴图/概念图的个人美术工作者
- **核心问题**：素材散落在各文件夹，找不到、重复存、无法按内容/风格/颜色检索
- **核心价值**：导入即去重、一屏浏览万图、标签+评分+收藏组织素材、可选 AI 自动打标与"以文搜图"
- **形态**：Windows 10/11 原生 WPF 桌面应用，本地单文件数据库，**默认完全离线可用**，AI 为用户可选能力

## 2. 典型使用场景

1. 新建素材库：把几个 TB 级参考图文件夹拖进来，自动扫描去重、后台生成缩略图，期间界面不卡。
2. 找参考：按左侧"材质参考"分类或"metal"标签缩小范围，再用文件名搜索；或输入"生锈的金属表面"直接语义搜图。
3. 整理：给选中图打星级、加备注、拖入"角色三视图"收藏夹；删掉 AI 误判的标签。
4. 增量维护：监看的文件夹里存入新图自动入库；源文件被移走时标记"缺失"而不是悄悄消失。
5. 换电脑：拷贝 `%APPDATA%` 下数据目录或导出库即可恢复。

## 3. 功能需求（窗口与控件级）

### 3.1 编号总则

- W = 窗口；W1 下 A = 顶部页签/横区、B = 主分区、C = 底部区；编号再往下定位到控件（如 B3-3）。
- 编号是位置锚点：功能改了要核对编号；删除的编号作废不复用；新增顺延。编号表维护在工程内 `docs/UI_NUMBERING.md`，每次 UI 变更同步。

### 3.2 W1 主窗口

默认尺寸 1400×850，最小 1100×700，PerMonitorV2 高 DPI，默认深色主题（可切换浅色）。

**A1 顶栏（高 56px，左→右）**

| 编号 | 控件 | 交互与行为 |
|---|---|---|
| A1-1 | 「导入」按钮 | 打开 W2；导入进行中按钮置灰并显示转圈 |
| A1-2 | 「AI 分析」按钮 | AI=关闭/未就绪时置灰，悬停提示原因（未启用/模型缺失/未配置）；可用时弹出批量分析确认框（显示待处理数量），进行中变为进度文案可取消 |
| A1-3 | 视图切换 | 网格/列表两个分段按钮，选中态高亮；两种视图共享同一数据源与排序 |
| A1-4 | 搜索框 | 占位符"搜索素材…"；清空按钮；停止输入 300ms 自动搜索，回车立即搜索 |
| A1-5 | 搜索模式 | 下拉：标签 / 文件名·备注 / 语义图搜；选"语义"但 AI 不可用时给出气泡说明并自动回退文本搜索 |
| A1-6 | 通知铃铛 | 有未读任务消息（导入完成、AI 完成、文件缺失）显示小红点；点击打开消息列表面板（非假弹窗） |
| A1-7 | 设置齿轮 | 打开 W3 |

**B1 左栏（宽 220–300，可拖拽调宽，上下两段）**

- B1-1 素材库树：全部(N) + 10 个一级分类（贴图/纹理、实拍参考、概念设计、雕刻参考、材质参考、环境/场景、灯光参考、配色参考、UI/HUD、动画参考）+ 未分类(N)；分类计数实时刷新；单击筛选画廊，再次单击同一节点取消筛选。
- B1-2 收藏夹树：列出全部收藏夹及数量；右键菜单：新建/重命名/删除（删除前确认，只删关联不删图片记录）；支持把画廊图片拖到收藏夹节点完成加入。
- B1-3 高频标签：按使用次数倒序 Top 50，显示"标签 (n)"，单击按标签筛选。
- 筛选态：B1 筛选条件与 A1 搜索为**叠加（AND）**关系，左栏顶部出现"已筛选：× 清除"芯片。

**B2 画廊（中心区，占据剩余空间）**

- B2-1 计数条："共 N 张 / 当前筛选 M 张"。
- B2-2 排序下拉：导入时间↓↑、文件名 A-Z、评分↓、文件大小↓——**必须真实生效**；选择持久化到设置。
- B2-3 图卡网格/列表（UI 虚拟化，仅为可视区创建控件）：
  - 网格卡片：缩略图（默认 200px）、文件名、评分星、AI/手动标签角标；悬停浮起，选中紫色描边；缺失文件显示灰色"文件缺失"遮罩。
  - 列表行：小缩略图、文件名、尺寸、大小、分类、评分、标签摘要、导入时间。
  - 单击=选中并刷新 B3；双击=用系统默认程序打开原图；Ctrl+点击多选、Ctrl+A 全选。
  - 右键菜单见 3.5。
  - 缩略图后台异步生成，未到货显示占位块；可视区请求优先。
- B2-4 缩放条：80–512px 滑杆 + 数值，松手 150ms 防抖重排；网格/列表各自记住尺寸。
- 大数据量：5 万张以内滚动 60fps 为设计目标（M4 压测 10 万）。

**B3 详情栏（右，宽 280–400）**

| 编号 | 区块 | 行为 |
|---|---|---|
| B3-1 | 大图预览 | 显示缩略图大图；双击预览用系统看图器打开原图；预览区右上角"在资源管理器中定位"按钮 |
| B3-2 | 文件信息 | 文件名、宽×高、格式、大小、完整路径（可复制）、导入时间；文件缺失时整区黄色警示并提供"重新定位…"（选择新路径后更新记录） |
| B3-3 | 评分 | 1–5 星，点击同一星可取消为 0；**立即落库**，改选图片不丢值 |
| B3-4 | 主色调 | 最多 5 个色块（来自颜色分析）；未分析时显示"分析后显示"，不显示假数据 |
| B3-5 | 标签 | chip 流式排列，AI 标签与手动标签视觉区分（如 AI 标签加虚框/来源 tooltip）；每个 chip 可 × 删除（AI 标签删除后在同次库生命周期内不再被自动加回）；底部输入框回车添加手动标签，支持一次粘贴多个（逗号/顿号分隔） |
| B3-6 | 分类 | 下拉可手动修改一级分类（含清空为未分类），立即落库 |
| B3-7 | 备注 | 多行文本；失焦或停止输入 800ms **自动保存**，保存成功显示瞬时"已保存"提示；杜绝 Python 版只编辑不保存的问题 |

多选时 B3 进入批量态：仅显示可批量操作项——加标签、移除标签、设分类、设评分、加收藏夹，并显示"已选 N 张"。

**C1 状态栏**：就绪/后台任务进度（"生成缩略图 123/1000"）/筛选结果数/AI 档位（关闭·本地·云端）/库总大小。

### 3.3 W2 导入对话框（520×420）

- 「选择文件夹」「选择文件」两个入口；选定后显示路径与扫描到的图片数。
- 选项：包含子文件夹（默认开）、跳过隐藏文件（默认开）、格式过滤复选组（jpg/png/bmp/gif/webp/tiff/tga）。
- 「开始导入」后：进度条 + 当前文件 + 新增/跳过（重复或损坏）计数 + 可滚动日志；「取消」随时生效（以文件为粒度）。
- 结束：显示"新增 N 张，跳过 M 张"，按钮变为「完成」；关闭后 W1 自动刷新画廊与计数。
- 重复打开对话框不得累积事件回调（Python 版缺陷）。

### 3.4 W3 设置窗口（页签式）

- **A1 常规**：主题（深色/浅色，切换即时生效）；启动时检查库文件完整性（开关）；库存储位置（只读展示 + "在资源管理器中打开"）。
- **A2 缩略图**：边长 128–512、WebP 质量 30–100；保存后提示"将对现有缩略图重建"，确认后后台重建并有进度；「立即清空缩略图缓存」按钮（二次确认）。
- **A3 AI**（详见第 4 节）。
- **A4 监控目录**：目录列表 + 添加/移除；开关"自动导入新文件"；移除目录只停止监控不删数据。
- **A5 关于**：产品名、版本（从 Assembly 自动读取，禁止手填）、数据目录、各库统计（图片/标签/收藏/向量数）、「从旧版(Python)导入…」入口、「导出诊断日志」按钮。
- 所有设置点保存才生效（提供取消）；写盘采用原子替换并留 .backup。

### 3.5 全局右键菜单与快捷键

- 画廊图片右键：在资源管理器中定位、用系统默认程序打开、复制路径、添加到收藏夹（子菜单含新建）、编辑标签（批量弹窗）、设置分类（子菜单）、评分（子菜单）、重新生成缩略图、AI 分析所选、从库中移除（**仅删记录不动磁盘文件**，二次确认，文字明确说明）。
- 标签 chip 右键：删除该图片此标签 / 全库删除此标签（二次确认，显示影响数量）。
- 收藏夹节点右键：重命名/删除/设封面。
- 快捷键：Ctrl+I 导入；Ctrl+F 聚焦搜索；Delete 从库移除所选；Ctrl+1..5 评分；Ctrl+D 打开详情定位；F5 刷新；Ctrl+, 设置。
- 所有破坏性操作必须可撤销或二次确认；"从库移除"在本次会话内提供撤销提示条。

### 3.6 反馈与通知（A1-6）

- 消息中心条目：导入完成、缩略图批量完成、AI 批量完成/失败汇总、监控新增、文件缺失批量告警；点击条目可定位到相关图片/目录。
- 后台任务一律有进度、可取消、不弹模态打断；错误用状态栏+消息中心，严重错误才弹对话框。

## 4. AI 双轨需求（用户可选）

- 设置 A3 提供三档单选：**关闭 / 本地模型（离线）/ 云端服务**，切换即时生效不重启；默认关闭。
- **本地模型**：
  - 首次启用引导下载模型文件（显示体积、来源、许可、SHA-256 校验、进度条、断点重试、支持手动选择已下载文件）；模型存数据目录 models\。
  - 功能：自动一级分类 + 风格/题材/材质/色调标签（沿用 10 分类与四类标签体系）、图文向量、语义搜图、以图搜相似图。
  - 全程离线，不产生任何网络请求（除用户主动点下载）。
- **云端服务**：
  - 配置：base_url、API Key、模型名（提供 OpenAI 兼容说明与预设模板）；「测试连接」按钮；Key 用 Windows DPAPI 加密存储，不写明文、不进日志。
  - 界面明确告知"图片将上传至所选服务，请确认合规"，仅允许 HTTPS；可只启用云端打标而语义检索仍用本地（能力按服务声明自动开关）。
- **共性**：
  - AI 为纯增量能力：关闭/未下载模型/断网/鉴权失败时，图库、导入、标签、收藏等基础功能 100% 正常，仅 AI 入口置灰并说明原因（修复 Python 版"一依赖缺全挂"）。
  - 批量分析在后台运行（默认每次处理 100 张待处理，可取消），结果写库后刷新；AI 标签带来源与置信度，用户可删改；**手动标签永不被 AI 覆盖**。
  - 颜色分析不依赖 AI 档位，导入后即可手动/自动运行。

## 5. 非功能需求

- 性能：万张级库首屏 ≤2s（热缓存）；导入吞吐以磁盘为准且 UI 全程可响应；滚动 60fps；语义搜图千级结果 <100ms、万级 <500ms。
- 可靠性：设置/元数据原子写 + .backup，损坏自动从最近可解析快照恢复；SQLite WAL；异常杀进程不丢已提交数据、不损坏库。
- 可维护：核心 Services 全部可单测（不依赖 UI）；Release 0 警告；xUnit 测试密度对标隐坞（321 测试覆盖 Services/契约层）。
- 部署：Self-Contained win-x64 + ReadyToRun 单包，目标机免装运行时；保留正式版五件套与 dist 历史。
- 安全：仅云端档在用户配置后联网；Key 加密；默认不收集任何遥测。
- 兼容：Windows 10 21H2+ / 11；图片格式 M1 支持 jpg/jpeg/png/bmp/gif/webp/tiff/tga；M4 评估 EXR/HDR/PSD。

## 6. 明确不做（本方案范围外）

- 不做图片编辑/修图（只管理与查看）；不做云同步/多端账号；不做团队协作与权限；不做视频素材；不引入 WebView2/Electron/任何 HTML UI。

---

# 第二部分　技术方案（严格对标隐坞规范）

## 7. 工程结构与规范

新建根目录 `c:\AI_Programming\APP\QL_World_dotnet\`；Python 版 `QL_World\` 冻结为参照，不再修改。

```
QL_World_dotnet/
├─ QL.World.sln
├─ nuget.config                 # 固定 nuget.org（同隐坞）
├─ GEMINI.md                    # 项目规范真源（本文件技术部分并入）
├─ AGENTS.md                    # 指向 GEMINI.md；开始工作前检查 .agents/
├─ README.md  CHANGELOG.md  .gitignore
├─ run.bat  run-hidden.vbs  run.ps1
├─ build.bat  build.ps1
├─ tools/release/               # 仅本地发布逻辑（ReleaseContract.psm1 同构）
├─ docs/
│  ├─ USER_GUIDE.md
│  ├─ UI_NUMBERING.md           # W/A/B/C 编号表（唯一真源）
│  └─ superpowers/{specs,plans,reports}/
├─ tests/
│  ├─ QL.World.Tests/           # xUnit，文件命名 {Service}Tests.cs
│  └─ release/                  # 发布契约测试
└─ src/QL.World/
   ├─ QL.World.csproj           # net10.0-windows; UseWPF; 不开 WinForms
   ├─ app.manifest  App.xaml  App.xaml.cs
   ├─ Assets/
   ├─ Themes/ (Generic.xaml Dark.xaml Light.xaml Converters.cs)
   ├─ Models/      (ImageRecord, ImageTag, TagSource, Collection, ColorSwatch,
   │                AppSettings, AiProviderKind, AiProviderSettings…)
   ├─ Data/        (Schema.sql, DatabaseService, DbMigrator)
   ├─ Services/    (见 7.3)
   ├─ Services/Ai/ (IImageAiService, OnnxAiService, CloudAiService, AiServiceFactory,
   │                VectorSearchService)
   ├─ Helpers/     (ObservableObject, RelayCommand, AsyncRelayCommand, Md5Hasher,
   │                PathHelpers, Debouncer, PinyinHelper[M2 中文搜索可选])
   ├─ ViewModels/  (ShellViewModel, GalleryViewModel, DetailViewModel,
   │                TagTreeViewModel, CollectionsViewModel, ImportViewModel,
   │                SettingsViewModel, NotificationListViewModel…)
   └─ Views/       (ShellWindow.xaml, ImportDialog.xaml, SettingsWindow.xaml,
                    分区 UserControl：TopBar/TagTreePanel/CollectionsPanel/
                    GalleryPanel/DetailPanel/StatusBar/NotificationPopup)
```

### 7.1 必须复刻的隐坞约定
- 纯 MVVM、**不引第三方 MVVM 框架**；DI 采用隐坞同款手写 `ServiceRegistry`（聚合所有服务、构造注入），不引入容器包；ViewModels 继承自移植的 ObservableObject。
- Services/ 纯逻辑、不引用 UI 类型（图像产出用自己的结构/字节，仅在 ViewModel 转 BitmapSource）；P/Invoke（如 DPAPI、Shell 定位）全部隔离到 Interop/。
- 命名：命名空间 `QL.World.{Folder}`；类 PascalCase；私有字段 `_camelCase`；常量 UPPER_SNAKE_CASE。
- 数据根目录：`%APPDATA%\Qianlan_App\QL_World\`（qlworld.db、thumbnails\、models\、config.json(+ .backup)、logs\）。
- JSON 配置/状态一律 AtomicTextFile 原子替换并保留 .backup，损坏只从可解析快照恢复，禁止静默覆盖唯一副本。
- Debug 隔离变量：`QL_WORLD_DATA_ROOT`（数据目录）、`QL_WORLD_UI_TEST_MODE`（自动化定位/单实例锁隔离）、`QL_WORLD_BUILD_OUTPUT`；Release 不启用。
- run 三件套行为同隐坞：无控制台、自动构建、单实例、已有实例优雅退出后再启动；日志 `%TEMP%\QL_World.run.log`。
- 版本号唯一来源 csproj `<Version>`，全程 major.minor.patch，脚本禁硬编码；UI 版本文本从 Assembly 读取。
- 发布：build.ps1 只读 Git 不做写操作；Prepare/Finalize 两阶段、annotated Tag 含 `release-manifest-sha256:`、Manifest 最后原子落地；五件套命名 `QL World_v{x.y.z}.zip / _source.zip / _更新公告.txt / _使用说明.txt / _manifest.json`；dist/ 永不覆盖。
- 不进版本控制：bin/ obj/ dist/ *.user .vs/。

### 7.2 技术选型
| 需求 | 选型 | 说明 |
|---|---|---|
| 数据库 | Microsoft.Data.Sqlite | 单长连接 + WAL + PRAGMA user_version 迁移 |
| 图像解码/缩略图 | SixLabors.ImageSharp | 自管 WebP 编解码，不依赖系统 WIC 扩展 |
| M4 专业格式 | Magick.NET（定点、封装在 FormatDecoder 后） | TGA/EXR/HDR/PSD，M4 再引 |
| AI 离线 | Microsoft.ML.OnnxRuntime（CPU）+ CLIP ViT-B-32 ONNX | 模型选型先做 Spike（许可/来源/体积） |
| AI 云端 | 裸 HttpClient + System.Text.Json | OpenAI 兼容 /chat 多模态、/embeddings |
| 向量检索 | 自写 VectorSearchService（System.Numerics SIMD 余弦） | ≤5 万免原生依赖；超量再评估纯托管 HNSW |
| 配置加密 | Interop 封装 DPAPI（ProtectedData） | 云端 Key |
| 测试 | xUnit + FluentAssertions | 同隐坞测试栈 |

## 8. 数据设计

### 8.1 Schema（user_version = 1）
- **images**：id、file_path UNIQUE、file_name、sortable_name、file_hash（索引）、file_size、width、height、format、rating(0–5)、notes、primary_category、dominant_colors(JSON)、is_missing、created_at、updated_at。
- **image_tags**：(image_id, tag) PK、source∈('ai','manual')、confidence、created_at；UPSERT 规则：已存在 manual 行时 ai 写入不得降级覆盖（修 Python 版 INSERT OR REPLACE 缺陷）。
- **collections**：id、name UNIQUE、description、cover_image_id、created_at、updated_at。
- **collection_images**：(collection_id, image_id) PK、sort_order。
- **image_embeddings**：image_id PK、model_id（换模型可作废）、dim、embedding BLOB（小端 float，自带头，不用 pickle）、created_at。
- **meta**：key/value（schema 版本、向量模型 id、索引状态等）。
- 索引：hash、category、rating、image_tags.tag、file_name 排序按需建。

### 8.2 旧版数据
- M2 在 W3/A5 提供"从旧版导入"：读 Python 版 SQLite 的 images/tags/collections；embedding/dominant_colors 不迁、由新管线重算；导入走正常去重路径。当前旧库仅 8 条测试数据，不做专门兼容包袱。

## 9. 关键服务设计

1. **DatabaseService**：单连接、事务包装、参数化（禁字符串拼 SQL）、仓储方法返回 Model；DbMigrator 启动时按 user_version 顺序升级，每个版本一个幂等迁移。
2. **ImportService**：IAsyncEnumerable 扫描 → hash 集合去重 → 元数据提取（损坏文件计入跳过）→ 50/批事务；IProgress 报三件套（当前/总数/文件名），CancellationToken 以文件为粒度取消；完成后把"待生成缩略图"入队。
3. **ThumbnailService**：有界并发后台队列（默认 2）、可视区优先调度、LRU 内存缓存、`thumbnails\<hash2>\<hash>.webp`；设置变更触发受影响项重建；磁盘失败返回占位图并告警。
4. **ImageQueryService**：分类/标签/收藏/文本/语义组合筛选（AND），服务端分页+排序，供虚拟化画廊按页取数；LIKE 参数化。
5. **TagService / CollectionService**：标签增删（manual 保护）、全库删标签（返影响数）、收藏夹排序与封面。
6. **ColorAnalysisService**：纯 C# K-Means（k=5，像素采样上限可配），输出 RGB 色块 + 冷暖/对比等标签；无外部依赖、可单测（固定样例图）。
7. **FileWatcherService**：FileSystemWatcher + 2s 防抖，新增→自动导入，删除/改名→is_missing 标记 + 通知聚合；监控目录可热插拔。
8. **AI 层**：IImageAiService 声明 Capabilities（打标/图向量/文向量/相似图）；Factory 按设置产出 Disabled/Onnx/Cloud 实例，切换即重建；VectorSearchService 建索引、持久化、余弦 TopK；AI 写标签遵守 manual 保护与"用户删除不自动加回"（会话内抑制集，M2 评估是否落库）。
9. **AppSettingsService / AtomicTextFile**：移植隐坞实现；AppSettings 强类型化所有配置项（主题、缩略图、AI 三档及子配置、监控目录、排序记忆）。
10. **NotificationService**：会话消息中心（A1-6），后台任务完成/缺失文件聚合提示。

## 10. 里程碑与完成定义（不含工时）

**M0 工程骨架**
- [ ] sln/src/tests 建立，NuGet 还原通过；GEMINI/AGENTS/README/CHANGELOG、run 三件套、build 骨架、nuget.config、.gitignore 就位
- [ ] 移植 Helpers(ObservableObject/RelayCommand)、AppDataRootResolver、AtomicTextFile、AppSettingsService、ServiceRegistry、Themes 基线
- [ ] W1 空壳（仅标题/版本号/主题）可 run.bat 静默启动；Release 0 警告；测试工程跑通冒烟测试

**M1 核心素材管理（产品级）**
- [ ] Schema+迁移+DatabaseService（单测：CRUD/事务/标签 UPSERT 语义/迁移幂等）
- [ ] ImportService+W2（单测：去重、损坏跳过、取消、递归选项）
- [ ] ThumbnailService（单测：命中/重建/并发/损坏图占位）
- [ ] W1 三栏：虚拟化画廊（网格+列表双模板、缩放、排序真实生效）、B1 树（含未分类/叠加筛选/清除芯片）、B3（评分/标签/分类/备注自动保存/信息/缺失态）、C1
- [ ] 标签+文本搜索（防抖/叠加筛选）、多选批量操作、右键菜单与快捷键、从库移除（可撤销提示）
- [ ] 验收：千张样例导入滚动流畅；杀进程后数据完好；全部测试绿

**M2 组织与可靠性**
- [ ] 收藏夹全套（服务单测+树+拖拽+封面+重命名/删除）
- [ ] FileWatcher 自动导入与缺失处理（重新定位）、W3/A4
- [ ] W3 全部页签生效（A1/A2/A5）、消息中心、浅色主题、旧版导入器
- [ ] UI_NUMBERING 全表冻结并逐项走查；CHANGELOG 首版

**M3 AI 双轨**
- [ ] 抽象+Factory+A3 设置（三档切换、连接测试、Key DPAPI、模型下载向导含校验）
- [ ] 纯 C# 颜色分析与 B3-4
- [ ] VectorSearchService（假向量单测）+ 语义搜索接线 + 以图搜相似
- [ ] OnnxAiService（Spike 通过后）、CloudAiService（OpenAI 兼容）
- [ ] 批量分析窗口（进度/取消/统计/失败汇总）；manual 保护与删除抑制验证
- [ ] 验收：关闭/断网/鉴权失败/模型损坏四种异常下主功能全正常

**M4 专业化与发布**
- [ ] PBR 贴图成组（规则可编辑+单测）、TGA/EXR/HDR/PSD 评估接入
- [ ] 10 万级压测与优化；docs/USER_GUIDE
- [ ] Prepare/Finalize 全流程演练（含契约测试），dist 发布 v1.0.0 五件套

## 11. 验证策略

- 门禁：每里程碑 `dotnet build -c Release` 0 警告 0 错误；`dotnet test`（含 tests/release 契约）全绿。
- 核心服务必须有 {Service}Tests.cs：Database/Import/Thumbnail/ImageQuery/Tag/Collection/ColorAnalysis/VectorSearch/AppSettings/AtomicTextFile/PbrGrouping/FileWatcher；AI 网络与模型用接口假对象单测，不依赖外网。
- 异常路径手测脚本化：断网、源文件删除、缩略图目录清空、config.json 损坏恢复、模型 SHA 不符、云端 401/超时、监控目录热插拔。
- 数据安全演练：任务进行中强杀进程 → 重启校验库完整与备份恢复；Release 包在 QL_WORLD_DATA_ROOT 指向空目录下首次启动验收。

## 12. 风险与对策

| 风险 | 对策 |
|---|---|
| CLIP ONNX 来源/许可不确定 | M3 先做选型 Spike；不通过则先发布云端档+颜色分析，离线档顺延 |
| ImageSharp 不支持 EXR/HDR/PSD | 封装 IImageDecoder，M4 用 Magick.NET 定点接入，主链路不污染 |
| 云端协议碎片化 | 只承诺 OpenAI 兼容协议 + 预设模板，不逐家做 SDK |
| 万图以上 UI 性能 | M1 即虚拟化+分页+异步缩略图，不把性能债留到后期；M4 压测 |
| 范围蔓延 | M1/M2 不碰 AI，M3 不改基础布局；每阶段冻结编号表；超范围重新评审本文件 |
