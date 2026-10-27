# Changelog

本项目遵循语义化版本（major.minor.patch）。里程碑：M0 骨架 → M1 核心素材管理 →
M2 组织与可靠性 → M3 AI 双轨 → M4 专业化与发布（v1.0.0）。

> **版本重编码说明（2026-09-30）**：自 0.0.1 起启用新版本编码规则。
> 历史 0.1.0–1.6.0 条目保留为开发记录，不再作为版本序列参考。
> 新规则：小修改 +0.0.1；新增功能/里程碑 +0.1.0；大版本 1.0.0 需用户确认。

## [Unreleased]

## [0.16.0] — 2026-10-27 图片查重 DUP-A~C：感知哈希与全库扫描

### 新增

- **感知哈希（dHash 64 位）**：新增 `PerceptualHashService`，解码后拉伸降采样
  到 9×8 灰度，逐行比较相邻列得到 64 位哈希；转码/缩放/调色后的同一张图
  汉明距离稳定在 ≤10，作为近重复检测 L1（严格同图）判定基础。
- **导入落库 phash**：导入流水线在元数据探测通过后计算 phash 并写入
  `images.phash`（16 位 hex）；读取或解码失败只留 NULL，不影响导入本身，
  远端 NAS 与本地同源。
- **解码能力扩展**：`IMagickDecoder.TryLoadFrame(Stream)` 新增流重载，
  TGA/PSD/EXR/HDR 等 ImageSharp 不支持的格式也能在 NAS 流上计算哈希。
- **导入时 L1 查重**：导入批次入库后用「库内 + 本次已入库」的 phash 索引
  取汉明距离最优命中（≤10），命中只写 W2 导入日志
  （`疑似重复（距离 N）：源 ↔ 库内`）并发布通知中心
  `NotificationKind.DuplicateDetected`，照常入库不拦截；同批与跨批新图
  可互查，无命中时零日志行、零通知。文件监控自动导入同样发布该通知。
- **全库查重扫描**：新增 `DuplicateScanService`，后台任务可进度上报与取消。
  L1 用内存 BK-tree 对全库 phash 做汉明半径查询（≤10）；L2 读取
  `image_embeddings`，仅在同 `model_id` 内按 256 条分块计算余弦（≥0.92）。
  候选对取传递闭包聚类，每组成员写一行 `duplicate_candidates`（pending）；
  新一轮扫描先清 pending 旧行，resolved/dismissed 历史保留，取消不写候选表。
- **存量 phash 补算**：扫描前对 `phash` 为空的图片按
  `thumbnails\<hash2>\<hash>_thumb.webp` 补算并回写；缩略图缺失或解码失败
  只计入 `ImagesWithoutPhash`，不中断扫描。

### 数据

- schema v12：`images` 新增 `phash` 列；新增 `duplicate_candidates` 表
  （组/图/类型/层级/得分/状态）及两个索引，为后续全库扫描与审核窗口预留。
  存量行 phash 保持 NULL，由全库查重扫描时从缩略图补算。

### 测试

- 新增 10 项单元测试：转码/缩放稳定性（距离 ≤10）、不同内容区分度
  （距离 >10）、损坏字节与缺失文件返回 NULL、hex 往返、汉明距离、
  导入后 phash 落列且与文件直算一致。
- 新增 5 项导入查重测试：库内命中与同批命中、异图零命中、命中不拦截
  入库、W2 命中通知与无命中零通知。
- 新增 9 项全库扫描测试：BK-tree 半径查询与暴力枚举一致、阈值边界、
  重复 imageId 去重；L1 链式传递闭包成组、L2 同 model_id 隔离、重扫
  pending 清理与历史保留、缩略图 phash 补算与缺失计数、进度阶段、取消不写表。

## [0.15.1] — 2026-10-17 UI 编号真源同步 + 新增界面布局可视化图鉴

### 文档

- `docs/UI_NUMBERING.md` 补齐三个版本的状态：A1-5 搜索模式下拉标记
  **已合并（v0.14.6）**（能力并入 A1-4 智能搜索框，编号作废）；A1-9 品牌块
  补记 v0.14.4 放大填满顶栏；W1/B2-3 图卡补记 v0.14.5 自适应列数与
  v0.15.0 按显示宽度分档加载。
- 新增 `docs/ui-layout-map/ui-layout-map.html`：界面布局可视化编号图鉴
  （SVG 线框图 + 点击芯片查说明 + 全量编号表），含开发进度统计与
  待开发项（PEND-001~005）汇总。

## [0.15.0] — 2026-10-14 画廊缩略图按显示尺寸自动选档（高清升级）

### 新增

- **按显示宽度选档**：网格/瀑布流面板在自适应布局时上报卡片实际显示宽度
  （物理像素，含 DPI 缩放）。卡片宽度超过 256px Thumb 档的 1.25 倍上采样
  范围（≈320 物理像素）时，自动改用 1024px Preview 档 WebP 作为卡片图，
  先显示 Thumb 再渐进替换，窗口放大后图片不再模糊。
- **本地/远端分流**：本地行始终允许升级（解码本地原图生成 Preview，一次
  生成磁盘缓存，加载走小体积 WebP 保证大图加载速度）；远端（NAS）行在
  弱网下不触发 Preview 生成，避免整图下载占用带宽，网络恢复后自动升级，
  已缓存的 Preview 不受网络限制。
- 高清档位使用独立的小容量内存 LRU（64 张，与 Thumb 档 500 张分离），
  控制大图位图内存占用。
- 新增 `GalleryDisplayMetrics` 显示宽度通道与 5 项单元测试。

## [0.14.8] — 2026-10-14 修复顶栏功能按钮/搜索框无法点击

### 修复

- 顶栏合并后整行 80px 属于 WindowChrome 标题区（CaptionHeight=80），鼠标命中被
  系统判为窗口拖拽。为搜索框（A1-4）、功能按钮组（导入/扫描/刷新/AI/颜色/下载/审核）、
  最近入库下拉、本地/共享库切换补上 `WindowChrome.IsHitTestVisibleInChrome="True"`，
  恢复这些控件的可点击性；品牌区与空白处仍保留窗口拖动。

## [0.14.7] — 2026-10-14 修复画廊卡片误显示「文件缺失」覆盖层

### 修复

- 画廊卡片（网格/瀑布流）的「文件缺失」覆盖层绑定增加
  `FallbackValue=Collapsed` / `TargetNullValue=Collapsed`：当卡片未绑定到有效
  行数据（虚拟化容器回收）时保持隐藏，不再出现无数据却显示「文件缺失」的幽灵卡片。
  该覆盖层现在仅由数据库行 `is_missing=1` 触发。

## [0.14.6] — 2026-10-12 智能搜索：单搜索框自动识别意图 + 拼音 + 语义 fallback

### 变更

- **UI 合并**：删除 A1-5 搜索模式下拉 ComboBox，A1-4 搜索框加宽到 340px，
  单搜索框自动识别意图（学习 ArtStation/FAB 素材网站搜索逻辑）。
- **SearchQueryParser 智能解析**：自动判断搜索意图——含扩展名→文件名优先，
  完全匹配分类名→分类搜索，含中文→生成拼音版本，其余→多字段 OR 搜索。
- **ImageQueryService SQL 多字段 OR**：Tag 模式不再只查 image_tags，
  联合查询 tag + file_name + notes + primary_category + pinyin。
- **拼音支持**：新增 TinyPinyin 库；image_tags 表加 pinyin 列（schema v11）；
  TagService/LegacyPythonImporter/ImageQueryService 入库时预计算拼音；
  DbMigrator v11 迁移回填所有已有标签的拼音。
- **语义 fallback**：精确匹配无结果（输入 ≥4 字符）且 AI 可用时，
  自动降级到 SemanticSearchService 语义搜索并合并结果。
- **Enter 键立即提交**：跳过 300ms 防抖定时器，SearchTextBox_KeyDown 处理器。

### 新增文件

- `Services/SearchQueryParser.cs` — 智能搜索查询解析器
- `Data/Schema.v11.sql` — image_tags 加 pinyin 列

### 验证

- Release 编译 0 警告 0 错误
- 测试 785/785 全绿

## [0.14.5] — 2026-10-12 画廊自适应布局：窗口缩放无空白

### 变更

- **网格视图（VirtualizingTilePanel）自适应**：窗口放大时缩略图等比放大填满宽度，
  放大到 1.5 倍原大小时插入新列缩回；最小不低于原大小 75%。始终无右侧空白。
- **瀑布流视图（VirtualizingMasonryPanel）自适应**：同源算法，扣除列间 Gap 后计算。
- 新增 `ComputeAdaptiveColumns` 静态方法（网格/瀑布流各一份，因 Gap 计算不同）。
- `MeasureOverride` 缓存 `_columns`/`_effectiveItemWidth`/`_effectiveItemHeight`，
  `ArrangeOverride` 直接复用，保证测量与排列一致。

### 验证

- Release 编译 0 警告 0 错误。

## [0.14.4] — 2026-10-12 品牌区放大：Logo 填满顶栏 + 名称字号优化

### 变更

- **W1/A1-9 品牌 Logo 放大**：24×24 → 56×56，圆角 6→12，填满 80px 顶栏高度。
- **「千星汇图」名称字号优化**：14→18→40→30px，最终 30px SemiBold + Microsoft YaHei UI，与 56px Logo 视觉协调且更柔和可爱。

### 验证

- Release 编译 0 警告 0 错误。

## [0.14.3] — 2026-10-12 顶栏合并：标题栏+工具条单行 80px

### 变更

- **W1/A1 顶栏合并为单行**：原标题栏（40px）+ 工具条行（44px）合并为单行 80px。
- **排列顺序**：A1-9 品牌 Logo → A1-4 搜索框 → A1-5 搜索模式下拉 → 剩余操作按钮依次（导入/扫描/刷新/AI/颜色/下载/审核）→ 最近入库 → 本地/共享库切换 → [弹性空白] → 通知/设置/版本号/系统三按钮。
- `WindowChrome.CaptionHeight` 同步 40→80，整行可拖动。
- `Grid.RowDefinitions` 由 4 行精简为 3 行（顶栏 80 / 主区 * / 状态栏 28）。

### 验证

- Release 编译 0 警告 0 错误。

## [0.14.2] — 2026-10-10 UI 精简：移除顶部菜单栏

### 变更

- **移除顶部菜单栏（文件/视图/工具/帮助）**：文件、视图、工具三个菜单的功能全部已有对应快捷按钮（导入、扫描NAS、下载队列、三种视图切换、本地/共享库、AI分析、分析颜色、审核队列、设置），菜单属于重复入口；帮助菜单三项（关于/日志文件夹/快捷键提示）均无实际功能（前两项错误绑定到 SettingsCommand，快捷键提示为禁用占位），一并移除。
- Ctrl+I 等快捷键仍由 `InputBindings` 生效，不依赖菜单。

### 验证

- Release 编译 0 警告 0 错误。

## [0.14.1] — 2026-10-10 修复：登录窗口关闭后主界面无法打开（XamlParseException）

### 修复

- **B1 图标模式 ToggleButton 样式类型不匹配**：`FilterPopupToggle` 和分类图标列表的 `ToggleButton` 误用了 `SubtleButtonStyle`（TargetType=Button），导致 `XamlParseException: "Button" TargetType 与元素 "ToggleButton" 的类型不匹配`，ShellWindow 构造失败被 `OnStartup` catch 后 `Shutdown(1)`，表现为「登录窗口关闭后主界面不打开」。
  - 新增 `SubtleToggleButtonStyle`（TargetType=ToggleButton，完整 ControlTemplate），替换两处 ToggleButton 的 Style 引用。

### 验证

- 启动日志确认 `ShellWindow shown.` + `InitializeAsync completed.`，主界面正常打开。

## [0.14.0] — 2026-10-09 布局瘦身：B1 图标模式 + B3 可折叠，最大化中央画廊

### 变更

- **B1 左栏双模式（v0.14.0）**：新增 `SidebarIconMode` 设置（默认 true=图标模式）。
  - **图标模式（约 60px）**：顶部模式切换按钮 + 漏斗筛选按钮（Popup 承载 B1-6 全部 11 组筛选）+ 分类图标垂直列表（8 个：全部/角色/生物/场景/道具/素材/未分类，hover 显示 `名称 (计数)` tooltip）。
  - **图标+文字模式（约 240px）**：恢复完整分类树 + B1-6 筛选标签组。
  - 顶部切换按钮（»/«）在两种模式下始终可见，状态持久化。
- **B1-6 筛选标签组浮层化**：图标模式下通过漏斗按钮的 Popup 调出（宽 280px，右对齐，点击外部关闭）；文字模式下仍常驻展开。
- **B3 详情栏可折叠（v0.14.0）**：新增 `DetailPanelExpanded` 设置（默认 false=折叠）。
  - **折叠态（约 40px）**：仅显示一个展开按钮（»）。
  - **展开态（约 300px）**：完整详情面板，右上角加折叠按钮（«）。
- **列宽动态绑定**：B1/B3 的 `ColumnDefinition.Width` 通过新增 `BoolToGridLengthConverter`（Parameter `"trueWidth,falseWidth"`）绑定到 VM 布尔属性，避免硬编码宽度反复摇摆。
- **分类图标资源**：新增 `Themes/Icons.xaml` 资源字典，定义 8 个分类 + 漏斗/展开/收起共 11 个 `StreamGeometry`；`CategoryNodeViewModel` 新增 `IconGeometry` 属性（`Geometry.Parse`，测试环境无 Application 也可用）。

### 测试

- 785 项测试全绿（Release 0 警告 0 错误）。

### UI 编号同步

- `W1/B1` 左栏：宽度由固定 240 改为动态（图标 60 / 文字 240）。
- 新增 `W1/B1-ICON` 图标模式分类列表（图标按钮 + tooltip）。
- 新增 `W1/B1-FLT` 漏斗筛选按钮 + Popup（承载 B1-6）。
- `W1/B1-1` 分类树：仅文字模式可见。
- `W1/B1-6` 筛选标签组：文字模式常驻；图标模式经 W1/B1-FLT Popup 调出。
- `W1/B3` 详情栏：宽度由固定 300 改为动态（展开 300 / 折叠 40）；折叠态显示展开按钮。

## [0.13.7] — 2026-10-09 UI 迁移：质感分类自 B1-1 移入 B1-6 筛选标签组（B1-6-11）

### 变更

- **质感从素材库分类树移至筛选标签组**：按用户要求，质感不再作为 B1-1
  分类树节点（树收敛为 全部 + 5 分类 + 未分类 = 7 行，折叠计数 8→7），
  改在 B1-6 尾部新增「质感」多选标签组（编号 B1-6-11，17 个标签 chips：
  金属/石材/锈蚀/磨损…，视觉位于状态形容之后、三个属性桶之前），
  点击按标签 EXISTS 筛选，组内 OR、与其它筛选组 AND。
