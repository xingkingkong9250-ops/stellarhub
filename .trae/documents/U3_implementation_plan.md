# U3 实施计划 —— UI 优化 P2 清零（目标 v1.3.0）

> 来源：[ui_optimization_target.md](file:///c:/AI_Programming/APP/QL_World_dotnet/.trae/documents/ui_optimization_target.md) §4.B.2 / §4.C.1 / §4.E.3（仅有的 3 个 P2 项）。
> 完成后该文档 P0/P1/P2 全部落地。基线：v1.2.0（U2-Z，8a914d1），测试 264，staging 190.4MB。

## 子阶段划分

| 阶段 | 内容 | 来源 |
|---|---|---|
| U3-A | B1-5 左栏「属性筛选」折叠组（颜色/分辨率/大小/评分） | §4.B.2 |
| U3-B | B2-3-ACT 网格卡片 hover 操作组（打开/定位/复制路径） | §4.C.1 |
| U3-C | B1-COL 分区折叠 + 折叠态子项计数 | §4.E.3（适配现状，见下） |
| U3-Z | 收尾：v1.3.0 + 文档同步 + 门禁 | — |

## 关键现状核查（影响设计）

1. **B1 不是 TreeView**：素材库/收藏夹/Top50/贴图组均为 ItemsControl 平铺，无层级无折叠，且节点计数已常驻显示（U1-B 核实）。§4.E.3 字面前提（TreeView 折叠节点）不成立 → 适配为「B1 四个分区标题可折叠，折叠态标题行显示该分区子项总数」（待用户确认）。
2. **颜色筛选无法纯 SQL 实现**：dominant_colors 存 JSON hex 数组（如 `["#A1B2C3",…]`），SQL 无法计算色相 → Schema v5 新增 `images.color_family TEXT NULL` 列，分析/backfill 时一并写入；存量数据由迁移器从既有 JSON 纯计算回填（不重跑 K-means）。
3. **卡片操作与选中态解耦**：现有 Open/Reveal/CopyPath 命令作用于 PrimarySelected，hover 卡片需要参数化命令（CommandParameter=卡片项）。
4. **冒烟脚本现状**：tools/smoke 仅 UiSmokeA3.ps1（W3/A3 专用）；主窗口改动与 U1/U2 一致走人工验收，不新增脚本。

## U3-A：B1-5 属性筛选（§4.B.2）

**Schema v5**（[DbMigrator.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Data/DbMigrator.cs) CURRENT_VERSION 4→5）：
- `ALTER TABLE images ADD COLUMN color_family TEXT NULL`（9 族：red/orange/yellow/green/cyan/blue/purple/magenta/gray）
- 迁移时回填：`SELECT id, dominant_colors WHERE color_family IS NULL AND dominant_colors 非空` → C# 解析首个 hex → HSV 算族 → UPDATE（S<0.18 或 V<0.15 → gray；否则按 H 八等分，red 跨 0°）
- DatabaseServiceTests 硬编码版本断言 4→5 同步（M2/U2-D 教训）

**筛选模型**：
- 新增 `Services/Colors/ColorFamily.cs`：`HexToFamily(string) → string?` 纯函数
- [LibraryFilter.cs](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Models/LibraryFilter.cs) 加 `ColorFamily` / `ResolutionBucket` / `FileSizeBucket` / `RatingBucket`（均 null=全部）+ Clone 同步
- [ImageQueryService.AppendClauses](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Services/ImageQueryService.cs#L451) 加 4 个 AND 分支（全参数化）：
  - 颜色：`i.color_family = $cf`
  - 分辨率（长边 MAX(width,height)）：<1024 / 1024–2047 / 2048–4095 / ≥4096；NULL 尺寸不匹配任何档
  - 大小：<1MB / 1–5MB / 5–20MB / ≥20MB
  - 评分：未评分(=0) / ≥3 / ≥4 / =5
- [ColorAnalysisService](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Services/Colors/ColorAnalysisService.cs) AnalyzeAsync 写 dominant_colors 时同窗 UPDATE color_family

**ViewModel/XAML**：
- 新增 `AttributeFilterViewModel`：四组单选 chip 选项（各含「全部」），选中写 Gallery.Filter + RefreshAsync；暴露 ActiveSummary 供 FilterSummary 拼接；ResetSelection 供 ClearFilters
- [ShellWindow.xaml](file:///c:/AI_Programming/APP/QL_World_dotnet/src/QL.World/Views/ShellWindow.xaml) B1 贴图组之后追加「属性筛选」区：Expander 式可折叠（默认收起，折叠态持久化 AppSettings.SidebarAttributeFilterExpanded），内四行维度（行标题 + WrapPanel chips，选中 chip 用 ChipActiveButtonStyle）
- GalleryViewModel.BuildSummary 追加属性筛选摘要；ShellViewModel.ClearFilters 重置四维
- 编号：B1-5（B1-5-1 颜色 / B1-5-2 分辨率 / B1-5-3 大小 / B1-5-4 评分）

**测试**：ColorFamilyTests（hex→族边界：灰阈值、red 环绕、非法输入）；ImageQueryServiceTests 扩展（四维度各档 + AND 组合 + NULL 尺寸不匹配）；DbMigrator v5 迁移+回填测试；ColorAnalysisServiceTests 断言 family 同写。

## U3-B：B2-3-ACT 卡片 hover 操作组（§4.C.1）

- ShellViewModel 新增参数化命令 `OpenItemCommand` / `RevealItemCommand` / `CopyItemPathCommand`（参数 GalleryItemViewModel；`CanExecute = item is { IsMissing: false }`），复用 SystemNavigator/Clipboard
- GridCardTemplate 缩略图 Grid 内加底部横排三按钮浮层（OverlayBrush 底），外层 Border `IsMouseOver` Trigger 切 Visibility（默认 Collapsed，不参与 Measure——虚拟化安全）；IsMissing 卡被缺失遮罩盖住天然不可触
- 仅网格态（文档明确）；列表态不加
- 编号：B2-3-ACT；与右键菜单功能重叠属预期（待评估项 §6 后续再梳理边界）
- 测试：ShellViewModelTests 加参数化命令 CanExecute/执行路由单测（Navigator 静态不可 mock 的跟随现状仅测 CanExecute 与空参数容错）

## U3-C：B1-COL 分区折叠 + 折叠态计数（§4.E.3 适配）

- B1 四分区标题（素材库/收藏夹/热门标签 Top50/贴图组）改为 ToggleButton（▾ 展开 / ▸ 折叠 + 名称），内容区 Visibility 绑定
- 折叠时标题追加 `(N)`：素材库=分类节点数、收藏夹=收藏夹数、Top50=标签数、贴图组=组数（子项总数语义）；展开不显示（子项已自带计数）
- 折叠态持久化：AppSettings 四个 bool（默认全展开）
- ShellViewModel 加 Sidebar*Collapsed 属性 + 各分区 Count 计算属性（订阅 Gallery.Refreshed 刷新，树 RefreshAsync 后同步）
- 编号：B1-COL
- 测试：计数计算属性与持久化读写单测

## U3-Z：收尾

1. csproj `<Version>` 1.2.0→1.3.0；AppInfo.MilestoneLabel = "U3 发布基线"
2. CHANGELOG [1.3.0] 按子阶段组织
3. GEMINI.md 里程碑状态；UI_NUMBERING 新增 B1-5 系列/B2-3-ACT/B1-COL；ui_optimization_target.md 三项标注「已落地（U3-x）」+ 修订记录
4. `powershell -File build.ps1 -Mode Prepare`：0 警告 0 错误 + 测试全绿 + R2R 成功
5. staging 体积对比（预期 ≈190.4MB 不变，无新依赖）
6. 交付总结 + project_memory 沉淀（仅「编译不可见、实测发现」）

## 验收标准

- U3-A：选颜色族/分辨率/大小/评分 chip 画廊即时过滤且与 B1/A1 其它筛选 AND 叠加；FilterSummary 与「清除全部筛选」同步；未分析颜色图片选颜色族时结果为 0 不报错
- U3-B：hover 网格卡片浮出三按钮且点击作用于该卡片（非当前选中）；移出即隐藏；缺失卡不出现；列表态无变化
- U3-C：四分区可各自折叠/展开，折叠标题显示子项数；重启后折叠态保持
- 全部阶段独立中文 commit（git commit -F 临时文件），UI_NUMBERING 同步
