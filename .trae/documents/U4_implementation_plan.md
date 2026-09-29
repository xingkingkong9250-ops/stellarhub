# U4 实施计划 — B3-4 主色调色块展示（PRD 核心功能收尾）

## 背景与范围

- PRD 唯一未实现核心功能：W1/B3-4 详情栏「主色调」（UI_NUMBERING 唯一仍标「规划」的行）。
- 数据层已完备：`images.dominant_colors` = JSON 数组（3–5 个 `"#RRGGBB"`，ColorExtractor K-means 确定性产出），A1-8「分析颜色」可批量回填，U3-A 色族筛选已消费 `color_family`。
- 用户已决策（2026-09-29 AskUserQuestion）：色块**仅展示 + tooltip 显示 hex**，不做点击联动/复制。
- 顺带修正：UI_NUMBERING 父行 W1/B3、W1/C1 残留的「已实现（M1 部分）」过时措辞（子行均已实现）。

## U4-A：B3-4 主色调色块行

1. **DetailViewModel**：新增 `DominantColors`（`IReadOnlyList<string>`，解析 DominantColorsJson；null/空/JsonException → 空列表）+ `HasDominantColors`；选中图片变化时刷新。多选批量编辑态不展示（B3 多选态本无 B3-1~B3-7 单选字段）。
   - 刷新时机：颜色批量回填完成后重新选中即可见（与评分/标签同模式，依赖选中刷新）；若实现中发现 ColorAnalysisService 已有完成事件则顺带订阅，无则不加（避免过度设计）。
2. **ShellWindow.xaml B3 区**：在 B3-3 评分与 B3-5 标签之间插入 B3-4 行——「主色调」小标题（沿用详情栏小标题样式）+ ItemsControl 横向排列 ≤5 个色块 Border（20×20，CornerRadius 3，Background 绑 hex 经新 converter，1px 主题边线描边保证浅色块可辨，ToolTip 绑同一 hex 字符串）；`HasDominantColors=false` 整行隐藏（BoolVis）。
   - 主题约束：色块颜色是数据内容色（非主题硬编码），允许；描边用主题 BorderBrush，禁硬编码 hex。
3. **Helpers**：新增 `HexBrushConverter`（`#RRGGBB` → SolidColorBrush；非法输入 → Transparent）。先查 Helpers 目录有无现成 converter 可复用，有则不新建。
4. **UI_NUMBERING**：B3-4 行 →「已实现（U4-A）」（补注：≤5 色块 + tooltip hex，数据 dominant_colors，整行可隐藏）；父行 B3/C1 措辞修正。
5. **测试**（xUnit）：
   - DetailViewModel：合法 JSON 解析出色列表 / null 与损坏 JSON 得空列表且 HasDominantColors=false / 切换选中刷新。
   - HexBrushConverter：合法 hex 得对应 Color、非法得 Transparent。

## U4-Z：收尾（v1.4.0）

1. csproj `<Version>` 1.3.0→1.4.0；AppInfo.MilestoneLabel="U4 发布基线"。
2. CHANGELOG [1.4.0]（按 U4-A 组织）；GEMINI 里程碑状态更新（注明 PRD 核心功能 100% 覆盖）。
3. UI_NUMBERING 全表核对。
4. 门禁：`powershell -File build.ps1 -Mode Prepare`（0 警告 + 测试全绿 + R2R 成功）。
5. staging 体积对比（预期 ≈190.46MB 不变，无新依赖）。
6. 交付总结（commit 列表 + 门禁结果 + 人工验收项）；记忆沉淀（仅「编译不可见、实测发现」）。

## 验收标准

- 已分析颜色的图片被选中时，详情栏显示 3–5 个色块，tooltip 为对应 `#RRGGBB`。
- 未分析/数据损坏的图片选中时，主色调行整体隐藏，无异常。
- 门禁通过；测试净增 ≥3。