- **分类体系真源不变**：质感仍是 TagTaxonomy 6 分类之一，DeriveCategory
  照常产出 primary_category='质感'；新增树展示真源
  `TagTaxonomy.TreeCategories`（Categories 去掉质感），B1-1 树与折叠
  计数改由它驱动，树节点计数改为按分类名映射（不再依赖索引对齐）。
  图卡右键「设置分类」与详情栏分类下拉保留全部 6 分类（编辑操作不受影响）。
- 筛选链路：`LibraryFilter.TextureTags`（新字段，Clone 同步）+
  `ImageQueryService.AppendTagGroupClause("$texture", …)` +
  ClearFilters 显式重置 + FilterGroupsPanel 质感组（ResetSelection 覆盖）。
- `docs/UI_NUMBERING.md`：B1-1 改 5 分类；新增 B1-6-11；B1-6 改 11 项；
  B1-COL 计数 7。冒烟脚本 UiSmokeU3 同步：树无质感分类按钮、
  折叠计数 (7)、展开 B1-6 后质感 chip「金属」存在、重启持久化。

### 测试

- 新增 2 项：`Tree_excludes_texture_category_after_move_to_filter_groups`、
  `Toggle_texture_chip_writes_texture_tags`；既有树结构/计数/折叠头/
  Clone/ResetSelection 断言同步。测试总数 783 → 785，全绿
  （1 例 NAS 远程缩略图测试首跑网络偶发失败，单跑通过）。

## [0.13.6] — 2026-10-09 UI 隐藏：B1-2 收藏夹 / B1-3 热门标签 / B1-4 贴图组（功能预留）

### 变更

- **左栏三分区暂隐藏（功能预留，非删除）**：B1-2 收藏夹树、B1-3 高频标签
  Top50、B1-4 贴图组树按用户要求暂不放在 B1 位置展示。ShellWindow.xaml
  三个分区头与内容面板加 `Visibility="Collapsed"`，分区头注释记录还原
  方法（删除 Collapsed 还原 BoolVis 绑定）；编号保留不作废。
- **预留范围**：ViewModel（CollectionTree/TagTree.TopTagNodes/PbrGroupTree）、
  服务层、折叠态持久化（sidebar_collections/top_tags/pbr_groups_expanded）、
  画廊拖拽加入收藏夹、W3/A6 贴图组规则页全部保留不动——仅 UI 入口隐藏。
- 后续待办：用户要求在合适时机于别处补充这三个功能的入口（不在左栏），
  届时恢复或重挂 UI 即可（project_memory 已记录 PEND-002）。
- `docs/UI_NUMBERING.md`：B1-2/3/4 改「暂隐藏（功能预留）」；B1-COL 收敛
  为素材库 + 筛选标签组两个可见分区头。冒烟脚本 UiSmokeU3 同步：断言
  收藏夹/热门标签/贴图组分区头不存在、重启后仍隐藏。

## [0.13.5] — 2026-10-09 UI 合并：属性筛选三维并入筛选标签组（B1-5 作废）

### 变更

- **B1-5 属性筛选分区整体取消（B1-5 及 B1-5-2/3/4 作废）**：分辨率（长边）/
  文件大小/评分三维本质都是辅助筛选标签，与 B1-6 筛选标签组合并——
  左栏筛选能力统一收口到 B1-6 一处。B1-5 区块从 ShellWindow.xaml 删除，
  `SidebarSectionHeaderStyle` 折叠头与 `sidebar_attribute_filter_expanded`
  持久化设置项同步移除（AppSettings/AppSettingsService）。
- 新增 B1-6-8/9/10 三个**单选**桶组（`SingleSelectFilterGroupViewModel`，
  复用 FilterGroupTemplate 与多选标签组外形一致）：组内互斥、再点取消、
  无「全部」占位 chip；点击写 `Filter.ResolutionBucket/FileSizeBucket/
  RatingBucket`，「清除全部筛选」一并重置。原 `AttributeFilterViewModel`
  整文件删除，`AttributeFilterDisplay`（桶文案 switch）迁入
  FilterGroupsPanelViewModel.cs（Support 命名空间）。
- `docs/UI_NUMBERING.md`：B1-5、B1-5-1~4 全部标记**已作废永不复用**；
  新增 B1-6-8/9/10 三行；B1-6 改 10 项。冒烟脚本 UiSmokeU3 同步：
  四分区断言（属性筛选标题不存在）、「全部」chip 全局 =0、
  B1-6-8/10 桶 chip（`<1024`/`未评分`）存在性。

### 测试

- 新增 2 项：`Panel_builds_three_single_select_attribute_groups`（组名序/
  各 4 chips/无「全部」）、`Single_select_attribute_group_is_mutually_
  exclusive_and_toggle_off`（互斥切换 + 再点取消回 null）；
  `ResetSelection_clears_all_groups_and_palette` 扩展属性桶断言。
  测试总数 781 → 783，全绿。

## [0.13.4] — 2026-10-09 UI 合并：颜色色族筛选并入色彩倾向色板

### 变更

- **B1-5 属性筛选移除「颜色」维度（B1-5-1 作废）**：原 9 色族单选 chips
  （红/橙/黄/绿/青/蓝/紫/品红/灰）与 B1-6-3「色彩倾向」19 色色板功能
  重叠——两者底层都筛 `images.color_family`，色板经 `HexToFamily` 映射
  后覆盖全部色族且支持多选，是严格超集。现色彩筛选统一收口到 B1-6-3
  色板，属性筛选收敛为三维（分辨率/文件大小/评分）。
- 同步删除冗余链路：`LibraryFilter.ColorFamily` 字段及其 `Clone()` 项、
  `ImageQueryService` 的 `color_family = $colorFamily` 单选子句、
  `AttributeFilterViewModel.ColorOptions/ApplyColor`、筛选摘要的色族片段、
  `ClearFilters` 重置项。色族判定服务 `Services.Colors.ColorFamily`
  保留（色板映射、dominant_colors 回填仍在用）。
- `docs/UI_NUMBERING.md`：B1-5-1 标记**已作废永不复用**；B1-6-3 注明为
  唯一色彩筛选入口；B1-5 改三维。冒烟脚本 UiSmokeU3 同步（断言「品红」
  chip 已消失、「全部」chip 恰 3 个）。

### 测试

- `ImageQueryServiceTests` 两处色族用例改走色板路径
  （`ColorPaletteHexes`：#FF0000→red / #0000FF→blue / #808080→gray）。
- 781 项测试保持全绿；Release 0 警告 0 错误。

## [0.13.3] — 2026-10-09 UI：B1-1 二级分类标签改气泡 chips 自动换行

### 变更

- **B1-1 素材库树展开后的二级标签布局改版**：分类（如「角色」21 个标签）
  展开后原为整行纵列树节点，21 个标签占 21 行、纵向空间浪费严重；现改为
  圆角气泡 chips + `WrapPanel` 自动换行（新样式 `TreeTagChipStyle`，
  圆角 8、未选 SurfaceVariant 底+描边、hover/选中 Accent 高亮），视觉与
  B1-5 属性筛选气泡统一，侧栏 240–300px 下每行容纳 3–4 个标签。
- 纯布局改动，筛选语义不变（仍写 `Filter.Tag` 单标签筛选，与 B1-3 热门
  标签 chips 活跃态三处同步）；分类行本身的 ▸/▾ 展开箭头与计数不受影响。
- `docs/UI_NUMBERING.md` B1-1 条目同步。

### 测试

- 无逻辑改动，781 项测试保持全绿；Release 0 警告 0 错误。

## [0.13.2] — 2026-10-09 CLIP 词表 v2 落地：本地模型零样本对齐新标签体系

### 变更

- **重新生成 CLIP 文本嵌入 JSON（v1 → v2）**：用内置打包的
  `AiModels/text_model.onnx`（Xenova clip-vit-base-patch32，254 MB，
  无需重新下载）经 ClipSpike `gen` 生成，词表从旧「10 分类 + 4 组」换为
  M12 体系的 **6 分类 + 11 组 168 标签（共 174 条零样本向量，均 512 维）**，
  覆盖 6 个分类组与美术风格/画面构图/灯光氛围/题材气质/状态形容 5 个
  筛选组；文件格式走 `format` 列、色彩倾向走色板，不进 CLIP 词表。
- `ClipTextIndex.ExpectedPromptTemplateVersion` **1 → 2**：老用户
  dataRoot/models 下的 v1 JSON 启动时自动被内嵌 v2 覆盖（v0.9.0 的
  EnsureDistributed 版本校验机制）；用户自制 ≥v2 文件仍保留。
- 同步替换两处分发副本：内嵌资源 `Assets/Ml/text-embeddings-clip-vit-b32.json`
  与离线打包目录 `AiModels/`（发布时复制到 exe 同目录 models\）。
- 生成工具沿用既有 `tools/spike/ClipSpike`（`gen`/`sanity` 命令，
  Vocab.cs 已含 M12 v2 中英文 prompt 词表），未另建 ClipVocabGen 工程
  （避免重复代码）；模型经同卷硬链接接入 tools/spike/models（gitignored）。

### 测试

- 新增 `Embedded_resource_matches_TagTaxonomy_vocabulary`：守卫内嵌 JSON
  与 `TagTaxonomy` 锁步——6 分类名/顺序、11 组组名、每组标签名/顺序、
  512 维、174 条目总数，词表漂移会直接红。
- 既有 EnsureDistributed 幂等测试与 LocalAiService 合成词表 fixture 的
  版本号同步为 2（v1 现在语义上就是过期文件）。
- 测试总数 **780 → 781**；Release 0 警告 0 错误。

## [0.13.1] — 2026-10-09 M12 补齐：手动标签重推导分类接通 + 旧分类 UI 残留清理

### 修复（M12-F/Z 复核发现的遗漏）

- **B3 详情栏分类下拉仍为旧 10 分类**：`DetailViewModel.CategoryOptions`
  与 B2 图卡右键「设置分类」子菜单（`ShellViewModel.CategoryMenuOptions`）
  在 M12-F 漏改，仍列出贴图/纹理、实拍参考等旧分类；现统一改为
  `TagTaxonomy.Categories` 的 6 个新分类（+未分类）。
- **手动加/删标签后分类不重推导**：`TagService.RecomputeCategoryForImage`
  在 M12-B 已实现但零调用——B3 单图加标签、删标签、批量加标签、批量删
  标签 4 个入口均未接入，导致用户手动编辑标签后 primary_category 与标签
  词表脱节。现 4 个入口全部接通（批量删除先完成全部删除再逐图重推导一次，
  避免逐标签重复推导）；AI 分析路径不受影响（PersistResult 已有推导）。
- `tools/smoke/UiSmokeU3.ps1` 折叠计数断言「素材库 (12)」更新为
  「素材库 (8)」（全部+6 分类+未分类），新增 Step 3b：B1-6 筛选标签组
  折叠区存在性 + 展开后「写实」(B1-6-1)、「PNG」(B1-6-2) chips 检查。

### 测试

- 新增 `DetailCategoryRecomputeTests`（6 项）：分类下拉 6 分类契约、
  加分类组标签→推导分类、加纯筛选组标签→保持未分类、删标签→回落未分类、
  批量加标签逐图重推导、批量删标签全部删完后一次重推导。
- 测试总数 **774 → 780**；Release 0 警告 0 错误。

## [0.13.0] — 2026-10-09 M12 标签驱动分类体系重构（6 新分类 + 11 标签组）

### 核心变更（Breaking：分类体系替换）

- **标签决定分类**：原 10 个固定一级分类（贴图/纹理、实拍参考…动画参考）
  替换为 **6 个新分类**——角色 / 生物 / 场景 / 道具 / 质感 / 素材。
  图片标签命中某分类组词表时由 `TaxonomyService.DeriveCategory` 推导
  `primary_category`（命中标签数最多→平局取置信度和最高）；只命中筛选组
  标签时分类为 NULL（未分类）。筛选标签共 **7 组**：美术风格 / 文件格式 /
  色彩倾向 / 画面构图 / 灯光氛围 / 题材气质 / 状态形容，不参与分类推导。
- **Schema v10 迁移**：`images` 新增 `legacy_primary_category` 列备份原值
  （只增不删）；旧分类映射（贴图/纹理→素材、材质参考→质感、环境/场景→
  场景、雕刻参考→角色、UI/HUD→道具；实拍参考/概念设计/灯光参考/配色参考/
  动画参考→NULL），并对已有人工/AI 标签的图重推导分类。
- **Cloud AI 提示词为 Breaking Change**：云端分析不再要求输出固定
  category 字段，改为从标签分类体系动态构建提示词、只输出 tags，
  `PrimaryCategory` 由下游 `TaxonomyService` 推导。

### 新增

- `Models/Taxonomy/TagTaxonomy.cs`：6 分类 + 11 组（6 分类组+7 筛选组）
  标签词表（约 166 标签，「锈蚀」等跨组标签分类组优先）、TagToGroup/
  GroupToCategory 反向映射、IsKnownCategory/GroupOf/CategoryOfTag/
  TagsOfCategory 查询方法。
- `Services/TaxonomyService.cs`：DeriveCategory 两个重载（纯标签计数 /
  带 AI confidence），Local CLIP 与 Cloud 两路统一走标签推导。
- `TagService.RecomputeCategoryForImage(imageId)`：手动加删标签后重推导
  分类（直接 UPDATE，不走 AddTags 避免事件循环）。
- **LibraryFilter 7 个新筛选维度**：ArtStyleTags / CompositionTags /
  LightingTags / ThemeTags / ConditionTags（组内 OR、组间 AND，
  `image_tags EXISTS+IN`）、FileFormats（`images.format` 不区分大小写）、
  ColorPaletteHexes（#RRGGBB 经 ColorFamily.HexToFamily 映射色族后匹配
  `i.color_family`）。
- **B1-1 素材库树重构**：全部 + 6 新分类 + 未分类；分类行可展开
  （▸/▾）显示该分类组词表标签为二级节点，点击二级标签按单标签筛选。
- **B1-6 筛选标签组折叠区**：5 组多选标签 chips + 文件格式多选 +
  19 色色彩倾向色板；默认收起，折叠态持久化
  （`sidebar_filter_groups_expanded`）；筛选摘要（B2-1）同步显示
  各激活维度；「清除全部筛选」一并重置。

### AI 路径

- `AiAnalysisService.PersistResult`：AddTags 后用推导分类覆盖 AI 原始
  PrimaryCategory（保留 WHERE IS NULL 契约）；`AiLearningService.RefineAsync`
  重排后重新推导。
- `LocalAiService.AnalyzeImageAsync`：先 DeriveCategory(rank.Tags)，
  null 才回退 CLIP rank.PrimaryCategory；本地 CLIP 标签推导分类优先于
  CLIP 内置分类排序。
