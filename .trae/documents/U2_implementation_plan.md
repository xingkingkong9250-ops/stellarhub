# U2 实现计划 —— UI 优化里程碑 2

> 范围：[ui_optimization_target.md](file:///c:/AI_Programming/APP/QL_World_dotnet/.trae/documents/ui_optimization_target.md) P1 项落地
> 代号：U2（UI patch 2）；子阶段 U2-A / U2-B / U2-C / U2-D / U2-E / U2-F / U2-Z
> 起始版本：v1.1.0（U1 发布基线）
> 收尾版本：v1.2.0（U2 完成后；UI 可见改进 + schema 迁移走 minor 升位）

---

## 0. 关键决策（已与用户确认）

| 项 | 决策 | 影响 |
|---|---|---|
| §4.C.2 来源徽标 | **完整三态**：「本地」/「云端」/「AI」 | 需 schema v4 迁移：`image_embeddings` 加 `source` 列（local/cloud）；AiAnalysisService 写入时记录档位；ImageQueryService 查询 JOIN；GalleryItem 暴露 SourceBadge |
| §4.A.2 加载进度条 | **新增 C1 薄进度条**：2px IsIndeterminate ProgressBar，导入/AI/缩略图生成期间显示 | StatusBarViewModel 新增 IsLoading；订阅 AiAnalysisService.IsRunning + ThumbnailService.PendingCount + ImportService.IsImporting；AccentGradientBrush 作 Foreground |

---

## 1. U2-A：§4.A.2 品牌渐变位

### 目标
新建 `AccentGradientBrush`（#7C5CFF → #A48BFF），用于三处品牌位：① A1 标题栏「QL World」前渐变 Logo 方块；② AI 分析按钮 hover 态；③ C1-C 薄进度条（IsIndeterminate）。

### 改动文件
- [src/QL.World/Themes/Dark.xaml](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Themes/Dark.xaml) — 新增 `<LinearGradientBrush x:Key="AccentGradientBrush">`（#7C5CFF→#A48BFF，横向）
- [src/QL.World/Themes/Light.xaml](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Themes/Light.xaml) — 同上（Light 色 #6A4DFF→#9275FF，保证 Dark/Light 对比度均达标）
- [src/QL.World/Views/ShellWindow.xaml](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Views/ShellWindow.xaml) — A1 Logo 方块（24×24 Border，Background=AccentGradientBrush，CornerRadius=6）；AI 分析按钮 Style 加 hover trigger 用 AccentGradientBrush；C1-C 下方加 2px ProgressBar
- [src/QL.World/ViewModels/StatusBarViewModel.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/ViewModels/StatusBarViewModel.cs) — 新增 `IsLoading` bool；订阅 AiAnalysisService.IsRunning、ThumbnailService（PendingCount>0）、ImportService（若暴露事件，否则 ShellViewModel 中转）
- [src/QL.World/ViewModels/ShellViewModel.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/ViewModels/ShellViewModel.cs) — 若 ImportService 事件不便直接订阅，ShellViewModel 转发到 StatusBarViewModel.IsLoading
- [tools/smoke/UiSmokeA1.ps1](file:///c:/AI_Programming/APP/QL_World_dotnet/tools/smoke) — 断言 Logo 方块可见 + 进度条默认隐藏

### 实现要点
- **Logo 方块**：在 A1 `<TextBlock Text="QL World">` 前加 `<Border Width="24" Height="24" CornerRadius="6" Background="{DynamicResource AccentGradientBrush}"/>`，Margin="0,0,10,0"
- **AI 按钮 hover**：在 SubtleButtonStyle 或新派生 Style 加 `<Trigger Property="IsMouseOver" Value="True"><Setter Property="Background" Value="{DynamicResource AccentGradientBrush}"/></Trigger>`；仅 A1-2「AI 分析」按钮引用此 Style，不全局应用
- **C1 薄进度条**：在 C1 `<Grid>` 内 StackPanel(Center) 下方加 `<ProgressBar Height="2" IsIndeterminate="True" Visibility="{Binding StatusBar.IsLoading, Converter={StaticResource BoolVis}}" Foreground="{DynamicResource AccentGradientBrush}" Background="Transparent" BorderThickness="0" />`；GridSpan 整个 C1 宽度
- **IsLoading 聚合**：StatusBarViewModel 暴露 `IsLoading`，由 ShellViewModel 在各服务事件中 `statusBar.IsLoading = ai.IsRunning || thumbnails.PendingCount > 0 || isImporting`；避免 StatusBarViewModel 直接依赖三个服务（保持松耦合）
- **不扩大使用范围**：正文背景、卡片底色、列表选中态等均不用渐变

### 验收
- 启动应用 → A1 左上角渐变 Logo 方块可见
- 鼠标悬停「AI 分析」按钮 → 背景渐变（仅此按钮）
- 触发导入或 AI 分析 → C1 底部 2px 进度条显示，完成后隐藏
- 主题切换 Dark↔Light → 渐变色随主题变化且对比度达标

---

## 2. U2-B：§4.B.1 顶栏「最近入库」下拉

### 目标
A1 区增加「最近入库 ▾」下拉（今天 / 7 天 / 30 天 / 全部），映射到 LibraryFilter.ImportedWithinDays；持久化到 AppSettings。

### 改动文件
- [src/QL.World/Models/LibraryFilter.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Models/LibraryFilter.cs) — 新增 `int? ImportedWithinDays`（null=不限）；Clone 同步
- [src/QL.World/Services/ImageQueryService.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Services/ImageQueryService.cs) — AppendClauses 加分支：`i.created_at >= $cutoff`，cutoff = DbTime.Now() 减 N 天
- [src/QL.World/Models/AppSettings.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Models/AppSettings.cs) — 新增 `int? GalleryRecentImportDays`（null=全部，默认 null）
- [src/QL.World/ViewModels/TopBarViewModel.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/ViewModels/TopBarViewModel.cs) — 新增 `RecentImportOptions` 列表 + `SelectedRecentImport`（int? null）；setter 同步到 `_gallery.Filter.ImportedWithinDays` + RefreshAsync + 持久化到 settings
- [src/QL.World/ViewModels/GalleryViewModel.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/ViewModels/GalleryViewModel.cs) — 构造时从 settings 恢复 ImportedWithinDays 到 Filter
- [src/QL.World/Views/ShellWindow.xaml](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Views/ShellWindow.xaml) — A1 右侧 StackPanel 在搜索框前加 `ComboBox Width="120" ItemsSource="{Binding TopBar.RecentImportOptions}" DisplayMemberPath="Display" SelectedValuePath="Days" SelectedValue="{Binding TopBar.SelectedRecentImport}"`
- [docs/UI_NUMBERING.md](file:///c:/AI_Programming/APP/QL_World_dotnet/docs/UI_NUMBERING.md) — 新增 A1-9
- [tools/smoke/UiSmokeA1.ps1](file:///c:/AI_Programming/APP/QL_World_dotnet/tools/smoke) — 断言下拉可见 + 切换 7 天触发画廊刷新

### 实现要点
- **选项模型**：`record RecentImportOption(int? Days, string Display)`，列表：`[(null,"全部"),(1,"今天"),(7,"最近 7 天"),(30,"最近 30 天")]`
- **cutoff 计算**：DbTime.Now() 返回 UTC "yyyy-MM-dd HH:mm:ss"；`var cutoff = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd HH:mm:ss")`；参数化绑定
- **AppendClauses 位置**：在 Semantic 分支之后、OnlyMissing 之前，作为独立分支；与其它 WHERE 用 AND 串联
- **持久化**：TopBarViewModel.SelectedRecentImport setter 调用 `_settings.GalleryRecentImportDays = value; _settings.Save()`
- **ComboBox 默认值**：从 settings 恢复；首次启动为 null（「全部」）

### 验收
- 启动应用 → A1 显示「最近入库 ▾」下拉，默认「全部」
- 选「今天」→ 画廊仅显示今日导入；B2-1 计数更新
- 切「全部」→ 恢复全库；重启应用保持上次选择
- 与搜索/分类筛选可叠加（AND）

---

## 3. U2-C：§4.B.3 详情栏「相关资产」section

### 目标
B3 底部新增 B3-9 横向缩略图条（top 6），复用 SemanticSearchService.SearchByImageId 缓存；无 embedding 时整段隐藏。

### 改动文件
- [src/QL.World/ViewModels/DetailViewModel.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/ViewModels/DetailViewModel.cs) — 注入 SemanticSearchService + ImageQueryService（已有 _query）+ ThumbnailService；新增 `RelatedAssets` ObservableCollection + `HasRelatedAssets` bool；ReloadSingleAsync 末尾异步加载
- [src/QL.World/ViewModels/DetailViewModel.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/ViewModels/DetailViewModel.cs) — 新增 `RelatedAssetItem` VM（Id, FileName, ThumbnailImage, ClickCommand）
- [src/QL.World/Services/ThumbnailService.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Services/ThumbnailService.cs) — 若需同步获取单张缩略图（已有 TryGetCache 之类接口），复用；否则 DetailViewModel 通过 GalleryThumbnailProvider
- [src/QL.World/Views/ShellWindow.xaml](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Views/ShellWindow.xaml) — B3 Single ScrollViewer 末尾加「相关资产」section：ItemsControl 横向 ListBox（6 张 64×64 缩略图）
- [docs/UI_NUMBERING.md](file:///c:/AI_Programming/APP/QL_World_dotnet/docs/UI_NUMBERING.md) — 新增 B3-9
- [tools/smoke/UiSmokeB1.ps1](file:///c:/AI_Programming/APP/QL_World_dotnet/tools/smoke) — 断言：选中有 embedding 的图 → B3-9 可见且 6 张；选中无 embedding 的图 → B3-9 隐藏

### 实现要点
- **数据加载**：`ReloadSingleAsync` 末尾 `_ = LoadRelatedAsync(record.Id)`；`LoadRelatedAsync` 调 `_semanticSearch.SearchByImageId(id)`，取前 6，对每个 id 用 `_query.GetById` + 缩略图服务加载
- **整段隐藏**：`HasRelatedAssets` 绑定到 section 的 Visibility；SearchByImageId 返回 null/空 → HasRelatedAssets=false
- **点击行为**：点击相关资产缩略图 → ShellViewModel 的「在画廊中选中此图」命令（复用现有 Gallery.SelectById 若有，否则简单触发 FindSimilar 切换语义档）
- **性能**：SemanticSearchService 已有内存缓存（千级×512×4B=2MB）；6 张缩略图异步加载不阻塞 UI
- **不与 B3-8 重复**：B3-8「找相似」按钮仍触发画廊语义档切换；B3-9 是 B3 内嵌预览，两者并存

### 验收
- 选中已有 embedding 的图 → B3 底部出现「相关资产」+ 6 张缩略图
- 选中无 embedding 的图 → 整段隐藏不报错
- 点击相关资产 → 该图被选中（或触发画廊定位）
- 切换选中 → 相关资产列表更新

---

## 4. U2-D：§4.C.2 类别/状态徽标（完整三态）

### 目标
卡片缩略图右下角增加来源徽标：「本地」/「云端」/「AI」三态；schema 加 source 列记录 embedding 档位。

### 改动文件
- [src/QL.World/Data/Schema.v4.sql](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Data/Schema.v4.sql)（新建）— `ALTER TABLE image_embeddings ADD COLUMN source TEXT NOT NULL DEFAULT 'local';` + 索引 `CREATE INDEX IF NOT EXISTS idx_image_embeddings_source ON image_embeddings(source);`
- [src/QL.World/Data/DbMigrator.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Data/DbMigrator.cs) — CURRENT_VERSION 3→4
- [src/QL.World/Services/Ai/AiAnalysisService.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Services/Ai/AiAnalysisService.cs) — PersistEmbedding 加 `source` 参数；INSERT 列加 source；source 由 `_ai` 档位决定（LocalAiService→"local"，CloudAiService→"cloud"，Disabled→"local" fallback 不写）
- [src/QL.World/Services/Ai/IAiService.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Services/Ai/IAiService.cs) 或实现类 — 暴露 `AiMode Mode { get; }` 属性，供 AiAnalysisService 判断 source
- [src/QL.World/Services/ImageQueryService.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Services/ImageQueryService.cs) — QueryGallery SQL 加 LEFT JOIN image_embeddings e ON e.image_id=i.id，SELECT e.source AS embedding_source；加 EXISTS 子查询检查 image_tags ai source；GalleryListItem 加 SourceBadge 计算字段
- [src/QL.World/Models/ImageRecord.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Models/ImageRecord.cs) 或 GalleryListItem — 新增 `SourceBadge` 字段（string? null=无徽标 / 「本地」/「云端」/「AI」）
- [src/QL.World/ViewModels/GalleryItemViewModel.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/ViewModels/GalleryItemViewModel.cs) — 暴露 SourceBadge
- [src/QL.World/Views/ShellWindow.xaml](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Views/ShellWindow.xaml) — GridCardTemplate 缩略图 Border 右下角加徽标 Border（半透明 OverlayBrush 底 + 反色文字）；ListRowTemplate 列表态在缩略图右上角加同款
- [docs/UI_NUMBERING.md](file:///c:/AI_Programming/APP/QL_World_dotnet/docs/UI_NUMBERING.md) — 新增 B2-3-OL1
- [tools/smoke/UiSmokeB1.ps1](file:///c:/AI_Programming/APP/QL_World_dotnet/tools/smoke) — 断言：有 embedding 的图卡显示「本地」或「云端」徽标；无任何 AI 数据的图卡无徽标

### 实现要点
- **source 列默认 'local'**：历史 embedding（M3-D/E 生成）全部由本地 ONNX 产生，ALTER TABLE 默认值 'local' 自动回填
- **徽标优先级**：embedding_source='local' → 「本地」；='cloud' → 「云端」；无 embedding 但 EXISTS image_tags where source='ai' → 「AI」；否则 null（无徽标）
- **IAiService.Mode 暴露**：LocalAiService 已知是 Local；CloudAiService 已知是 Cloud；DisabledAiService 返回 Disabled（不写 embedding）
- **SQL 计算**：在 QueryGallery 的 SELECT 子句加
  ```sql
  CASE
    WHEN e.source = 'local' THEN '本地'
    WHEN e.source = 'cloud' THEN '云端'
    WHEN EXISTS (SELECT 1 FROM image_tags it WHERE it.image_id = i.id AND it.source = 'ai') THEN 'AI'
    ELSE NULL
  END AS source_badge
  ```
  + LEFT JOIN image_embeddings e ON e.image_id = i.id
- **徽标渲染**：GridCardTemplate 缩略图 Border 内右下角 HorizontalAlignment=Right VerticalAlignment=Bottom Margin=4 Padding=4,1 CornerRadius=4 Background={DynamicResource OverlayBrush}；TextBlock FontSize=10 Foreground={DynamicResource TextOnOverlayBrush}（新增主题色，Dark #E6E6EC / Light #1A1A22，对 OverlayBrush ≥4.5:1）；Visibility 绑定 SourceBadge != null
- **ListRowTemplate**：缩略图 Border 内右上角同款徽标（列表态缩略图小，右上更显眼）
- **迁移幂等**：ALTER TABLE ADD COLUMN 在 SQLite 中幂等性靠 PRAGMA user_version 保护（v4 只跑一次）；不要用 IF NOT EXISTS（SQLite ALTER TABLE 不支持）

### 验收
- 现有库（v3 schema）启动 → 自动迁移到 v4，历史 embedding 标记为「本地」
- 有 embedding 的图卡显示「本地」或「云端」徽标
- 仅被 AI 打过标签无 embedding 的图卡显示「AI」徽标
- 完全无 AI 数据的图卡无徽标
- 主题切换 → 徽标对比度达标

---

## 5. U2-E：§4.D.1 卡片元数据条

### 目标
GridCardTemplate 增加第三行「标签摘要（前 2 个 + +N）」。

### 改动文件
- [src/QL.World/Services/ImageQueryService.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Services/ImageQueryService.cs) — GalleryListItem 已有 TagsCsv；无需改 SQL
- [src/QL.World/Models/ImageRecord.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Models/ImageRecord.cs) 或 GalleryListItem 定义处 — 新增 `TagsSummary` 计算属性（从 TagsCsv split 取前 2 + 「+N」）
- [src/QL.World/ViewModels/GalleryItemViewModel.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/ViewModels/GalleryItemViewModel.cs) — 暴露 TagsSummary（只读计算属性）
- [src/QL.World/Views/ShellWindow.xaml](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Views/ShellWindow.xaml) — GridCardTemplate StackPanel 加第三行 TextBlock Text={Binding TagsSummary} Foreground=TextMutedBrush FontSize=11 TextTrimming=CharacterEllipsis；Grid RowDefinitions 加第三行 Auto
- [tools/smoke/UiSmokeB1.ps1](file:///c:/AI_Programming/APP/QL_World_dotnet/tools/smoke) — 断言：3+ 标签的图卡显示「tag1, tag2 +1」

### 实现要点
- **TagsSummary 计算**：`TagsCsv.Split(',').Take(2)`；若 `tags.Length > 2` 则 `$"{string.Join(", ", first2)} +{tags.Length - 2}"`；若 0 标签返回 string.Empty（XAML 用 ZeroVis 隐藏）
- **GridCardTemplate Grid.RowDefinitions**：当前 2 行（Auto, Auto）→ 3 行（Auto, Auto, Auto）；第三行 TextBlock 在 StackPanel 内（已是 Row 1 的 StackPanel），直接追加即可，无需改 Grid 结构
- **列表态不显示**：ListRowTemplate 已有 TagsCsv 显示（第二列），不重复

### 验收
- 有 3+ 标签的图卡 → 第三行显示「tag1, tag2 +1」
- 有 1-2 标签 → 显示「tag1, tag2」
- 无标签 → 第三行隐藏（不占空间）
- 网格缩放 → 标签摘要随卡片宽度自适应

---

## 6. U2-F：§4.E.2 品牌化版本号

### 目标
C1 右侧（实际 A1 右侧版本号位置）版本号后增加里程碑标签（如 `v1.2.0 · U2 发布基线`）。

### 改动文件
- [src/QL.World/Helpers/AppInfo.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Helpers/AppInfo.cs) — 新增 `public const string MilestoneLabel = "U2 发布基线";`（每个里程碑手动更新）
- [src/QL.World/ViewModels/ShellViewModel.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/ViewModels/ShellViewModel.cs) — VersionText 改为 `$"v{AppInfo.Version} · {AppInfo.MilestoneLabel}"`
- [tools/smoke/UiSmokeA1.ps1](file:///c:/AI_Programming/APP/QL_World_dotnet/tools/smoke) — 断言 A1 右侧文本含「U2 发布基线」

### 实现要点
- **不解析 csproj Description**：目标文档「不强制」；硬编码 const 简单可靠，每个里程碑 X-Z 阶段更新
- **SettingsViewModel.VersionText 不改**：W3/A5 关于页保持纯版本号 `v1.2.0`，不带里程碑标签（关于页是技术信息，A1 是品牌位）

### 验收
- 启动应用 → A1 右侧显示「v1.2.0 · U2 发布基线」
- W3/A5 关于页仍显示「v1.2.0」（无标签）

---

## 7. U2-Z：收尾

按序执行：

1. [src/QL.World/QL.World.csproj](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/QL.World.csproj) `<Version>` 1.1.0 → 1.2.0
2. [src/QL.World/Helpers/AppInfo.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Helpers/AppInfo.cs) MilestoneLabel → 「U2 发布基线」（若 U2-F 已设可跳过）
3. [CHANGELOG.md](file:///c:/AI_Programming/APP/QL_World_dotnet/CHANGELOG.md) 顶部新增 `## [1.2.0] - 2026-09-2X` 节，按 U2-A~F 子阶段组织条目
4. [GEMINI.md](file:///c:/AI_Programming/APP/QL_World_dotnet/GEMINI.md) 里程碑状态补「U2 UI 优化（P1 项）」
5. [docs/UI_NUMBERING.md](file:///c:/AI_Programming/APP/QL_World_dotnet/docs/UI_NUMBERING.md) 补齐 U2 新增编号（A1-9 / B3-9 / B2-3-OL1）
6. [ui_optimization_target.md](file:///c:/AI_Programming/APP/QL_World_dotnet/.trae/documents/ui_optimization_target.md) §4.A.2/§4.B.1/§4.B.3/§4.C.2/§4.D.1/§4.E.2 节末尾标注「已落地（U2-X）」
7. 门禁：`powershell -File build.ps1 -Mode Prepare`（0 警告 0 错误 + 测试全绿 + R2R 成功）；新增 Schema.v4.sql 迁移需有对应测试（ImageQueryServiceTests 加 source 列断言）
8. staging 体积记录并对比 v1.1.0（预期不变或 +少量 KB，无新依赖）
9. 向用户交付总结：commit 列表、门禁结果、关键实现说明、未执行/需人工验收项
10. 记忆沉淀：实测发现的可复用坑写入 project_memory.md

---

## 8. 阶段提交顺序

1. U2-A 品牌渐变位 — 单 commit
2. U2-B 最近入库下拉 — 单 commit
3. U2-C 相关资产 section — 单 commit
4. U2-D 类别/状态徽标（含 schema v4 迁移） — 单 commit
5. U2-E 卡片元数据条 — 单 commit
6. U2-F 品牌化版本号 — 单 commit
7. U2-Z 收尾 — 单 commit

每个阶段独立 commit，commit message 用中文，一句话讲「为什么」。

---

## 9. 测试要求

- **U2-B**：ImageQueryServiceTests 加 ImportedWithinDays 过滤断言（今天/7天/全部三档）
- **U2-D**：ImageQueryServiceTests 加 SourceBadge 计算断言（有 local embedding / cloud embedding / 仅 AI tag / 无 AI 数据 四态）；DbMigratorTests 加 v3→v4 迁移断言
- **U2-E**：GalleryItemViewModelTests 加 TagsSummary 计算（0/1/2/3+ 标签四态）
- 其它阶段（U2-A/C/F）以冒烟脚本为主，无新 Service 单测

---

## 10. 不在本计划范围

以下项不在 U2 范围，留待后续里程碑：
- §4.B.2 左栏属性筛选分组（P2）
- §4.C.1 卡片 hover 操作组（P2）
- §4.E.3 节点折叠态计数（P2）
- §6 待评估项（最近浏览历史 / 同目录兄弟文件 / hover 与右键菜单边界 / AI 档位徽标）
