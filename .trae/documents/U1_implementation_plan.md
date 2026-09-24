# U1 实现计划 —— UI 优化补丁里程碑

> 范围：[ui_optimization_target.md](file:///c:/AI_Programming/APP/QL_World_dotnet/.trae/documents/ui_optimization_target.md) P0 项落地
> 代号：U1（UI patch 1）；子阶段 U-A / U-B / U-C / U-Z
> 起始版本：v1.0.0（M4 发布基线）
> 收尾版本：v1.1.0（U1 完成后；UI 可见改进走 minor 升位）

---

## 0. 前置：v1.0.1 补丁提交（Pre-U1）

工作区有 v1.0.0 后未提交的修复，独立提交为 v1.0.1 补丁，不混入 U1 阶段。

### 改动文件
- [src/QL.World/Themes/Dark.xaml](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Themes/Dark.xaml) — TextMuted #6E6E7A→#9696A2；新增 Overlay #CC17171C + OverlayBrush
- [src/QL.World/Themes/Light.xaml](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Themes/Light.xaml) — TextMuted #9A9AA6→#6E6E78；Warning #B07700→#8F6400；新增 Overlay #CCFFFFFF + OverlayBrush
- [src/QL.World/Views/ShellWindow.xaml](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Views/ShellWindow.xaml) — 缺失态遮罩硬编码 #CC17171C → DynamicResource OverlayBrush
- [src/QL.World/Views/Support/VirtualizingTilePanel.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Views/Support/VirtualizingTilePanel.cs) — MeasureOverride/ArrangeOverride/CleanupContainers 防 NRE（ItemsPanel 切换时 generator 为 null 提前 return）

### 收尾动作
1. [src/QL.World/QL.World.csproj](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/QL.World.csproj) `<Version>` 1.0.0 → 1.0.1
2. [CHANGELOG.md](file:///c:/AI_Programming/APP/QL_World_dotnet/CHANGELOG.md) 顶部新增 `## [1.0.1] - 2026-09-24` 节，条目：
   - 修复：VirtualizingTilePanel 在 Grid↔List 视图切换时 NRE 崩溃（generator 切换期可为 null）
   - 改进：主题对比度达标 WCAG AA — TextMuted Dark/Light 调整；Light Warning #8F6400
   - 改进：缩略图遮罩统一走主题 OverlayBrush，禁止硬编码 hex
3. [GEMINI.md](file:///c:/AI_Programming/APP/QL_World_dotnet/GEMINI.md) 里程碑状态补「v1.0.1 补丁」
4. 门禁：`powershell -File build.ps1 -Mode Prepare`（0 警告 0 错误 + 测试全绿 + R2R 成功）
5. staging 体积记录并对比 v1.0.0
6. commit message：「v1.0.1 补丁：主题对比度 + OverlayBrush 基线 + VirtualizingTilePanel NRE 修复」

### 验收
- 主题切换 Dark↔Light 后，缺失文件遮罩与文本对比度目视正常
- 网格↔列表快速切换 10 次不崩

---

## 1. U1-A：选中态边框色（§4.A.1）

### 目标
画廊网格选中图卡用 2px 主题 Accent 外描边，不改卡片底色；列表态用左竖条高亮（避免整行底色降低文本对比度）。

### 改动文件
- [src/QL.World/Views/ShellWindow.xaml](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Views/ShellWindow.xaml) — `GridCardTemplate` 外层 Border 加 Style trigger；`ListRowTemplate` 外层 Border 加左侧 3px Accent 竖条
- [src/QL.World/Themes/Generic.xaml](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Themes/Generic.xaml) — 若需 ListBoxItem 默认 Style 覆盖（去除默认选中底色，让位给边框）
- [tools/smoke/UiSmokeA1.ps1](file:///c:/AI_Programming/APP/QL_World_dotnet/tools/smoke/UiSmokeA1.ps1)（若存在）或对应冒烟脚本 — 选中图卡后断言外描边可见
- [docs/UI_NUMBERING.md](file:///c:/AI_Programming/APP/QL_World_dotnet/docs/UI_NUMBERING.md) — 新增 B2-3-SEL（选中态视觉锚点）

### 实现要点
- **不走 Converter**：直接在 `GridCardTemplate` 外层 Border 上加 `Style.Triggers`，`Binding RelativeSource={RelativeSource AncestorType=ListBoxItem} Path=IsSelected` → 设置 `BorderBrush={DynamicResource AccentBrush}` + `BorderThickness=2`
- **ListBoxItem 默认 Style 覆盖**：WPF 默认 IsSelected 给整行铺色，需在 Generic.xaml 覆盖 `ListBoxItem` Style 的 ControlTemplate 去掉默认 Selected Background 动画，否则边框与铺色叠加
- **列表态左竖条**：在 `ListRowTemplate` 外层 Border 的左侧叠加一个 `Width=3` 的 `Rectangle`/`Border`，Visibility 绑定 IsSelected；这样不破坏文本对比度
- **多选态**：Extended 多选时多个卡片同时显示边框，符合预期

### 验收
- 单击图卡 → 立即出现 2px 紫色外描边
- Ctrl+多选 → 多个图卡同时显示边框
- 切换 Dark/Light 主题 → 边框色随主题变化
- 列表态选中 → 左竖条显示，文本对比度不下降
- 网格↔列表切换 → 不崩（验证 v1.0.1 修复仍有效）

---

## 2. U1-B：§4.D.2 验证节（已落地确认）

### 目标
经核实 §4.D.2「顶栏计数显式化」已在 M1–M4 各阶段逐步落地，本节仅做验证与文档同步，不写新代码。

### 现状核实结果
| 节点 | XAML 绑定 | ViewModel 填充路径 | SQLite 查询 |
|---|---|---|---|
| TagTree.CategoryNodes.Count | `Text="{Binding Count}"` | `TagTreeViewModel.RefreshCountsAsync` → `_query.GetCategoryCounts()` | `SELECT primary_category, COUNT(*) FROM images GROUP BY primary_category` |
| CollectionTree.CollectionNodes.Count | `Text="{Binding Count}"` | `CollectionTreeViewModel.RefreshAsync` → `_collections.GetAll()` → `row.ImageCount` | 子查询 `SELECT COUNT(*) ... collection_images` |
| TagTree.TopTagNodes.Count | `Text="{Binding Count, StringFormat=' {0}'}"` | `RefreshTopTagsAsync` → `_tags.GetTopTags(50)` | `SELECT tag, COUNT(*) AS n FROM image_tags ... LIMIT 50` |
| PbrGroupTree.GroupNodes.MemberCount | `Text="{Binding MemberCount}"` | `PbrGroupTreeViewModel.RefreshAsync` → `_grouping.GetGroups()` → `row.MemberCount` | 贴图组服务查询 |

### 动作
1. 在 [ui_optimization_target.md](file:///c:/AI_Programming/APP/QL_World_dotnet/.trae/documents/ui_optimization_target.md) §4.D.2 节末尾追加「**状态**：经 U1-B 核实已落地（M1–M4 各阶段逐步实现），本节列为基线条款」
2. [docs/UI_NUMBERING.md](file:///c:/AI_Programming/APP/QL_World_dotnet/docs/UI_NUMBERING.md) B1-1/B1-2/B1-3/B1-4 行的「说明」补「（含子项计数显示）」
3. 冒烟脚本增加断言：B1 各分类节点显示非空 Count 文本

### 验收
- 启动应用 → B1 各节点 Name 后紧跟数字
- 导入新图 → TagTree Count 与 B2-1 计数条同步刷新

---

## 3. U1-C：底部快捷键提示（§4.E.1）

### 目标
参考千岚·商城底部快捷键提示，C1 状态栏右侧增加上下文相关快捷键提示。

### 改动文件
- [src/QL.World/Views/ShellWindow.xaml](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Views/ShellWindow.xaml) — C1 区重构成 `C1-L 状态文本 / C1-C 进度计数 / C1-R 快捷键提示` 三段
- [src/QL.World/ViewModels/StatusBarViewModel.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/ViewModels/StatusBarViewModel.cs) — 新增 `ShortcutHint` 属性，按当前焦点上下文切换
- [src/QL.World/ViewModels/ShellViewModel.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/ViewModels/ShellViewModel.cs) — 选中态变化时更新 ShortcutHint
- [docs/UI_NUMBERING.md](file:///c:/AI_Programming/APP/QL_World_dotnet/docs/UI_NUMBERING.md) — C1 拆为 C1-L/C1-C/C1-R
- [tools/smoke/UiSmoke*.ps1](file:///c:/AI_Programming/APP/QL_World_dotnet/tools/smoke) — 断言 C1-R 可见且文本随选中态变化

### 快捷键映射
| 上下文 | 提示文本 |
|---|---|
| 空库 | `Ctrl+I 导入` |
| 无选中 | `Ctrl+I 导入 · Ctrl+F 搜索 · F5 刷新` |
| 单选 | `Ctrl+1…5 评分 · F2 重命名 · Del 移除 · Ctrl+F 搜索` |
| 多选 | `Ctrl+1…5 批量评分 · Del 批量移除` |
| 搜索焦点 | `Esc 清除 · Enter 应用` |

### 实现要点
- `ShortcutHint` 是只读计算属性，依赖 `Gallery.SelectedItems.Count` 与 `IsSearchFocused`
- 上下文判定优先级：多选 > 单选 > 搜索焦点 > 无选中 > 空库
- 不引入新快捷键，仅显示已有的（来自 [ShellWindow.xaml InputBindings L121-131](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Views/ShellWindow.xaml#L121-131)）
- F2 重命名快捷键若未实现，提示中先不加；只显示 InputBindings 已绑定的

### 验收
- 启动空库 → C1-R 显示 `Ctrl+I 导入`
- 选中单张 → C1-R 切换为评分/搜索提示
- 多选 → C1-R 切换为批量提示
- 主题切换 → C1-R 文本色随主题

---

## 4. U1-Z：收尾

按序执行：

1. [src/QL.World/QL.World.csproj](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/QL.World.csproj) `<Version>` 1.0.1 → 1.1.0
2. [CHANGELOG.md](file:///c:/AI_Programming/APP/QL_World_dotnet/CHANGELOG.md) 顶部新增 `## [1.1.0] - 2026-09-2X` 节
3. [GEMINI.md](file:///c:/AI_Programming/APP/QL_World_dotnet/GEMINI.md) 里程碑状态补「U1 UI 优化补丁」
4. [docs/UI_NUMBERING.md](file:///c:/AI_Programming/APP/QL_World_dotnet/docs/UI_NUMBERING.md) 补齐 U1 新增编号（B2-3-SEL / C1-L / C1-C / C1-R）
5. [ui_optimization_target.md](file:///c:/AI_Programming/APP/QL_World_dotnet/.trae/documents/ui_optimization_target.md) 标注 P0 项落地状态
6. 门禁：`powershell -File build.ps1 -Mode Prepare`（0 警告 0 错误 + 测试全绿 + R2R 成功）
7. staging 体积记录并对比 v1.0.1
8. 向用户交付总结
9. 记忆沉淀：实测发现的可复用坑写入 project_memory.md

---

## 5. 阶段提交顺序

1. v1.0.1 补丁（Pre-U1）— 单 commit
2. U1-A 选中态边框色 — 单 commit
3. U1-B 验证节 + 文档同步 — 单 commit
4. U1-C 底部快捷键提示 — 单 commit
5. U1-Z 收尾 — 单 commit

每个阶段独立 commit，commit message 用中文，一句话讲「为什么」。

---

## 6. 不在本计划范围

以下 P0/P1/P2 项不在 U1 范围，留待后续里程碑：
- §4.A.2 品牌渐变位（P1）
- §4.B.1 顶栏「最近入库」下拉（P1）
- §4.B.2 左栏属性筛选分组（P2）
- §4.B.3 详情栏「相关资产」section（P1）
- §4.C.1 卡片 hover 操作组（P2）
- §4.C.2 类别/状态徽标（P1）
- §4.D.1 卡片元数据条（P1）
- §4.E.2 品牌化版本号（P1）
- §4.E.3 节点折叠态计数（P2）