- `ClipTextIndex.EnsureDistributed`：加 promptTemplateVersion 版本校验
  （ExpectedPromptTemplateVersion=1），用户本地嵌入文件版本低或损坏时
  自动从内嵌资源重新生成。

### 测试

- 测试总数 **728 → 774**（M12 各阶段累计 +46）：TaxonomyService 推导
  用例、schema v10 迁移映射/备份/重推导、LibraryFilter 七维 SQL
  （单/多选、多组 AND、色板命中、Clone 不丢字段）、B1 树结构与二级标签、
  B1-6 chips/色板 VM 多选与重置。
- Release 全解决方案 **0 警告 0 错误**。

### UI 编号同步

- `W1/B1-1`：10 分类 → 6 新分类 + 可展开二级标签节点。
- 新增 `W1/B1-6` 与 `W1/B1-6-1~7`（美术风格/文件格式/色彩倾向色板/
  画面构图/灯光氛围/题材气质/状态形容）；计划原列 B1-5-5 色板落地归入
  B1-6-3，**B1-5-5 编号作废不复用**。

### 已知遗留

- ~~内嵌 CLIP JSON 仍为 v1~~ **已在 0.13.2 解决**：v2 JSON 已用内置
  `text_model.onnx` 重新生成（6 分类 + 11 组 168 标签，174 条目）。
  后续若再升级 `text_model.onnx`，重跑 ClipSpike `gen` 并同步
  `ExpectedPromptTemplateVersion` 常量。

## [0.12.3] — 2026-10-09 UI 调整：瀑布流默认 + 视图切换迁移 + 底部署名

### 变更

- **瀑布流（Browse）设为默认画廊视图**：`AppSettings.GalleryViewMode`
  默认值从 `"Grid"` 改为 `"Browse"`；`AppSettingsService` 非法值回退
  同步改为 `"Browse"`。全新安装与重置后开箱即瀑布流；老用户
  `config.json` 中已持久化的合法三态值（Grid/List/Browse）不受影响。
- **W1/A1-3 视图切换三按钮迁移至 B2 画廊工具条左侧**：原位于顶部
  Row 1 工具条右侧的「网格/列表/浏览」三 ToggleButton 整组迁移到
  B2 工具条左侧「显示计数」原位（位置更贴近画廊、视觉与缩放滑杆/
  排序下拉同栏）；删除迁移后冗余的 `1×22` 分隔符。
- **W1/B2-1 显示计数移至 W1/C1-C**：B2 工具条不再重复显示
  「显示 N 张 / 共 M」——状态栏 C1-C 居中区早已显示同一对计数
  （`StatusBar.FilteredCount / TotalCount`），移除 B2 重复后无信息
  损失；B2 左侧仅保留视图切换 + 筛选摘要（`FilterSummary`）。
- **W1/C1-NAME 底部状态栏追加软件英文名+版本号**：在 C1-R 区
  AI 档位文本（`StatusBar.AiStatusText`）之后追加 ` · StellarHub vX.Y.Z`
  署名。新增 `ShellViewModel.AppVersionText` 属性（`{EnglishName} v{Version}`）。
  顶栏 W1/C1-VER 的品牌化「vX.Y.Z · 里程碑标签」展示保留不变。

### 测试

- `AppSettingsServiceTests`：`New_installation_uses_known_defaults` 与
  `Unknown_theme_and_view_mode_fall_back_to_defaults` 两处
  `GalleryViewMode` 期望值 `"Grid"` → `"Browse"`。
- `ShellViewModelTests.Gallery_starts_empty_with_default_view_mode`：
  断言从 `IsGridView || IsListView` 扩展为含 `IsBrowseView` 三态之一
  （原断言默认排除 Browse，新默认为 Browse 必须更新）。

### UI 编号同步

- `W1/A1-3` 视图切换：位置从「顶部 Row 1 工具条右侧」改为
  「B2 画廊工具条左侧」，编号沿用（功能未变，仅迁移位置）。
- `W1/A1-T` 工具条行：右侧描述去掉「视图切换三按钮」，仅保留
  「本地/共享库切换」。
- `W1/B2-1` 计数条：重命名为「视图切换+筛选摘要」，计数显示
  职责完全交给 `W1/C1-C`。
- 新增 `W1/C1-NAME`：底部状态栏 AI 档位后的软件英文名+版本号署名。

## [0.11.0] — 2026-10-09 M11 图片原始文件统一托管（CAS + NTFS 硬链接分类）

### 新增

- **统一托管库**：首次启动 W7 向导选定库目录（须与数据目录同盘），
  此后所有导入原图以内容寻址（MD5）存入 `files\{hash前1位}\{前4位}\`
  CAS 层，文件名保留原名并追加入库编码；一级分类以 NTFS 硬链接组织，
  同内容跨分类只占一份物理空间（去重方案 A），原始外部文件不再被移动
- **入库编码**：`yyyyMMdd-<hash前6位>-<D3序号>`，按「日期+hash前缀」
  在独立事务内计数预留，事务回滚则序号撤销
- **W7 首次启动向导（540×500）**：新安装仅「开始使用」；检测到旧库时
  提供「立即迁移」（逐张 CAS 复制 + 硬链接 + 回写，行级幂等，中断后
  重开自动续跑）与「跳过」（软跳过：不删数据，画廊默认隐藏未托管行，
  可在 W3/A1 随时恢复）；NAS 旁路：`source=remote` 老行迁移自动跳过
  并记日志，不触碰 NAS 代码
- **W3/A1 素材库托管卡片**：库目录只读展示 + 置灰「修改…」（本版不
  支持改库）+「显示未托管的旧版图片」开关（仅跳过迁移后可用，翻转
  即时刷新画廊）
- schema v9：`library_files`（content_hash 主键 + physical_path +
  entry_code 唯一 + 尺寸/格式）、images 新增 library_hash/
  library_path/original_path/entry_code 四列；升级自动回填
  library_hash←file_hash、original_path←file_path
- 正常导入、FileWatcher 监控导入、Python 旧版库导入三条入口统一托管
  语义（库未配置时全部回落旧版直存，行为兼容）；源文件缺失的旧版
  Python 记录回落直存登记，元数据不丢失
- 测试 657 → 719（+62）：schema v9 迁移/路径解析器/编码生成器/CAS+
  硬链接/托管导入/老数据迁移/W7 向导/软跳过过滤/旧版导入托管/开关
  联动画廊；DatabaseServiceTests 版本断言 8→9
- 版本号 0.10.0 → 0.11.0（NO.M，新增功能 +0.1.0）；UI_NUMBERING 新增
  W7-1~W7-4 与 W3/A1-LIB* 控件编号

## [0.10.0] — 2026-10-09 AI 自适应学习（三方案）+ 训练数据留存

### 新增

- **AiLearningService：不重训模型的应用层「越用越准」管线**，三方案
  融合于本地分析精排（RefineAsync，任一环数据不足自动退化为原始结果，
  管线故障绝不影响主分析）：
  1. **候选词表学习**——用户人工标签按使用频次进入候选词表（≤32 个，
     排除保留率过低的词），经 text_model 计算文本 embedding 与图像
     embedding 余弦打分，AI 从此能打出用户领域自定义词（embedding 按
     标签缓存于 learned_tag_embeddings）
  2. **修正偏好重排**——保留率 = 1−删除次数/建议次数；<0.30 的标签
     剔除（需 ≥3 条建议），其余按保留率最高 0.75× 降权重排
  3. **参考样本 kNN**——有 embedding 且有 manual 标签的图构成经验库
     （仅 local 来源向量，≤4000），新图取 top-8 近邻相似度加权投票，
     与零样本排序融合
- **训练数据留存（为将来模型微调准备）**，schema v8 新增三表：
  - `ai_tag_suggestions`：AI 原始建议快照（标签+主分类+置信度+后端+
    模型），永不删除——用户删改不影响，与最终态对齐即得正负样本
  - `tag_feedback`：用户标签编辑事件日志（added/removed/bulk_removed +
    行来源），只追加永不删除——偏好统计与困难负样本原始依据
  - `learned_tag_embeddings`：学习词表文本 embedding 缓存
- **W3/A3-LEARN「自适应学习」开关**（默认开，即时生效）：关闭仅跳过
  精排，训练数据快照与编辑日志不受影响（留存最大化）
- TagService 新增 TagAdded/TagRemoved 事件（仅用户编辑触发，AI 写入
  走建议快照；RemoveTag 先读行来源再删除；DeleteTagEverywhere 逐图记录）
- 测试 +11：建议快照/错误跳过/编辑事件/全局删除逐图记录/保留率/词表
  排序与低保留剔除/经验库联表/词表+kNN 融合/低保留剔除精排/无 embedding
  退化/RunBatch 快照集成；DatabaseServiceTests 版本断言 7→8
- 版本号 0.9.0 → 0.10.0（NO.M，新增功能 +0.1.0）；UI_NUMBERING 新增
  W3/A3-LEARN

## [0.9.0] — 2026-10-09 AI 分析规则调整：标签驱动 + W2/AI 导入后自动分析

### 新增

- **W2/AI「AI 分析（导入完成后自动执行）」选项组**（导入对话框，
  文件格式之后）：关闭 AI（默认）/ 云端 AI / 本地 AI 三选一；选云端或
  本地时，导入运行结束即对**本次新增图**按所选后端执行作用域 AI 分析，
  进度显示在底部信息提示栏
  - `AiServiceFactory.Create(settings, mode)` 新增按次指定模式重载，
    不改写全局设置（W3/A3 保持不变）；App 组装时向 AiAnalysisService
    注入 backendResolver
  - ImportViewModel：`ImportAiMode`（默认 Disabled，每次打开对话框
    重置，避免无意消耗云端额度）+ `ImportAnalysisRequested` 事件携带
    本次新增图 ID 与模式；取消的导入只分析已入库部分
  - AiAnalysisService 新增 `BatchProgress(已处理, 总数)` 事件，
    ShellViewModel 订阅后写状态栏（INPC 属性更新由 WPF 自动封送）
  - 按次覆盖后端时 embedding source 如实记录（cloud/local 跟随实际
    backend，不再取全局 _ai.Mode）

### 修复/调整

- **AI 待分析规则改为标签驱动**：待分析 = 无任何标签（人工或 AI 写入
  均算）且无分类的图；有标签或分类的一律跳过——不论来源，不再以
  embedding 有无为准，避免反复刷新库中已有标签分类
  - 启动软件仍不自动分析（语义不变）；顶栏「AI 分析」按钮/工具菜单
    即"为无标签图补打标"语义
  - GetPendingBatch 改按 image_tags 不存在 + primary_category 为空
    过滤（作用域经 json_each 传入，规避超长 IN 参数列表）
  - AiAnalysisServiceTests：改写跳过语义测试（人工标签/AI 标签/仅分类
    三态跳过）+ 新增作用域与后端覆盖解析测试；新增 ImportViewModelTests
    （默认关闭不触发 / 云端模式触发并携带 ID）
  - 版本号 0.8.1 → 0.9.0（NO.M，新增功能 +0.1.0）；UI_NUMBERING 新增
    W2/AI、A1-2 语义注记更新

## [0.8.1] — 2026-10-08 AI 批量分析「死循环 + 通知刷屏」修复

### 修复

- **AI 批量分析死循环（消息提示循环 BUG）**：失败图不写 embedding，
  `GetPendingBatch` 每轮按"最前面的 pending"取样会永远取到同一批
  永久失败图（实测 13 张损坏图被重复处理十几万次：ProcessedCount
  162380 / 成功仅 2846），且每轮发布一条「AI 分析进行中」通知把
  消息中心与未读徽标刷爆
  - GetPendingBatch 改 keyset 分页（`i.id > $afterId` 游标只前进），
    每张图每轮至多处理一次，无论成败循环必然终止；失败图下次手动
    分析仍会重试（无持久失败标记，保持最小改动）
  - 进度通知节流（≥5s 一条）且发布前 ClearByKind 收敛为最新单条，
    运行结束清掉进度条目只留一条「AI 分析完成」（带失败计数）
  - AiAnalysisServiceTests 补 2 项回归：全失败必终止（每图仅 1 次调用）
    + 进度条目收敛、仅剩单条完成通知
  - 版本号 0.8.0 → 0.8.1（NO.P，缺陷修复 +0.0.1）

## [0.8.0] — 2026-10-08 W1/A1-10 主动刷新数据按钮

### 新增

- **A1-10「🔄 刷新」按钮（顶栏工具条，扫描 NAS 之后）**：此前刷新仅
  F5 快捷键可用且只重载 SQLite 查询，磁盘上新增文件不会被动入库
  （除非监控目录 watcher 触发或手动导入）
  - RefreshCommand 升级为「主动刷新」：逐个重扫全部被监控目录
    （ImportService 按 hash 幂等去重，已入库文件跳过）→ 重载画廊 →
    状态栏汇报（扫描/新增计数）；有新增时联动主色调回填
  - 无监控目录或目录不存在时退化为纯画廊重载（兼容旧行为）
  - 复用 IsImporting 跨命令防重入（刷新期间导入/扫描 NAS/AI/颜色
    禁用）；F5 快捷键绑定同一命令，语义同步升级
  - 窄窗口（<1500 逻辑像素）按钮收窄为 🔄 图标（F-04 响应式同款）
  - ShellViewModelTests 补 3 项测试：新文件入库 + 重扫幂等 + 无监控
    目录退化
  - 版本号 0.7.3 → 0.8.0（NO.M，新增功能 +0.1.0）；UI_NUMBERING
    新增 A1-10（A1-9 之后顺延）

## [0.7.3] — 2026-10-08 W1/A1-6 消息中心「有黄点但列表恒空」修复

### 修复

- **A1-6 消息中心显示错误（消息实际存在，非无消息）**：弹层 ListBox
  与「暂无消息」空态绑定 `TopBar.Notifications.Items`（NotificationListViewModel
  层），但该 VM 未转发服务上的 `Items`，绑定静默失败 → 列表恒空、空态
  文案恒显；而未读黄点徽标绑定 `HasUnreadBadge` 正常工作，形成
  「有未读黄点但显示暂无消息」的矛盾状态
  - NotificationListViewModel 新增 `Items` 转发属性（直接返回服务的
    ObservableCollection），XAML 绑定路径无需改动
  - NotificationListViewModelTests 补 `Items_forwards_service_collection_
    for_popup_binding` 回归测试（原有 6 项保留）
  - 版本号 0.7.2 → 0.7.3（UI 补丁，修订号 +0.0.1）

## [0.7.2] — 2026-10-08 W3 设置窗口内容自适应滚动

### 修复

- **W3 设置窗口内容被遮挡（缺滚轮浏览）**：A1–A8 各页签内容原为普通
  StackPanel/Grid 直排，窗口高度不足时（典型：A3 AI 页云端+本地两区块、
  A7 NAS 详情表单 N2–N6、A8 新建用户表单）底部控件直接被裁切且无法
  滚动查看
  - 全部 7 个页签内容统一包 `ScrollViewer`（`TabScrollStyle`：
    垂直 Auto / 水平 Disabled，矮窗口自动出现滚动条，拉高窗口即恢复
    平铺，实现自适应）
  - 滚轮接管：内层 ListBox/Slider 即使自身无可滚动余量也会吞掉
    MouseWheel 冒泡事件，新增 `TabScroll_PreviewMouseWheel` 在隧道阶段
    统一滚动外层 ScrollViewer（一格 Delta=120 → 60px，接近系统默认行速）；
    ComboBox 弹层为独立 Popup 视觉树不受影响
  - A4/A6 列表补 `MinHeight=80`，防止外层滚动布局下空列表塌陷
  - 版本号 0.7.1 → 0.7.2（UI 补丁，修订号 +0.0.1）；UI_NUMBERING.md
    W3 行为注记同步，A1–A8 编号不变

## [0.7.1] — 2026-10-08 ImageSharp 安全升级（3.1.10 → 3.1.12）

### 变更

- **SixLabors.ImageSharp 3.1.10 → 3.1.12**（主工程 + StressGen + ClipSpike）：
  - 修复 1 条 Moderate 通告（GHSA-rxmq-m78w-7wmc）
  - 移除 csproj 遗留 `<NoWarn>NU1902</NoWarn>` 全局抑制
- **残留 5 条通告（3 High + 2 Moderate）按 advisory 精确抑制**：
  改用 `NuGetAuditSuppress`（逐条 URL，替代全局 NoWarn），未来其它包的
  新通告不受影响。3.1.x 分支已停止安全维护，5 条的修复版本均为 v4.1.2；
  升 4.x 需 Six Labors Split License key（Release 构建硬校验），暂不引入。
  风险论证（2026-10-08 逐条核对 PoC 触发条件，详见 csproj 注释）：
  本仓库不使用 HistogramEqualization / TiffEncoder / TiffCompression /
  HalfVector4 / ICC 颜色转换 / IccProfile.Entries，TIFF 仅单向解码
  （缩略图与预览输出一律 WebP），3 条 High（GHSA-j3p4 / GHSA-j9gm /
  GHSA-jjfr，float TIFF + 直方图均衡化、TIFF T4/T6 1-bit 编码越界写）
  的内存破坏路径均不可达；仅 BigTIFF 恶意文件存在单线程解码 DoS
  （GHSA-wmxv，桌面场景影响有限），ICC CLUT 缺陷（GHSA-gwg2）需主动
  访问 IccProfile.Entries 才触发
- **4.1.2 升级预验证（已回退）**：编译 0 错误（现有 API 面完全兼容），
  仅 Split License 构建校验阻塞 Release 门禁；日后如需升级，
  在 csproj 配置 `$(SixLaborsLicenseKey)` 或 `sixlabors.lic` 即可
- 版本号 0.7.0 → 0.7.1（依赖安全补丁，修订号 +0.0.1）
- Release 0 警告 0 错误 + 全量 634 测试通过

## [0.7.0] — 2026-10-08 品牌焕新（星光相框猫 Logo + 青蓝星光配色）

### 新增

- **品牌 Logo 落地（用户指定品牌图，原图 1:1 保留）**：
  - `Assets/brand-logo.jpg`（WPF Resource）：青蓝发光相框 + 白色小猫 +
    金黄星光，与「StellarHub / 千星汇图」语义直接对应
  - `Assets/app.ico`：由品牌图派生的多尺寸图标（256 PNG +
    64/48/32/16 DIB 混合编码），写入 csproj `<ApplicationIcon>`，
    EXE / 任务栏 / Alt+Tab 图标同步焕新
  - Generic.xaml 隐式 Window 样式统一 `Icon`，全部窗口（含导入、
    输入框、下载队列、审核队列等对话框）一处生效
- **三处界面品牌位替换**：
  - 顶栏 A1-9：20×20 渐变占位色块 → 22×22 圆角品牌图（描边 + 名称）
  - 登录页 W5-7（新编号）：64×64 圆角 Logo + 青蓝柔光投影 + 产品名，
    窗高 360 → 440
  - 设置 W3/A5 关于页：36×36 Logo 与产品名/版本横排

### 变更

- **配色全面对齐品牌图（Dark / Light / ModernDark 三套主题）**：
  - Dark：中性炭灰 → 深空蓝黑（Background `#0A0F16` / Surface
    `#101822`，SurfaceVariant/Hover/Active/Border 全部冷蓝调）；
    紫色强调 → 青蓝（Accent `#2288E0` / AccentDim `#71C9FF` /
    AccentBg `#0C2A42`）；品牌渐变改青→蓝 `#0E9FC0 → #3B82F6`
  - Light：强调色同步青蓝（`#176FBF` / `#0B4F8F` / `#E4F1FC`），
    面板改冷白调，文字随冷调微调；TextMuted 两主题均保持 WCAG AA
    （≥ 4.5:1，实测 5:1 以上）
- **评分星标改星光金**：新增 Gold/GoldDim 色板与
  GoldBrush / GoldDimBrush（深底亮金 `#F5CB5C`、浅底压深 `#8F6600`），
  `RatingStarBrushConverter` 激活态由 AccentDimBrush 改 GoldBrush，
  与品牌图上的金黄星光呼应
- 版本号 0.6.2 → 0.7.0（品牌功能 + 配色改版，次版本号 +0.1.0）
- 全量 634 测试通过；Release 代码 0 警告（仅存量 NU1903
  ImageSharp 3.1.10 安全通告，与本轮无关）

## [0.6.2] — 2026-10-08 顶栏改版（VS Code 风格菜单行 + 工具条行）

### 变更

- **标题栏一体化（WindowChrome 合并）**：用 `WindowChrome` 把 Windows 系统
  标题栏吃掉，Row 0 菜单行（40px）变成可拖动标题栏；自定义三按钮
  （`CaptionButton` / `CaptionCloseButton` Style + `OnMinimize` /
  `OnMaximize` / `OnClose` 事件 handler）替代 `UseAeroCaptionButtons`
  （Windows 11 上 Aero 系统按钮不显示）；比原来 32px 系统标题栏 + 56px 单行
  顶栏 = 88px → 40+44 = 84px，**净省 4px** 垂直空间，且两行比例协调
- **顶栏布局重构**：原单行 56px → 双行 84px（菜单行 40 + 工具条行 44），
  参照 VS Code / Trae 风格；解决窄窗口下右侧控件（排序、缩放、版本号、设置）
  被推出屏幕的问题
- **新增文本菜单栏（A1-M 系列）**：Menu + MenuItem，IsMainMenu=True，承载
  次要功能入口，工具条行只放高频操作
  - `文件(_F)`：导入图片（Ctrl+I）/ 扫描 NAS 库 / 下载队列
  - `视图(_V)`：网格 / 列表 / 浏览（IsCheckable 互斥）+ 本地库 / 共享库
    （仅 NAS 登录可见）
  - `工具(_T)`：AI 分析 / 分析颜色 / 审核队列（条件可见）/ 设置
  - `帮助(_H)`：关于 / 打开日志文件夹 / 快捷键提示（指向 C1-R）
- **工具条行（A1-T）三列 Grid 布局**：
  - 左 Auto：导入 / 扫描 NAS / AI / 颜色 / 下载队列 / 审核（含 pending 角标）
  - 中 *：搜索框 + 搜索模式下拉 + 最近入库下拉（CompactHidePanel，
    窗口 <1500px 时折叠）
  - 右 Auto：本地/共享库切换 + 视图切换三按钮
- 通知🔔 + 设置⚙ + 版本号迁移到菜单行右侧系统区
- 状态栏 / 主分区 / 详情栏编号后移：Grid.Row 1→2、2→3
- UI 编号新增：A1-M / A1-M-FILE / A1-M-VIEW / A1-M-TOOLS / A1-M-HELP / A1-T
- 版本号 0.6.1 → 0.6.2（NO.P 补丁轮次）

## [0.6.1] — 2026-10-08 浏览视图打磨（ArtStation 风格对齐）

### 变更

- **B2-3-BRW 长宽比收紧**：`GalleryItemViewModel.AspectRatio` clamp 由
  `[0.4, 3.0]` 收紧至 `[0.75, 1.5]`，避免过多纵向长方形；与
  `Stretch=UniformToFill` 配合裁剪极端纵横比，整体偏向近方形
- **B2-3-BRW 卡片极简化**：默认态仅显示图片，移除常驻评分 chip 与来源
  徽标；hover 时底部黑色渐变浮层显示「评分 ★ + 文件名」+ 右侧来源徽标
  （参考 ArtStation Civilization VII 卡片设计）
- **B2-3-BRW 移除失效按钮**：上一版的「打开 / 定位 / 复制路径」三按钮
  hover 浮层功能无法触发（命令绑定上下文问题），按用户反馈直接替换为
  文件名 + 评分文本，无交互按钮；如需操作请使用 B3 详情面板的快捷动作
- 版本号 0.6.0 → 0.6.1（NO.P 补丁轮次）

## [0.6.0] — 2026-10-08 画廊浏览视图（瀑布流，B2-3-BRW）

### 新增

- **B2-3-BRW 浏览视图（瀑布流）**：A1-3 视图切换新增「⊞ 浏览」第三态，
  形成 Grid / List / Browse 三态互斥。瀑布流卡片为 ArtStation 风格的
  极简文本布局：图片满栏宽、来源徽标右下、评分 chip 左上、hover 浮出
  「打开 / 定位 / 复制路径」三按钮；无文件名 / 标签 / 尺寸文本块
- **VirtualizingMasonryPanel**：新增像素滚动虚拟化瀑布流面板
  （`src/QL.World/Views/Support/VirtualizingMasonryPanel.cs`）。等宽不等高，
  按「最短列优先」放置下一张，`GalleryItemViewModel.AspectRatio`
  （height/width，钳到 [0.4, 3.0]）决定卡片高度；与 `VirtualizingTilePanel`
  同样的容器生命周期回收 / 异步加载 generator re-hook / ItemsPanel swap
  外来容器恢复路径
- **AspectRatio 计算属性**：`GalleryItemViewModel.AspectRatio` 由原图
  width/height 计算并钳到合理范围，缺失尺寸回落 1.0（正方形）
- **B2-4-BRW 浏览缩放滑杆**：B2-1 工具条新增 160–512 卡片宽度滑杆，
  仅 Browse 态可见；与 Grid/List 各自独立持久化（`gallery_browse_zoom`）
- **GalleryViewMode 三态**：`AppSettings.GalleryViewMode` 新增 `"Browse"`
  取值；`AppSettingsService` 加载校验和 clamp 同步扩展；`GalleryViewModel`
  重构为单源真值 `ViewMode`（string）+ `IsGridView`/`IsListView`/`IsBrowseView`
  三个 pass-through 布尔；`SharedGalleryViewModel` 同步镜像（不持久化，
  跟随本地 Gallery 状态）；`TopBarViewModel` 转发三个属性的 PropertyChanged

### 变更

- 版本号 0.5.13 → 0.6.0（新增功能，次版本 +0.1.0，修订号归零，NO.M 轮次）
- UI 编号变更：A1-3 改为「网格/列表/浏览」三态；新增 B2-3-BRW；
  B2-4 扩展为含浏览缩放滑杆
- `SharedGalleryViewModelTests.Toggling_grid_raises_list_changed` 测试更新：
  原 `IsGridView = !IsGridView` 2-state toggle 改为 `ViewMode = "List"`
  显式赋值（3-state API 下 setting false 不再唯一确定另一态）
- Release 0 警告 0 错误；`dotnet test` 634/634 通过
- 无新增依赖

## [0.5.13] — 2026-10-08 画廊卡片与导入对话框文本叠加修复（F-18）

### 修复

- **F-18a 画廊卡片维度与星级叠加（P0）**：`GridCardTemplate` 底部
  信息行的 `<Grid>` 未定义 `ColumnDefinitions`，`DimensionsText`
  与 `Rating`（★N）同处 cell(0,0)。Rating 设
  `HorizontalAlignment="Right"`，在 cell 宽度取两者最大值时被定位
  到右沿——即叠加在 DimensionsText 右侧字符上，导致「1920×1080」
  与「★5」相互压字。修复：补 `ColumnDefinitions`（* + Auto），
  DimensionsText 放 column 0 并加 `TextTrimming="CharacterEllipsis"`
  以应对窄卡片下长维度串；Rating 放 column 1 + 6px 左 margin
- **F-18b 导入对话框文件名与统计叠加（P0）**：`ImportDialog` 进度
  区 `<Grid>` 未定义 `ColumnDefinitions`，`CurrentFile` 文件名与
  右侧统计 StackPanel（新增 N / 跳过 N / processed/total）同处
  cell(0,0)，长文件名与统计相互压字。修复：补 `ColumnDefinitions`
  （* + Auto），CurrentFile 放 column 0（已有 TextTrimming），统计
  StackPanel 放 column 1 + 12px 左 margin

### 变更

- 版本号 0.5.12 → 0.5.13（缺陷修复，修订号 +0.0.1，NO.P 补丁轮次）
- Release 0 警告 0 错误
- 无 UI 编号变更；无新增依赖

## [0.5.12] — 2026-10-08 侧栏文本叠加修复（F-17）

### 修复

- **F-17 侧栏树节点计数与名称文字叠加（P0）**：`TreeToggleStyle` 内容
  模板使用 `<Grid>` 但未定义 `ColumnDefinitions`，`DisplayName` 与 `Count`
  两个 TextBlock 同处 cell(0,0)。Count 设 `HorizontalAlignment="Right"`，
  在 cell 宽度取两者最大值时，Count 被定位到 cell 右沿——即叠加在
  DisplayName 右侧字符之上，导致「贴图/纹理 5」「BB 0」等显示成相互压字。
  修复：三处（B1-1 分类树 / B1-2 收藏夹 / B1-4 贴图组）Grid 均补
  `ColumnDefinitions`（`*` + `Auto`），Count 放 column 1，并加 6px 左 margin
  与名称隔离。同步修 `SidebarSectionHeaderStyle` 模板：箭头 ▾ 与标题
  原靠 `Margin="14,0,0,0"` 偏移，依赖字体度量；改用 `ColumnDefinitions`
  （Auto + *）+ 4px 右 margin，彻底消除箭头压字风险

### 变更

- 版本号 0.5.11 → 0.5.12（缺陷修复，修订号 +0.0.1，NO.P 补丁轮次）
- Release 0 警告 0 错误
- 无 UI 编号变更；无新增依赖

## [0.5.11] — 2026-10-08 Local-only 模式画廊网格完全空白修复（F-16）

### 修复

- **F-16 Local-only 模式画廊网格区完全空白（P0）**：用户在 staging
  v0.5.10 真机回归时发现——状态栏显示 235/245 计数、左侧分类树计数正常，
  但中间网格区纯深色背景，0 卡槽渲染。Debug exe 同环境对比同样空白，
  排除 R2R 优化问题。诊断日志显示 `MeasureOverride` 进入 itemCount=245/235
  路径、`isItemsHost=True`、`visualParent=ItemsPresenter` 均正常，但
  `panel.ItemContainerGenerator`（即 .NET 10 `VirtualizingPanel.ItemContainerGenerator`
  属性）返回 null，导致 measure pass 在 `if (generator is null) return` 处
  直接退出，从未调用 `generator.StartAt/GenerateNext` 生成容器。根因：.NET 10
  桌面环境下 `VirtualizingPanel.ItemContainerGenerator` 在 panel 已注册为
  ItemsHost 且 owner.ItemContainerGenerator 实例存活的情况下仍可能返回 null
  （与 .NET Framework / .NET Core 3.x 行为不一致）。修复：`MeasureOverride`
  与 `ArrangeOverride` 均改用 `owner.ItemContainerGenerator`（`ItemsControl.ItemContainerGenerator`
  公共属性）直接获取 generator，绕过 .NET 10 `VirtualizingPanel.ItemContainerGenerator`
  的异常行为。真机 C2/C3/C5/C6/C7 回归全绿
- 移除本轮排查加入的全部临时诊断日志（`[PANEL] MeasureOverride#N`/`layout`/
  `end`/`generator=NULL`/`OnItemsChanged` 与 `[GALLERY] RefreshAsync gen=N` 等）

### 变更

- 版本号 0.5.10 → 0.5.11（缺陷修复，修订号 +0.0.1，NO.P 补丁轮次）
- Release 0 警告 0 错误；`dotnet test` 634 项全部通过
- 无 UI 编号变更；无新增依赖

## [0.5.10] — 2026-10-08 滚动后画廊卡片永久黑框修复（F-15）

### 修复

- **F-15 向下滚动后新卡片缩略图永久不显示（P1）**：网格/列表首屏缩略图
  正常，但滚动进入视口的卡片永久黑框（同批行停留十余秒也不恢复），
  切分类正常、仅滚动复现。根因：`VirtualizedThumbnailLoader` 仅在
  `ItemsChanged`/`Loaded` 后通过 `ItemContainerGenerator.ContainerFromIndex`
  轮询挂载容器；自定义 `VirtualizingTilePanel` 在滚动的 measure 过程中新
  生成的容器在 Background 优先级下不被 `ContainerFromIndex` 返回，且面板
  直接增删视觉子元素导致这些容器不可靠地触发 `Loaded`/`DataContextChanged`，
  于是只有首屏被 Attach，滚动新行的缩略图永不加载。修复：改为监听内部
  ScrollViewer 冒泡的 `ScrollChanged` 事件，并在每次触发时遍历**实时可视化
  树**对账——对真实存在的 ListBoxItem 幂等挂载并 Attach 当前 DataContext
  （Attach 缓存命中即返回，重复调用安全）；保留 Unloaded→Detach 与
  DataContextChanged 快路径。真机多轮上下滚动、快速/慢速滚动、网格/列表
  切换回归通过
- 移除本轮排查加入的全部临时高频诊断日志（`[VTP]`/`[VTL]`/SWEEP/
  `[THUMB]` Attach/Detach/Ready 等），仅保留解码失败与加载异常两条低频
  错误日志

### 变更

- 版本号 0.5.9 → 0.5.10（缺陷修复，修订号 +0.0.1，NO.P 补丁轮次）
- Release 0 警告 0 错误；`dotnet test` 634 项全部通过
- 无 UI 编号变更；无新增依赖

## [0.5.5] — 2026-10-06 v0.5.4 真机验收 7 项缺陷修复（F-01~F-07）

v0.5.4 真机验收（150% 缩放、绿联 NAS 环境）累计登记 6 项缺陷（F-01~F-06），
回归验证中追加发现第 7 项（F-07）。本版按根因统一修复，7 项全部回归通过。

### 修复

- **F-01 画廊白底（P1）**：ShellWindow 两个画廊 ListBox 的内联 Style 未
  `BasedOn` 全局 ListBox 样式，Background/Template 回落系统默认白底 + 1px
  系统边框。修复：补 `BasedOn="{StaticResource {x:Type ListBox}}"`
- **F-02 空态重叠（P1）**：本地/共享作用域 4 处（两个 ListBox 与两个空态
  Border）Visibility 绑定 bool 漏 Converter，绑定静默失败恒为 Visible，
  两套空态文字与列表混显。修复：补 BoolVis 转换器
- **F-03 ComboBox 闭合框显示 ToString 截断（P2，单点全局修复）**：WPF
  ComboBox 闭合框的 SelectionBoxItem 放的是原始选项对象，DisplayMemberPath
  只作用于下拉项容器，闭合框无模板回落 `ToString()`
  （如 "QL.World.ViewModels.RoleOption"）。ContentTemplate /
  ContentStringFormat 绑定方案在 .NET 10 实证无效（闭合框
  SelectionBoxItemTemplate 恒为 null）。最终修法：Generic.xaml 按 DataType
  为六类选项记录（SearchModeOption / RecentImportOption / GallerySortOption /
  CategoryOption / RoleOption / ThemeOption）声明隐式 DataTemplate（绑定
  Display 属性），全部 ComboBox 闭合框统一显示中文，无需逐控件改动
- **F-04 窄窗顶栏溢出（P2）**：窗口收窄时顶栏右侧 StackPanel 覆盖左侧
  （总宽超出约 150px），「AI 分析 / 分析颜色 / 搜索模式」被裁切。修复：
  新增 NumericThresholdConverter，为顶栏 11 类元素定义 <1500 阈值紧凑形态
  （文字按钮→图标按钮、搜索框 240→140、版本号隐藏等）
- **F-05 系统标题栏不随主题（P3）**：深色主题下 Windows 系统标题栏仍为
  白底。修复：App.xaml.cs 全局注册 Window.SourceInitialized 类处理
  （EventManager.RegisterClassHandler），按当前主题调用 DWM
  DwmSetWindowAttribute（DWMWA_USE_IMMERSIVE_DARK_MODE，Build≥22000 用
  属性 20 否则 19）；主题切换时遍历活动窗口即时刷新，深浅双向验证通过
- **F-06 审核角标空点残留（P2）**：PendingCount=0 时审核角标仍显示为空的
  琥珀色圆点（Visibility=0 触发器只加在内层 TextBlock，外层 Border 常显）。
  修复：隐藏逻辑上移到外层 Border（ZeroVis + Invert 参数）
- **F-07 首次单击详情栏空白（P1，回归验证中新增发现）**：首次单击图片后
  详情栏仍显示空态，第二次点击才恢复。根因：DetailViewModel.HasSelection
  setter 漏发 IsSingle 变更通知——首次单选时 IsBatch 恒为 false 不变、其
  setter 不触发，IsSingle 卡在初始 false，依赖它的绑定与按钮 CanExecute
  全部失效。修复：HasSelection setter 补 OnPropertyChanged(nameof(IsSingle))

### 变更

- 版本号 0.5.4 → 0.5.5（缺陷修复，修订号 +0.0.1，NO.P 补丁轮次）
- 7 项修复全部真机回归验证通过（截图证据），问题登记表见
  docs/release/v0.5.4_真机验收清单.md
- 无 UI 编号变更；无新增依赖

## [0.5.4] — 2026-10-05 全局窗口撞色修复（基础 UI 文本/背景配色审计）

深色主题下「导入图片到素材库」对话框呈现白色窗口背景 + 深色控件混搭：
复选框/说明文字（浅色）在白底上不可见。

根因：WPF 隐式样式按**精确类型**查找资源键，`Generic.xaml` 中
`Style TargetType="{x:Type Window}"` 不会作用于任何 Window 子类
（ImportDialog/InputDialog/…），故窗口样式从未生效，窗体回落到系统默认白底。
其中 ImportDialog、InputDialog 的内容根（DockPanel）未自设背景，白底直接外露。

### 修复

- 7 个窗口（Shell/Login/Settings/ReviewQueue/DownloadQueue/ImportDialog/
  InputDialog）根节点显式绑定 `Background="{DynamicResource SurfaceBrush}"`（主窗口为
  BackgroundBrush）与 `Foreground="{DynamicResource TextPrimaryBrush}"`
- ShellWindow 审核队列角标：`Foreground="White"` 硬编码改为
  `{DynamicResource TextOnWarningBrush}`——深色主题 Warning 为亮琥珀 #FFB020，
  白字对比度仅 1.8:1（远低于 WCAG AA 4.5:1）
- 三个主题字典（Dark/Light/ModernDark）新增 `TextOnWarning` 色与画刷
  （深色主题 #2A1D00 深棕黑，浅色主题 #FFFFFF）
- ModernDark.xaml 补齐与 Dark/Light 对齐的缺失键：TextOnAccent、DangerBg、
  WarningBg、Overlay、TextOnOverlay（色 + 画刷），消除主题切换时
  DynamicResource 静默失效产生黑字黑底的隐患

## [0.5.3] — 2026-10-05 登录成功主窗口闪退修复（ShutdownMode 陷阱）

绿联 NAS 实测：登录成功后主窗口"闪一下"即消失，进程退出。诊断日志显示
`[STARTUP] ShellWindow shown.` 后立即触发 `Closing event. Cancel=False`。

根因：经典 WPF 登录窗陷阱。`LoginWindow` 是首个显示的窗口，WPF 自动将其设为
`Application.MainWindow`；登录成功关闭登录窗时，`ShutdownMode="OnMainWindowClose"`
（App.xaml）判定"主窗口已关闭"触发整个应用 Shutdown，刚 Show 的 ShellWindow
被强制关闭。此前 NAS 不可达时用户只能关窗进本地模式，走的是同一关闭路径，
故该 bug 自 M6-B 登录门禁引入后一直被掩盖。

### 修复

- `App.xaml.cs OnStartup`：启动期间 `ShutdownMode = OnExplicitShutdown`，
  主窗口 `Show()` 后恢复 `OnMainWindowClose`（WPF 登录窗标准处理模式）
- `App.xaml.cs` / `ShellWindow.xaml.cs`：保留启动链路 WriteLog 诊断日志
  （ShellViewModel/ShellWindow 创建、OnLoaded/InitializeAsync/Closing），
  后续启动类问题可直接从 error.log 定位断点

## [0.5.2] — 2026-10-04 NAS 首次初始化目录结构缺失修复 + UI 线程死锁修复

绿联 NAS 实测发现两个阻塞性问题：

**问题 1：NAS 首次初始化目录结构缺失**
保存主 NAS 连接并测试通过后，NAS 上未自动创建 `StellarHubShared/` 目录结构，
导致首次启动时预置管理员账号写入失败（WebDAV PUT 不会自动创建父目录，返回
409 Conflict，且异常被登录流程静默吞掉只写日志）。根因是
`SharedStorageService.EnsureSharedStructureAsync` 存在但三个关键写入入口均未调用。

**问题 2：UI 线程死锁导致应用启动无响应**
`ShowLoginGate` 里用 `.GetAwaiter().GetResult()` 在 UI 线程同步等待 NAS 异步操作，
而 `WebDavNasConnector` 里大部分 `await` 未加 `.ConfigureAwait(false)`，导致经典
同步上下文死锁（await 完成后试图回到被阻塞的 UI 线程）。表现为窗口不可见、
error.log 只有 `[BOOT]` 无 `[LOGIN]`、进程卡死。

### 修复

- `AccountService.InitializeAdminAsync`：写入 `accounts.json` 前调用
  `EnsureSharedStructureAsync`，首次启动自动建目录
- `SubmissionService.SubmitFromLocalAsync`：上传到 `pending/` 前调用
  `EnsureSharedStructureAsync`
- `SubmissionService.ApproveAsync`：审核入库到 `originals/` /
  `thumbnails/` 前调用 `EnsureSharedStructureAsync`
- `App.xaml.cs` `ShowLoginGate`：NAS 探测用 `Task.Run` 包裹，避免 UI 线程同步等待死锁
- `WebDavNasConnector`：全部 24 个 `await` 补 `.ConfigureAwait(false)`，
  从根本上消除库代码回流 UI 同步上下文的风险
- `QL.World.csproj`：修复双 BOM 导致 Debug 构建 MSB4025 失败；保留单个 UTF-8 BOM
  以兼容 PowerShell 5.1 `Get-Content` 编码检测

## [0.5.1] — 2026-10-03 深色主题文字撞色修复（输入框内容不可见 / 设置窗黑底黑字）

用户肉眼复核发现：登录窗两个输入框看不到输入内容；深色主题下多个窗口文字与
底色撞色，设置窗尤甚。隔离 WPF 渲染探针定位为真实模板缺陷，与抓图/输入法无关。

### 修复

- **TextBox 内容区被垂直裁剪（根因）**：`Control.Padding` 是可继承属性，全局
  TextBox 模板把 `Padding` 同时作用在 `ScrollViewer.Margin` 与其内部
  `ScrollContentPresenter`（继承自 TextBox 的 Padding 被 ScrollViewer 默认
  模板再次应用），Height=28 的输入框内容视口只剩约 2px，文字仅渲染顶部一条
  细线，150% 缩放下肉眼不可见。模板改为 Border 硬编码 8,4 内边距、不再通过
  `Control.Padding` 传递（模板父级继承优先级高于模板内赋值，Padding=0
  阻断无效，已用 A/B/C/D 探针实证）
- 登录窗 `LoginPasswordBoxStyle`（PasswordBox）存在同样的双 Padding 缺陷；
  删除该局部样式，登录窗与设置窗（云端 API Key、NAS 密码）统一复用
  Generic.xaml 新增的全局 PasswordBox 深色样式；密码眼睛让位改用
  Padding="0,0,34,0"（仅水平分量，不触发垂直裁剪）
- 新增缺失的全局深色样式，消除设置窗 Aero2 系统外观回落导致的撞色：
  TabControl / TabItem（透明底、选中态 TextPrimary 文字 + Accent 下划线、
  悬停 SurfaceHover）、RadioButton（深色圆环、选中 Accent 圆点）、
  PasswordBox（Surface 底 / TextPrimary 文字 / 聚焦 Accent 描边）
- TextBox 模板滚动条可见性改为 TemplateBinding，多行框
  （备注框 VerticalScrollBarVisibility=Auto）恢复可用

### 变更

- 版本号 0.5.0 → 0.5.1（缺陷修复，修订号 +0.0.1）
- 全量测试仍为 623，全部通过；Release 构建 0 警告 0 错误
- 无 UI 编号变更（无新增界面元素，仅样式体系修复）；无新增依赖

## [0.5.0] — 2026-10-02 登录窗口改版（密码显隐 / 记住账户 / 记录密码 / 设置齿轮）

按统一布局重做 W5 登录窗口，内容固定为：用户名、密码（末端显隐切换）、
登录按钮、记住账户、记录密码、右上角设置齿轮。NAS 配置入口由独立按钮
收敛进齿轮；关闭登录窗口即进入仅本地模式（行为不变，去掉冗余按钮）。

### 新增

- W5-2 密码输入框末端眼睛开关：PasswordBox/TextBox 互换显示，内容双向同步，登录始终读取 PasswordBox.Password；明文态眼睛叠加斜杠并切换 ToolTip
- W5-4「记住账户」：登录成功把用户名写入 config.json（rememberedLoginUsername），下次启动自动回填
- W5-5「记录密码」：登录成功把密码经 DPAPI（CurrentUser）加密写入 config.json（rememberedLoginPassword），下次启动解密回填；勾选联动（记录密码自动勾选记住账户，取消记住账户连带取消记录密码）；登录失败不写入，取消勾选时清除历史记录
- W5-6 右上角齿轮按钮：未登录身份打开 W3 设置窗口（默认 A1 常规页，可切 A7 配 NAS），关闭后刷新 NAS 可用状态

### 变更

- 移除「仅本地模式」按钮——直接关窗（X/Esc）即本地模式；移除「打开 NAS 设置」按钮——功能收敛进 W5-6 齿轮（不再强制跳转 A7 页签）
- AppSettings / AppSettingsService 新增 RememberedLoginUsername 与 RememberedLoginPassword（Set/Get/Has，DPAPI 复用云端 API Key 同款加密通道）
- 版本号 0.4.1 → 0.5.0（新增功能，次版本号 +0.1.0，修订号归零）
- 全量测试 614 → 623（净 +9：LoginViewModel 记住凭据/级联/落盘 8 个新增 -1 个本地模式事件移除 = 净 +7，AppSettingsService 凭据往返 +2；设置事件测试改名）
- UI 编号：W5 补充 W5-1…W5-6 子编号；无新增依赖

## [0.4.1] — 2026-10-02 AI 标签置信度落库修复

v0.4.0 收尾核验发现：`AiAnalysisResult.Tags` 已携带每个标签各自的 confidence，
数据库 `image_tags.confidence` 字段与读取模型也已存在，但批量分析落库前把标签
合并成字符串列表，导致 AI 返回的逐标签置信度统一丢失为 NULL。本补丁补齐写入
闭环，不改变手动标签保护规则。

### 修复

- `TagService` 新增逐标签 confidence 写入重载，并复用同一条 UPSERT 契约：AI 不得覆盖手动标签的 source/confidence，手动补标签仍可把 AI 行升级为 manual
- `AiAnalysisService` 批量分析时改为传递 `AiTag.Confidence`，使云端/本地 AI 返回的每个标签独立写入 `image_tags.confidence`
- 回归测试覆盖同批标签不同置信度（0.91 / 0.67）及批量分析端到端落库（0.9 / 0.8）

### 变更

- 版本号 0.4.0 → 0.4.1（NO.P 补丁，修订号 +0.0.1）
- 全量测试 613 → 614（+1：TagService 逐标签 confidence）
- 无 UI 编号变更；无新增依赖

## [0.4.0] — 2026-09-30 M9 NAS SFTP 协议扩展

v0.3.0 NAS 接入支持 WebDAV 与 SMB，但绿联等成品 NAS 的 SFTP（SSH）通道
长期缺位——部分用户环境只开放 22 端口或按 Linux 用户体系管理共享目录。
M9 在多协议抽象上补齐第三个协议驱动，扩展路线唯一功能缺口闭合；设置界面、
下载队列、聚合画廊、主 NAS 迁移等上层链路零改动复用。

### M9-A · SFTP 连接器 + SSH.NET 接入

- 引入 SSH.NET 2026.0.0（纯托管，传递 BouncyCastle 2.7.0 / Logging.Abstractions 8.0.3），唯一新增 NuGet 依赖
- `ISftpSession` 窄接口 + `RenciSftpSession` 生产实现封装 `SftpClient`；`SftpExceptionMap` 集中映射 Renci/Socket 异常到 `NasConnectionKind`（认证失败、超时、NotFound、网络不可达、协议错误），上层只见 `NasConnectionException`
- `SftpNasConnector` 实现 `INasConnector` 10 方法：POSIX rename 优先、失败降级 download→upload→delete 搬运；Range 分段下载（越界报 RangeNotSatisfiable）；上传自动创建父目录；幂等 Mkdir/Delete；RootPath 相对路径输出与 WebDAV/SMB 语义一致

### M9-B · 工厂装配 + 热切连接 + 主机密钥策略

- `SftpConnectorFactory`：密码优先取明文入参、否则 DPAPI 解密；空用户名在连接前拒绝（ProtocolError，SFTP 不支持匿名/ShareLink）；默认端口 22、30s 连接超时
- 工厂热切 SSH+SFTP（对齐 SMB fail-fast），失败即分类抛出；`App.xaml.cs` 替换占位工厂，三协议路由全部接通
- 主机密钥复用 `TrustSelfSigned` 字段（不改 schema）：关闭时拒绝未知密钥并在异常中携带 `SHA256:` 指纹供带外核对；`SftpHostKeyPolicy` 纯函数承载决策与指纹格式化

### M9-C · 设置界面 SFTP 联动

- 协议下拉新增 SFTP；切换协议端口按 443/445/22 联动（null 或任一已知默认值才覆盖，自定义端口保留）
- SFTP 隐藏独立 TLS 开关（SSH 自带加密），信任复选框显示「信任未知 SSH 主机密钥」文案；用户名提示「SFTP 必填」、根路径提示 `/volume1/StellarHubShared`
- 清理 M5/M7 时期「SFTP 留待 M8/预留」过时注释；`UnsupportedProtocolFactory` 收紧为拒绝为任何已发货协议构造

### 变更

- 版本号 0.3.0 → 0.4.0（NO.M 功能里程碑，次版本号 +0.1.0，修订号归零）
- 全量测试 613 项全绿（553 → 613，+60：连接器沙盒 20、异常映射 12、契约 SFTP case 9、工厂 8、UI 联动 10，及路由测试调整）
- 3 个子阶段独立提交（9a6aa2a M9-A / 01f837c M9-B / e350882 M9-C / M9-Z 本次）
- 无真实 SFTP 服务器环境，测试全部基于本地目录 Fake 沙盒；真实绿联 NAS SFTP 实测列为人工验收项

## [0.3.0] — 2026-09-30 M8 多 NAS 聚合 + 主 NAS 迁移

v0.2.1 单实例假设限制：登录后只能访问主 NAS，副库需重新登录；画廊无主副
聚合视图；管理员切换主 NAS 需手动搬数据。M8 打破单实例瓶颈，引入聚合视图、
ShareLink 匿名分享、自主迁移 + 手动搬运能力，让多 NAS 部署成为一等公民。

### M8-A · NasRole 枚举 + 主 NAS 强制校验 + SourceNasId 字段

- `NasRole` 枚举（Primary=0/Secondary=1/ShareLink=2）替换 `IsPrimary` 布尔，`IsPrimary` 保留为计算属性 + 副作用 setter 兼容旧 config
- `AppSettingsService.ValidateNasRoles` 保证全列表至多 1 个 Primary
- `SharedImageRecord` 加 `SourceNasId` 字段持久化 library.json 便于跨重启保留来源

### M8-B · 多实例 ServiceRegistry + 副库连接注册

- `NasConnectionRegistry`（新建）：按 spec.Id 缓存 `INasConnector` + `SharedStorageService` 副本，fingerprint 变化触发重建，主库失败抛异常副库失败仅 Debug 日志
- `ServiceRegistry.NasRegistry` + `SecondaryStorages` 字段；旧 SharedStorage/Accounts/SharedLibrary/Submissions 仍指向主库实例（向后兼容）
- `App.xaml.cs` 登录后构造 NasRegistry + EnsureAll，OnExit 统一释放

### M8-C · 聚合视图 + 来源注入

- `AggregatedSharedLibraryService`（新建）：并发加载主库 + 所有副库 library.json，注入 `SourceNasId`，主库优先去重
- `SharedGalleryViewModel.Load` 用 aggregator 替代单库 Load（带回退）
- `SharedDetailViewModel` 加 `ResolveConnector(int?)` 按 SourceNasId 路由 connector

### M8-D · 副库权限分级 + ShareLink 副库

- `PermissionService` 加 CanWriteToSecondaryNas / CanDownloadFromSecondaryNas / CanMigratePrimary / CanSubmitToSecondaryNas
- `WebDavConnectorFactory` 跳过 Basic-auth for ShareLink（匿名访问）
- `SubmissionService` 加 `NotPermitted` outcome；普通用户提交副库返回 NotPermitted
- `SettingsViewModel` 加 `AddShareLinkCommand` 单独入口；A7 列表 🔗 图标区分

### M8-E · 主 NAS 迁移 + 管理员数据搬运 + 来源徽章开关

- `PrimaryMigrationService`（新建）：4 阶段迁移（元数据 JSON / originals+thumbnails 文件树 / accounts 最后覆盖 / Role 切换），幂等复制（GetMetadataAsync 探测跳过），失败不切 Role 保留旧主
- `DataTransferService`（新建，VMs/Support）：管理员手动搬运 N 张共享图，同 connector 优先 MOVE，跨主机 download→upload→delete 源；目标 library AddOrUpdate + 源 library Remove；单条失败计入 errors 不阻断
- `SettingsViewModel.SetPrimaryWithMigrationAsync`：递归预览源主库文件数+字节数 → 弹确认框 → 调迁移服务，进度回调实时刷新
- A7 加「设为主 NAS 并迁移数据」+「搬运到此处...」+ 迁移进度文案 +「显示数据来源徽章」CheckBox
- `AppSettingsService.ShowSourceBadge` 布尔设置项（默认 false）；开启后画廊 SourceBadge 显示 `nas://{id}`

### 变更

- 版本号 0.2.1 → 0.3.0（NO.M 功能里程碑，次版本号 +0.1.0，修订号归零）
- 全量测试 553 项全绿（519 → 553，+34：M8-A/B/C/D/E 各阶段新增测试）
- 6 个子阶段独立提交（70aec3c M8-A / 4fb23da M8-B / 0f8dbc8 M8-C / 86e0e9a M8-D / 2893f49 M8-E / M8-Z 本次）

## [0.2.1] — 2026-09-30 预置管理员 + 主 NAS 标记 + 登录窗口简化

v0.2.0 发布后实测痛点：首次启动需手动点「初始化管理员」且默认 admin/admin123 繁琐；登录窗口 NAS 下拉对单 NAS 用户多余；W3/A7 NAS 配置页签无管理员门禁，普通用户可误改。本轮补丁修复上述体验问题。

### 预置管理员账号

- 启动检测主 NAS 可达 + accounts.json 为空 → 自动创建预置管理员 `XingKongKong` / `zzh18254359250`，登录窗口用户名预填
- `AccountService.InitializeAdminAsync` 签名加 `username` 参数（不再硬编码 "admin"），保持向后兼容调用方需显式传用户名

### 主 NAS 标记

- `NasConnectionSpec` 加 `IsPrimary` 字段（默认 false，旧 config.json 反序列化兼容）
- 登录时系统自动取 `IsPrimary=true` 的连接；无主则取列表第一个
- W3/A7 列表加「★ 主」徽标（IsPrimary=true 显示），详情区 N6 加「设为主 NAS」按钮（选中项已为主时禁用）
- `SettingsViewModel.SetPrimaryCommand` 把选中项 IsPrimary=true + 其他全部 false + 逐个 `UpdateNasConnection` 持久化

### 登录窗口简化

- 移除 NAS 连接下拉（`NasConnections` / `SelectedConnection` 字段删除）
- 加「打开 NAS 设置」按钮——未登录也可打开 SettingsWindow 的 A7 页签配置 NAS；关闭后 `RefreshNasState()` 让登录窗口立即生效新配置（无需重启）
- `LoginViewModel.CanLogin` 取决于是否有可用 NAS + 未在登录中（`IsBusy` 联动）
- `LoginViewModel.Username` 接受 `presetUsername` 参数（App.xaml.cs 启动自动初始化管理员后预填）

### W3/A7 NAS 页签管理员门禁

- `SettingsViewModel.IsNasManagementEnabled` = `_currentUser is null || _currentUser.IsAdmin`（未登录可见以便首次配置；普通用户隐藏）
- A7 TabItem `Visibility` 绑 `IsNasManagementEnabled` + `BoolToVisibilityConverter`
- A8 用户管理页签 `IsUserManagementEnabled` 不动（仅管理员）

### 部署说明

- 首次启动：登录窗口显示「打开 NAS 设置」入口 → 配好 NAS + 标记主 NAS + 保存 → 关闭 → 登录按钮自动启用
- 第二次启动：自动连接主 NAS + 自动创建 XingKongKong 管理员 + 用户名预填 → 输密码 `zzh18254359250` 登录
- 多 NAS 场景：管理员在 W3/A7 标记某个为主，登录用主 NAS；其他 NAS 仅配置不用于登录

### 变更

- 版本号 0.2.0 → 0.2.1（NO.P 补丁，修订号 +0.0.1）
- 全量测试 519 项全绿（512 → 519，+7：AccountServiceTests +1 + LoginViewModelTests +3 + SettingsViewModelNasTests +3）
- 0 警告 0 错误

## [0.2.0] — 2026-09-30 M7 NAS SMB 协议扩展

v0.1.1 已支持 WebDAV NAS 协议，但用户使用绿联成品 NAS（UGOS）——UGOS 默认开启 SMB 文件服务，WebDAV 需额外配置，对成品 NAS 用户不友好。M7 在现有 `INasConnector` 抽象上加 SMB 协议驱动（仅 SMB；SFTP 推到 M8 单独里程碑）。选型与 enum 注释一致——Win32 `mpr.dll` P/Invoke（zero new NuGet），与项目「零依赖」风格统一。

### M7-A：多协议路由工厂 + SFTP 占位

- 新增 `NasConnectorFactory`：按 `spec.Protocol` 路由分派给对应子工厂
- 新增 `UnsupportedProtocolFactory`：通用占位（`Create` 抛 `NotSupportedException`，含协议名 + 目标里程碑）
- App.xaml.cs 注入点替换为 `NasConnectorFactory(webDav, smb, sftp)`；M7-A 阶段 SMB/SFTP 均为占位
- 测试 482 → 488

### M7-B：SMB Connector（Win32 mpr.dll P/Invoke）

- 新增 `SmbNative`：`WNetAddConnection2`（CONNECT_TEMPORARY=4，不写凭据管理器）/ `WNetCancelConnection2`（force=true）+ NETRESOURCE + 错误码常量（1326/86/5 Auth、53/67 Bad netpath、2/3 File/Path not found、1219 session expired）
- 新增 `SmbNasConnector`：实现 `INasConnector` 10 方法。`ResolveUncPath` 拼接 uncRoot+remotePath（>260 加 `\\?\UNC\` 长前缀绕过 MAX_PATH）；`MoveAsync` 跨卷失败降级 download → upload → delete；dispose 调 `WNetCancelConnection2(force=true)` 释放会话
- 新增 `SmbConnectorFactory`：RootPath 拆 share/subPath；username 非空时 WNet 握手，username 为空走集成认证；DPAPI 解密密码与 WebDav 一致
- App.xaml.cs SMB 占位替换为 `new SmbConnectorFactory()`（SFTP 仍占位）
- 新增 `SmbNasConnectorContractTests` 14 项契约测试：用本地临时目录当 UNC 替身，`ownsConnection:false` 绕过 WNet 握手
- 测试 488 → 502

### M7-C：UI 协议联动

- `NasProtocolOptions` 由 `[WebDav]` 扩展为 `[WebDav, Smb]`（Sftp 留待 M8）
- `SettingsViewModel` 加派生属性 `IsWebDavSelected` / `IsSmbSelected` / `IsTlsApplicable` / `IsTrustSelfSignedApplicable` / `NasPortHintText` / `NasRootPathHint` / `NasTlsLabelText`
- 协议 ComboBox `SelectionChanged` 转发到 ViewModel（spec 是 POCO 无 INPC，借由 View 事件桥接避免改造持久化 schema）；切换协议若 Port 为前一默认（443/445）或 null 自动填新协议默认，非默认值保留
- xaml N3 端口提示绑 `NasPortHintText`；N5 根路径提示绑 `NasRootPathHint`；UseTls 标签绑 `NasTlsLabelText`、Visibility 绑 `IsTlsApplicable`；TrustSelfSigned Visibility 绑 `IsTrustSelfSignedApplicable`（仅 WebDav）
- 用现有 `BoolToVisibilityConverter`，不新增 Converter

### M7-D：契约测试参数化

- `INasConnectorContractTests` 8 个 [Fact] → 9 个 [Theory]+[MemberData]，TestConnection 拆成 returns_true 与 throws_with_kind 两个独立断言单元
- 新增 `ConnectorCase` 上下文类（封装 connector factory + 已知远端文件 + 期望失败 Kind + 期望 List 数量）；Stub 与 Smb-localTemp 走同一份断言
- 保留 `SmbNasConnectorContractTests` 作为 SMB 专属边界行为测试（RangeNotSatisfiable / idempotent Delete / Move cross-volume fallback / GetMetadata NotFound），避免参数化把分支塞进断言
- WebDav 不参与参数化（依赖 HttpMessageHandler 难以与本地文件 sandbox 统一），保留独立 14 项
- 测试 502 → 512

### 部署说明（绿联 UGOS）

- 控制面板 → 文件服务 → SMB 服务：默认开启（无需额外配置）
- 共享文件夹设置：建议建 `StellarHubShared` 顶级共享，子目录由 StellarHub 自行管理
- RootPath 填 `StellarHubShared/` 或 `StellarHubShared/子目录`（前导/可省略，工厂自动拆 share/subPath）
- 凭据：DPAPI CurrentUser 加密落 config.json，与 WebDav 一致；明文密码从不落盘

### 变更

- 版本号 0.1.1 → 0.2.0（次版本号 +0.1.0，修订号归零；新功能里程碑）
- `AppInfo.MilestoneLabel` → "M7 NAS SMB 协议扩展"
- 全量测试 512 项全绿，0 警告 0 错误

## [0.1.1] — 2026-09-30 M6 多用户管理（补全，代号 U6）

M6 服务层已完整但 UI 未接通的补丁版本：共享库画廊、本地图片一键提交、管理员审核队列、共享库只读详情与保存副本全部接通。不立大里程碑，修订号 +0.0.1。

### U6-A：共享库画廊视图

- 新增 `SharedPaths`：NAS 共享目录单一路径来源（thumbnails/originals 分片 + pending 容器）
- 新增 `SharedGalleryViewModel`：共享记录映射为画廊行，网格/列表、缩放指标复用本地画廊设置，支持按文件名/标签内存过滤
- 工具栏新增本地库/共享库分段切换（B1-SCOPE）；共享 ListBox（B2）渲染远端缩略图

### U6-B：本地图片一键提交到共享库

- 本地画廊右键新增「提交到共享库」：上传原图 + WebP 缩略图到 pending 容器并写 pending JSON
- 按 hash 查重（已存在/审核中均拒绝重复提交），不可提交的行自动禁用菜单项

### U6-C：管理员审核队列（W6）

- `INasConnector` 新增 `MoveAsync`：WebDAV 以 RFC4918 MOVE（Destination + Overwrite:T）实现，服务器不支持时自动降级「下载→上传→删除」
- `SubmissionService.ApproveAsync` 审核通过时把原图/缩略图归位到 originals/thumbnails 分片，成功后写 `SourceNasPath`；通过/拒绝均清理 pending 残留文件
- 新增审核队列窗口 W6（720×540）：缩略图 + 提交人/时间/尺寸/大小/标签 + ✓通过/✕拒绝
- 工具栏新增「🛡 审核队列」管理员入口与 pending 数字角标

### U6-D：共享详情与保存副本

- 新增 `SharedDetailViewModel`：B3 详情栏在共享作用域显示只读信息（文件名/格式/尺寸/大小/评分/分类/标签/提交者/审核人·时间/NAS 路径）
- 「保存副本到…」：SaveFileDialog + 从 NAS 下载原图，结果经通知中心反馈；「复制 NAS 路径」写剪贴板
- 共享作用域下搜索框防抖提交不再查本地数据库，转为共享项内存过滤

### 变更

- 版本号 0.1.0 → 0.1.1（补丁修订）
- `AppInfo.MilestoneLabel` → "M6 多用户管理（补全）"
- 全量测试 482 项全绿，0 警告 0 错误

## [0.1.0] — 2026-09-30 M6 多用户管理

引入 NAS 共享目录多用户体系：账号存 NAS 共享目录，管理员可管理用户、审核提交，所有用户共享同一份图库。不新建服务端，通过 WebDAV 读写 NAS JSON。

### M6-A：账号系统 + NAS 共享存储

- 新增 `UserRole`（Admin/User）、`UserAccount`、`SharedImageRecord`、`PendingSubmission` 模型
- 新增 `PasswordHasher`（PBKDF2 10000 轮 HMAC-SHA256，16B salt，32B hash，固定时间比较）
- 新增 `SharedStorageService`（NAS JSON 读写：accounts.json / library.json / pending/）
- 新增 `AccountService`（账号 CRUD + 初始化管理员 + 认证）
- 账号表存 NAS 共享目录，所有用户共享同一份
- 用户名 2-32 字符，密码至少 6 字符，唯一性不区分大小写

### M6-B：登录界面 + 启动流程

- 新增 `CurrentUser` 模型（登录用户上下文）
- 新增 `LoginViewModel` + `LoginWindow`（W4）：NAS 连接下拉 + 账号密码 + 本地模式 + 初始化管理员
- `App.xaml.cs` 启动流程改为 `LoginWindow` → 认证成功 → `ShellWindow`
- `INasConnector` 继承 `IDisposable`
- `ServiceRegistry` 注入 UserIdentity / CurrentUser / SharedStorage / Accounts

### M6-C：管理员用户管理界面

- `SettingsWindow` 新增 A7「用户管理」页签（仅管理员可见）
- 支持账号列表、新建用户、启用/禁用、重置密码、删除用户

### M6-D：NAS 共享库基础

- 新增 `SharedLibraryService`（library.json 读写 + 去重添加/删除/查询）
- `ShellViewModel` 集成共享图库加载（仅 NAS 登录用户可用）

### M6-E：数据提交审核流程

- 新增 `SubmissionService`（提交/列表/审核通过/拒绝）
- 审核通过后提交提升到共享图库并删除 pending 文件

### M6-F：权限校验集成

- 新增 `PermissionService`（登录/管理员/用户管理/审核/提交/查看权限）
- 管理员专属：用户管理、审核提交；普通用户：查看共享库、提交审核

### 变更

- 版本号 0.0.1 → 0.1.0（新增功能里程碑）
- `AppInfo.MilestoneLabel` → "M6 多用户管理"
- 全量测试 458 项全绿，0 警告 0 错误

## [0.0.1] — 2026-09-30 版本重编码基线

版本号从 1.6.0 重新编码为 0.0.1，启用新三段式版本编码规则。功能基线与 v1.6.0 完全一致（M0–M5 全部功能保留）。

### 变更

- 版本号 1.6.0 → 0.0.1（重新编码起始基线）
- 新增版本编码规则（记入用户记忆，所有开发项目通用）：
  - 小修改自动 +0.0.1（修订号）
  - 新增功能/里程碑推进自动 +0.1.0（次版本号，修订号归零）
  - 大版本号 1.0.0 需全部需求功能实现后询问用户确认
- csproj 新增 `<NoWarn>NU1902</NoWarn>` 抑制 SixLabors.ImageSharp 3.1.10 已知漏洞告警（不影响功能，项目无外网暴露面）
- `AppInfo.MilestoneLabel` → "版本重编码"

## [1.6.0] — 2026-09-30 M5 NAS 远程库（WebDAV + 三级缩略图 + 下载队列 + 弱网优化）

引入 NAS 远程库支持：WebDAV 协议接入群晖/威联通等 NAS 设备，远程图片可浏览/缩略图预览/下载到本地。零新增第三方依赖（仅用 BCL HttpClient + System.Xml）。平台抽象层（INasConnector / IFileSystemAccessor / IUserIdentity）为 M6 多用户/macOS 留路。

### M5-A：平台抽象层

- `Services/Nas/INasConnector.cs` 抽象：`TestConnectionAsync` / `ListAsync(path)` / `OpenReadAsync(path)` / `DownloadFileAsync(path, localPath, range)` / `UploadFileAsync` / `DeleteAsync`
- `INasConnectorFactory` 工厂接口（按 NasConnectionSpec 构建连接器）；`INasConnector` 与 `INasConnectorFactory` 都注入 DI（手写 ServiceRegistry）
- `RemoteFileSystemAccessor` 实现 IFileSystemAccessor（走 INasConnector.ListAsync/DownloadFileAsync）
- `IUserIdentity` + `UserIdentityService`：当前用户标识（M5 仅 stub，M6 接权限校验）

### M5-B：WebDAV 连接器

- `WebDavConnector : INasConnector`：基于 HttpClient，PROPFIND（Depth=1）列目录、GET 下载、PUT 上传、DELETE 删除
- `XNamespace D = "DAV:"` 解析 propstat（不硬编码前缀，兼容群晖/威联通命名差异）
- `ServerCertificateCustomValidationCallback` 适配自签名证书（.NET 5+ 强制要求，移除 legacy ServerCertificateValidationCallback）
- `WebDavConnectorFactory : INasConnectorFactory`：按 spec 构建 HttpClient（Basic Auth + 超时 + 证书回调）
- `DisposableWebDavConnector` 包装类：工厂托管 HttpClient 生命周期，dispose 时统一释放

### M5-C：NAS 连接配置 UI + 设置持久化

- `Models/NasConnectionSpec`：Id（单调递增整数，删除不复用，Max+1 分配）、Name、Host、Port、Username、EncryptedPassword（DPAPI CurrentUser 加密）、RootPath、UseTls
- `AppSettingsService.NasConnections` 列表持久化到 config.json；密码 SetNasPassword 用 ProtectedData.Protect(CurrentUser)，不回填 PasswordBox
- `UpdateNasConnection` 保留 EncryptedPassword 与 CreatedAt；密码仅经 SetNasPassword 修改
- `SettingsViewModel` 测试连接命令：调用 `INasConnectorFactory.Create(spec, plaintextPassword).TestConnectionAsync()` → 显示成功/失败
- `SettingsWindow.xaml` N 页签：连接列表 + 新增/编辑/删除/测试连接按钮；PasswordBox 切换 selection 时自动清空（防误改）

### M5-D：远程库扫描 + schema v6 迁移

- `Data/Schema.v6.sql`：images 表新增 `source TEXT NOT NULL DEFAULT 'local'` + `nas_connection_id INTEGER NULL` + 索引 `idx_images_source`
- `DbMigrator` CURRENT_VERSION 5→6；现有 v5 数据库补列时旧行 source='local'（DEFAULT 兜底）
- `ImageRecord` 模型新增 Source/NasConnectionId 字段；`ImageQueryService.ReadImage` 读取新列
- `RemoteLibraryScanner`：选 NAS 连接 → PROPFIND 递归列出图片（按扩展名过滤）→ 计算文件 hash（流式 Md5Hasher.ComputeStreamAsync，不读全字节 OOM）→ 写入 images 表（source='remote'，file_path=`nas://{connId}/{remotePath}`）
- `GalleryListItem.SourceBadge` 三态徽标：cloud（云端）> local（本地）> ai tag（AI 标签）> null

### M5-E：远程缩略图

- `IImageSource` 抽象：`OpenStreamAsync()` 返回图片字节流；`LocalImageSource`（包装 File.OpenRead）/ `RemoteImageSource`（走 INasConnector.OpenReadAsync 下载到 `%TEMP%\StellarHub\` → 解码 → 删除临时文件）
- `ThumbnailService.Request(hash, source, level, highPriority)` 重构：本地与远程走同一 IImageSource 接口
- 临时文件下载失败 try/finally 删除，不阻塞缩略图生成
- `GalleryThumbnailProvider` 缓存 INasConnector 按 NasConnectionId（一次 auth handshake 服务整个画廊 session）

### M5-F：三级缩略图（thumb/preview/original）+ LRU 缓存上限

- `Services/ThumbnailLevel.cs` enum：Thumb=0（256px）/ Preview=1（1024px）
- `ThumbnailService` 多级支持：`GetThumbnailPath(hash, level)` 目录布局 `<hash>_thumb.webp` / `<hash>_preview.webp`；`Request` / `Regenerate` 接受 level 参数；`ThumbnailReady` 事件签名 `Action<string, ThumbnailLevel>`
- `_jobs` 字典按复合 key `$"{hash}:{(int)level}"` 索引（同 hash 可并行 thumb 与 preview 两个 job）
- `MigrateLegacyThumbnails()`：启动迁移旧 `<hash>.webp` → `<hash>_thumb.webp`（幂等，跳过已带 _thumb/_preview 后缀的文件与 placeholder）
- `ThumbnailCacheService`：后台 Timer（5s 延迟 + 1h 周期）扫描 thumbnails/**/*.webp，按 LastAccessTime 排序删除最旧文件至 `ThumbnailCacheLimitMb` 阈值内；跳过 placeholder.webp、.partial、.tmp
- `AppSettingsService.ThumbnailCacheLimitMb` 钳制范围 256-16384 → 128-8192（M5-F 调整以适配多级缓存）
- `Views/Controls/ProgressiveImage.xaml(.cs)`：三层 Image 渐显控件（PlaceholderLayer 256px → PreviewLayer 1024px → FinalLayer 原图），DoubleAnimation 200ms QuadraticEase 淡入；上层淡入完成后才折叠下层避免闪烁
- `GalleryItemViewModel.PreviewImage` 属性（1024px 预览）；`DetailViewModel.OnSelectionChanged` 切换时清理前一项 PreviewImage + ClearPreviewRequest，新选项 RequestPreview

### M5-G：下载队列 + 断点续传 + 失败重试

- `Data/Schema.v7.sql`：download_queue 表（id/image_id/source/nas_connection_id/remote_path/local_path/total_bytes/downloaded_bytes/status/error_message/retry_count/created_at/started_at/updated_at）+ 索引 `idx_download_queue_status`
- `DbMigrator` CURRENT_VERSION 6→7
- `Models/DownloadQueueItem` 行模型 + 路由字段（Source/NasConnectionId）
- `DownloadQueueService`：2 并发 worker（SemaphoreSlim），`.part` 续传（Range header 4MB 分块），416 → 删 .part 重启；3 次指数退避重试；Cancel 删除 .part
- `DownloadQueueViewModel` + `DownloadQueueWindow`：D1-D4 布局（队列/进度条/状态/操作），ProgressBar 绑只读计算属性必须 Mode=OneWay（U3 教训）
- ShellWindow 工具栏「📥 下载队列」按钮 + `ShellViewModel.ShowDownloadQueueCommand`

### M5-H：弱网检测 + 预加载邻近图片 + 三级淡入

- `Services/NetworkConditionMonitor.cs`：10 样本滑动窗口，失败率 >50% 或平均延迟 >3000ms → IsWeakNetwork=true；Changed 事件仅在状态翻转时触发；MIN_SAMPLES=3 启动守卫避免单次握手失败锁死画廊
- `ThumbnailService.RequestPrefetch`：低优先级批量入队，已存在缩略图跳过，空/whitespace hash 静默丢弃
- `GalleryThumbnailProvider`：构造接受 NetworkConditionMonitor；Attach/Prefetch 在弱网下跳过远程请求（本地项不受影响）；新增 Prefetch 方法
- `GalleryViewModel.RefreshAsync` 在 Refreshed?.Invoke() 后调用 Thumbnails.Prefetch(Items) 预取当前页周边缩略图

### M5-Z：收尾

- `QL.World.csproj` `<Version>` 1.5.0 → 1.6.0（UTF-8 BOM 保留）
- `AppInfo.MilestoneLabel` "U5 品牌升级" → "M5 NAS 远程库"
- `CHANGELOG.md` 顶部新增 [1.6.0] 节
- `GEMINI.md` 里程碑状态更新
- `docs/UI_NUMBERING.md` 补齐 N + D 系列编号
- `docs/USER_GUIDE.md` 新增 NAS / 远程库扫描 / 下载队列章节
- `README.md` + `AGENTS.md` 顶部功能描述同步
- 门禁：build.ps1 -Mode Prepare（0 警告 + 全测试绿 + R2R publish 成功）
- staging 体积对比：v1.5.0 190.46MB → v1.6.0 190.78MB（+0.32MB，零新依赖，仅 C# 代码 + schema v6/v7.sql embedded resource + R2R 代码量增量）

## [1.5.0] — 2026-09-29 U5 品牌升级补丁（QL World → 千星汇图 / StellarHub）

品牌升级为千星汇图（StellarHub），为 M5 NAS 远程库扫清品牌基线。不引入新功能依赖，不动业务逻辑，PRD 核心功能 100% 基线不变。

### U5-A：显示层与程序集品牌

- `AppInfo.ProductName` "QL World" → "千星汇图"；新增 `EnglishName = "StellarHub"`；`MilestoneLabel` "U4 发布基线" → "U5 品牌升级"
- `QL.World.csproj` `<AssemblyName>` "QL World" → "StellarHub"（exe 产物 `StellarHub.exe`）；新增 `<Product>`、`<AssemblyTitle>`、`<Copyright>`、`<Description>`；`<RootNamespace>QL.World` 保持不变，所有命名空间与 XAML xmlns 不受影响
- `ShellWindow.xaml` A1 Logo 文字 → 千星汇图；窗口标题已绑 `{Binding Title}` 自动跟随 `AppInfo.ProductName`
- `SettingsWindow.xaml` 标题 `设置 — QL World` → `设置 — 千星汇图`；A5 关于页产品名同步
- `LegacyPythonImporter` 错误消息同步品牌名
- `Theme` 注释与 `App.xaml.cs` 内部日志标签保留 "QL World"（不影响用户感知）

### U5-B：发布脚本与五件套命名

- `build.ps1` staging 与 dist 路径前缀 `QL World_v$Version` → `StellarHub_v$Version`；顶部脚本注释同步
- `ReleaseArtifacts.cs` 7 处版本目录/五件套文件名常量改为 `StellarHub_v{ver}`；fallback 标题 `BuildMinimalReleaseNotes`/`BuildMinimalUserGuide` → 千星汇图 v{version}
- `ReleaseContractTests` 五件套断言路径与 zip 名同步；fake staging 中 `QL World.dll/.exe/.runtimeconfig.json` → `StellarHub.*`；UserGuidePath 测试内容用千星汇图品牌名
- `tools/smoke/UiSmokeA3.ps1` + `UiSmokeU3.ps1` 启动 exe 名 → `StellarHub.exe`
- `run.ps1` L89 用 `$runtimeConfig.Name` 动态推导 exe 名，自动适配 StellarHub.exe 无需改
- 生产代码 `DebugUiTestMode.cs` mutex/event 名（`Global\QL_World_SingleInstance` / `Local\QL_World_GracefulShutdown`）保留 —— 用户不可见且改名有升级期单实例保护短暂失效副作用，留待后续单独评估
- `RELEASE_NOTES.md` v1.0.0 历史发布记录保持原样（不篡改历史）

### U5-C：数据目录迁移

- `AppDataRootResolver` `ProductFolder` "QL_World" → "StellarHub"；新增 `LegacyProductFolder = "QL_World"` 作迁移源；新增 `.migrated` / `.migration-failed` 标记
- 一次性迁移逻辑（`TryMigrate`）：已 `.migrated` 直接返回新路径；legacy 不存在或无白名单条目视为新装不写标记；有内容则逐项 `File/Directory.Move` 同卷原子；任一失败回滚已移动项 + 写 `.migration-failed` 到 legacy + 返回 legacy 路径，启动不崩；全成功才写 `.migrated`
- `MigratableEntries` 白名单覆盖 db / `-wal` / `-shm` / `.backup` / config / `.backup` / thumbnails / models / logs；未知 stray 文件留在 legacy 供用户手动处理
- `run.ps1` L103 错误消息路径 `Qianlan_App\QL_World\` → `Qianlan_App\StellarHub\`
- DEBUG 下传非空 debugOverride 仍短路优先（测试隔离不变）
- `DatabaseFileName = "qlworld.db"` / `ConfigFileName = "config.json"` 保持不变（内部文件名用户不可见，改名徒增迁移复杂度）
- `AppDataRootResolverTests` 由 2 用例升级为 6 用例：Debug 优先级 / 全新安装无标记 / 空 legacy 视为新装 / 首次迁移成功 / 幂等跳过 / 失败 fallback 写失败标记（FileStream FileShare.None 锁源触发 IOException 验证回滚与降级）

### U5-Z：收尾

- `<Version>` 1.4.0 → 1.5.0；`AppInfo.MilestoneLabel = "U5 品牌升级"`（U5-A 已改）
- `CHANGELOG.md` 顶部新增 [1.5.0] 节
- `GEMINI.md` 第 1 节项目身份 + 里程碑状态更新（品牌升级说明）
- `docs/USER_GUIDE.md` 品牌引用同步（标题、首段、12.5 数据目录路径、12.6 进程名、1.2 数据目录路径迁移说明）
- `README.md` 顶部产品名同步
- `AGENTS.md` 无 QL World 引用无需改
- `docs/UI_NUMBERING.md` 无 QL World 引用无需改

## [1.4.0] — 2026-09-29 U4 主色调收尾（PRD 核心功能 100%）

### U4-A：B3-4 详情栏主色调色块

- 详情栏评分行下新增「主色调」行：≤5 个 20×20 圆角色块横向排列，数据来自
  images.dominant_colors（ColorExtractor K-means，A1-8「分析颜色」批量回填），
  tooltip 显示对应 #RRGGBB；1px 主题 BorderSubtleBrush 描边保证浅色块可辨
- DetailViewModel 解析 dominant_colors JSON：null/空/损坏一律空列表，
  HasDominantColors=false 时整行 BoolVis 隐藏；选中切换随记录刷新；
  多选批量编辑态随单选面板天然隐藏
- 新增 HexBrushConverter（#RRGGBB → SolidColorBrush，非法输入 → Transparent）
- UI_NUMBERING：B3-4 → 已实现（U4-A），PRD 核心功能自此 100% 覆盖；
  父行 B3/C1 过时「M1 部分」措辞修正

## [1.3.0] — 2026-09-30 U3 UI 优化补丁

### U3-A：§4.B.2 左栏属性筛选折叠组（B1-5）

- 左栏新增「属性筛选」折叠组（默认收起，折叠态持久化），四维单选 chips
  （各含「全部」，再点已选清除），与 A1/B1 其它筛选 AND 叠加
- 颜色：9 色族（红/橙/黄/绿/青/蓝/紫/品红/灰）——Schema.v5 新增
  images.color_family + 索引，由 dominant_colors 首色按 HSV 感知扇区
  纯计算回填（DbMigrator 同事务），ColorAnalysisService 同窗写入；
  未分析图片不匹配任何色族
- 分辨率按长边 MAX(width,height) 分档（<1024 / 1024–2047 / 2048–4095 /
  ≥4096，NULL 不匹配）；文件大小四档（<1MB/1–5MB/5–20MB/≥20MB）；
  评分四档（未评分/≥3★/≥4★/5★）
- 新增 ColorFamily 纯函数、AttributeFilterViewModel、FilterChipStyle、
  SidebarSectionHeaderStyle；计数条摘要追加四维
- UI_NUMBERING 新增 B1-5 / B1-5-1~4

### U3-B：§4.C.1 卡片 hover 操作组（B2-3-ACT）

- 网格卡片缩略图底部三按钮浮层：打开原文件 ↗ / 资源管理器定位 ⌖ /
  复制路径 ⧉；OverlayBrush 底 + hover 浮出、移出即隐（Collapsed 不参与
  Measure，虚拟化安全）
- 参数化命令作用于悬停卡片而非当前选中；缺失卡 CanExecute 排除；
  列表态不加
- UI_NUMBERING 新增 B2-3-ACT

### U3-C：§4.E.3 B1 四分区折叠+折叠态计数（B1-COL）

- 素材库/收藏夹/热门标签/贴图组标题改为 ToggleButton（▾/▸），
  折叠态持久化（默认全展开）；折叠时标题显示子项总数「名称 (N)」，
  计数随树刷新实时更新
- 修复设置序列化缺陷：WhenWritingDefault 吞掉 false，默认 true 的 bool
  （CheckIntegrityOnStartup/AutoImportOnWatch/四个折叠态）写 false 后
  重载回落 true——六属性加 JsonIgnore(Never) 并补 round-trip 回归测试
- UI_NUMBERING 新增 B1-COL

## [1.2.0] — 2026-09-30 U2 UI 优化补丁

### U2-A：品牌视觉

- A1 Logo 文字块改为 AccentGradientBrush（Accent→AccentDim 线性渐变），与品牌色统一
- A1 AI 按钮 hover 切换为 AccentGradientBrush 背景 + TextOnAccentBrush 文字
- C1 状态栏新增细进度条（2px IsIndeterminate，AccentGradientBrush 前景），
  StatusBarViewModel.IsLoading 每 500ms 刷新（导入/AI 运行/缩略图挂起时显示）
- 主题新增 AccentGradientBrush、TextOnAccentBrush、AiButtonStyle

### U2-B：§4.B.1 顶栏「最近入库」下拉

- TopBarViewModel 新增 RecentImportOptions 与 SelectedRecentImport
  （今天/近 7 天/近 30 天/全部），持久化到 AppSettings.GalleryRecentImportDays
- LibraryFilter 新增 ImportedWithinDays（null=全部），
  ImageQueryService 以 i.created_at >= cutoff 过滤
- BuildSummary 显示「今天」/「近 N 天」
- UI_NUMBERING 新增 B2-3-OL1

### U2-C：§4.B.3 详情栏「相关资产」section

- DetailViewModel 新增 RelatedAssets（前 6 个语义相似缩略图）+ HasRelatedAssets
- 新增 FindSimilarRequested 事件与 SelectRelatedCommand；
  ShellViewModel.FindSimilarAsync 支持按 imageId 查找相似
- B3-9 ItemsControl 水平 VirtualizingStackPanel，64×64 缩略图按钮
- UI_NUMBERING 新增 B3-9

### U2-D：§4.C.2 类别/状态徽标（完整三态）

- Schema.v4：image_embeddings 新增 source TEXT NOT NULL DEFAULT 'local'
  + 索引；DbMigrator CURRENT_VERSION 3→4；历史 embedding 自动回填 'local'
- AiAnalysisService.PersistEmbedding 写 source 列，
  EmbeddingSource 从 IAiService.Mode 映射（Cloud→cloud，其余→local）
- ImageQueryService.QueryGallery LEFT JOIN image_embeddings + CASE 计算
  source_badge：cloud→云端 / local→本地 / 无 embedding 但有 AI tag→AI / 否则 null
- GridCardTemplate 缩略图右下角 + ListRowTemplate 缩略图右上角加徽标 Border
  （OverlayBrush 底 + TextOnOverlayBrush 文字），null 时整段隐藏
- 主题新增 TextOnOverlay 色 + TextOnOverlayBrush（Dark #E6E6EC / Light #1A1A22）

### U2-E：§4.D.1 卡片元数据条

- GalleryItemViewModel 新增 TagsSummary 计算属性：取前 2 标签以「, 」连接，
  超过 2 个追加「 +N」，无标签返回空串
- GridCardTemplate StackPanel 新增第三行标签摘要 TextBlock
  （TextMutedBrush + CharacterEllipsis + NullVis 隐藏）

### U2-F：§4.E.2 品牌化版本号

- AppInfo 新增 MilestoneLabel = "U2 发布基线"
- ShellViewModel.VersionText 改为 $"v{Version} · {MilestoneLabel}"
- SettingsViewModel.VersionText 保持纯语义版本（关于页不变）

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
