# M4 实现计划 — 专业化与发布（v1.0.0）

## Context

M3 双阶段已全部完成（v0.5.0）：AI 三档（Disabled/Local ONNX/Cloud）、颜色分析、语义搜索 Local/Cloud 双模，204 测试全绿。M4 是 PRD 定义的最后一个里程碑（§10「M4 专业化与发布」）：PBR 贴图成组、专业格式接入、10 万级压测、USER_GUIDE、Finalize 五件套发布。

**已确认决策**（用户答复）：
1. **PBR 呈现**：B1 树新增「贴图组」区块（与收藏夹并列，点组名筛选画廊）；成组 = 导入时按可编辑规则自动成组 + 手动重扫，两者都做
2. **格式范围**：全接 TGA/EXR/HDR/PSD（Magick.NET 定点引入，封装在 `ImageInspector` 后；PSD 按合成帧 flatten 显示）
3. **发布**：真实发布 —— Finalize 构建五件套 + 打 annotated tag `v1.0.0` + dist/ 落地

**侦察结论**（实现依据）：
- 迁移惯例：`Data/DbMigrator.cs` — `CURRENT_VERSION=1`，按 `PRAGMA user_version` 增量执行，SQL 从嵌入资源 `Data/Schema.v{version}.sql` 加载（csproj `<EmbeddedResource>`）；升 v2 = 新增 `Schema.v2.sql` + `CURRENT_VERSION=2`
- 导入钩子：`Services/ImportService.cs` — `TryReadMetadata` 探测（L103-119）+ `InsertBatch` 落库（L148-184）；格式探测入口 `Services/ImageInspector.cs` 注释明确 M4 扩展点
- 缩略图入口：`Services/ThumbnailService.cs` `ProcessJob()`（L171-204）直接 `Image.Load(source)` → WebP
- B1 树模式：`ViewModels/CollectionTreeViewModel.cs` — 节点点击切换 `Gallery.Filter.CollectionId`；B1 现有 B1-1 素材库树 / B1-2 收藏夹树 / B1-3 高频标签；W3 现有 A1–A5 页签
- SQL 筛选：`Services/ImageQueryService.cs` `AppendClauses()`（L417-477）— collection 筛选用 `EXISTS (SELECT 1 FROM collection_images ...)`，贴图组筛选复用同模式
- 发布：`build.ps1` — `Finalize` 模式为占位 throw（L51-52）；Prepare 产出 `artifacts\staging\`；PRD §7.1：五件套命名 `QL World_v{x.y.z}.zip / _source.zip / _更新公告.txt / _使用说明.txt / _manifest.json`，annotated tag 含 `release-manifest-sha256:`，manifest 最后原子落地，dist/ 永不覆盖
- 契约测试：tests 工程无 release 目录，本里程碑新建

**顺带回收 M3 遗留**：W1/B2-3-CM1 右键菜单 + W1/B3-8「查找相似」冒烟步骤 → 挂 M4-D；605 MB 模型包下载端到端 → 列入 M4-Z 人工验收项。

**并行性**：M4-E（USER_GUIDE）与 M4-F（ReleaseTool）无交叉，可 subagent 并行；其余串行。

---

## M4-A：schema v2 + PbrGroupingService（纯服务层 + 单测）

**新建**：
- `Data/Schema.v2.sql`（EmbeddedResource）：
  ```sql
  CREATE TABLE IF NOT EXISTS pbr_groups (
      id INTEGER PRIMARY KEY AUTOINCREMENT,
      name TEXT NOT NULL,            -- 基础名（展示用，允许重名：不同目录同名组）
      created_at TEXT NOT NULL,
      updated_at TEXT NOT NULL
  );
  CREATE TABLE IF NOT EXISTS pbr_group_images (
      group_id INTEGER NOT NULL REFERENCES pbr_groups(id) ON DELETE CASCADE,
      image_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE,
      role TEXT NOT NULL,            -- basecolor|normal|roughness|metallic|ao|height|other
      PRIMARY KEY (group_id, image_id)
  );
  CREATE INDEX IF NOT EXISTS idx_pbr_group_images_image ON pbr_group_images(image_id);
  ```
- `Services/Pbr/PbrRuleSet.cs` — 规则模型（`PbrRule(string Pattern, string Role, bool Enabled)` 有序列化）+ 默认规则集（first-match、大小写不敏感、剥尾部匹配段得基础名）：
  - `BaseColor|Albedo|Diffuse|_D` → basecolor；`Normal|_N` → normal；`Rough` → roughness；`Metal` → metallic；`AO|Occlusion` → ao；`Height|Bump|Disp` → height
- `Services/Pbr/PbrGroupingService.cs`：
  - `RescanAllAsync()` — 全库重扫：按「目录 + 基础名」分组，≥2 张才成组；全量重建 pbr_groups/pbr_group_images（事务）；清理空组
  - `ScanNewImportsAsync()` — 导入钩子增量路径（只扫新 image id 所在目录）
  - 规则从 AppSettings 配置读取（config.json 扩展 `PbrRules` 节，AtomicTextFile 原子写 + 损坏回退默认规则）

**修改**：
- `Data/DbMigrator.cs` — `CURRENT_VERSION = 2`
- `QL.World.csproj` — `Data/Schema.v2.sql` EmbeddedResource
- `Services/ImportService.cs` — 批量落库成功后调 `ScanNewImportsAsync`（异常不阻断导入）
- `Models` AppSettings 配置对象 — 增加 PbrRules 属性

**测试**：规则匹配（命中/大小写/first-match 优先级/无后缀裸图）、单图不成组、跨目录同名不成一组、RescanAll 重建幂等、空组清理、规则损坏回退默认、迁移 v1→v2 幂等（更新 `DatabaseServiceTests`）。

**验收**：单测全绿、0 警告；独立 commit。

---

## M4-B：B1「贴图组」区块 + W3/A6 规则编辑

**新建**：
- `ViewModels/PbrGroupTreeViewModel.cs` — 复刻 CollectionTreeViewModel 模式：分组节点列表、点击切换筛选、再点清除
- `ViewModels/PbrRulesViewModel.cs` — A6 页签：规则列表增删改（Pattern/Role/Enabled）、恢复默认、`RescanCommand`（立即重扫 + 完成通知）

**修改**：
- `Models/LibraryFilter.cs` — 新增 `PbrGroupId`；`ImageQueryService.AppendClauses()` — 新增 `EXISTS (SELECT 1 FROM pbr_group_images ...)` 分支（collection 同模式，注意语义分支优先级约定）
- W1 XAML（B1 区）— B1-4「贴图组」区块（ItemsControl 列组名 + 成员数）
- W3 XAML — 新页签 A6「贴图组规则」
- `ShellViewModel`/`ServiceRegistry` — 接线

**编号与冒烟**：
- `docs/UI_NUMBERING.md` — 新增 B1-4（B1-4-1 起）与 W3/A6 及其叶子编号
- `tools/smoke/` — 对应冒烟步骤（B1-4 节点筛选、A6 规则编辑持久化）

**验收**：点组名画廊只显示组成员；规则编辑保存后重扫生效；config.json 含 .backup；编号表同步；冒烟通过；独立 commit。

---

## M4-C：Magick.NET 专业格式（TGA/EXR/HDR/PSD）

**新增依赖**：`Magick.NET-Q8-AnyCPU`（Q8 非 HDRI——自动 8bit，无 tone-map 负担；AnyCPU 与现有 win-x64 发布兼容）

**新建**：
- `Services/MagickDecoder.cs` — internal，封装 Magick 读取：`TryReadMetadata`（宽高/格式名）+ `TryLoadFrame`（返回 8bit RGBA 像素；PSD 用 `MagickImageCollection` flatten 取合成帧）。可注入接口供测试

**修改**：
- `Services/ImageInspector.cs` — ImageSharp `Identify` 抛 `UnknownImageFormatException` 时 fallback 到 `MagickDecoder`（其余异常仍按失败处理）；导入白名单扩展 tga/exr/hdr/psd（定位 `ImportService` 递归枚举的扩展名过滤表）
- `Services/ThumbnailService.cs` `ProcessJob()` — `Image.Load` 失败时走 `MagickDecoder.TryLoadFrame` → `Image.LoadPixelData` → 复用现有 WebP 缩略图管线（保持命名/目录约定不变）

**测试**：测试代码内用 Magick 生成 TGA/EXR/HDR/PSD 样例 → TryReadMetadata 成功 + 宽高正确 + 缩略图生成成功；损坏文件两种解码器都失败 → 导入跳过计数正确；ImageSharp 原有格式回归不受影响。

**验收**：四格式导入→缩略图→画廊浏览全链路；PSD 显示合成帧；staging 体积增量记录（Magick.NET native 约 +10–20 MB 量级）；独立 commit。

---

## M4-D：10 万级压测与优化

**新建**：
- `tools/StressGen/`（console 工程，不入主 sln 的发布链）— 参数化生成合成库：`--count 100000 --out <dir>`；直插 SQLite（复用 schema）+ 程序化生成真实小尺寸缩略图文件 + 少量真实样例图（验证画廊解码路径）

**压测项与记录**（结果记入本计划文档附注 + GEMINI.md）：
- 冷启动时间；万级/10 万级画廊首次绘制与滚动帧率（网格/列表、缩放各级别）
- 文本/标签/语义搜索延迟；B1 树（含贴图组区块）刷新耗时；批量导入吞吐

**优化落点**（按实测结果执行，预期候选）：分页上限核对、`image_tags`/`pbr_group_images` 索引补齐（EXPLAIN QUERY PLAN 验证）、缩略图调度批量化、虚拟化容器回收确认。

**冒烟回收**（M3 遗留）：`tools/smoke/` 补 W1/B2-3-CM1 画廊右键菜单 + W1/B3-8「查找相似」步骤。

**验收**：10 万库指标产出；每项优化有前后对比数据；主链路测试仍全绿；独立 commit。

---

## M4-E：docs/USER_GUIDE.md

面向最终用户的中文手册：安装与首启（数据目录/单实例）、导入（递归/去重/损坏跳过/FileWatcher）、浏览与筛选（画廊/排序/B1 三区块/搜索三档）、收藏夹与贴图组、评分标签备注、AI 三档配置（模型下载向导/Key DPAPI/连接测试）、语义搜索（含图→图相似）、批量分析与颜色分析、设置项详解、数据安全（WAL/备份/缺失文件重新定位）、常见问题（模型校验失败/云端 401/缩略图重建）。

**验收**：覆盖全部 W1–W3 用户可见功能；与 UI_NUMBERING 编号交叉引用；独立 commit。

---

## M4-F：Finalize 五件套 + ReleaseTool + 契约测试

**新建**：
- `tools/ReleaseTool/`（console 工程，核心逻辑库化供测试直接引用）：
  - 输入：staging 目录（Prepare 产物）、版本号（csproj 读取）、git 信息
  - 产物（写入指定 dist root，默认 `dist/`，`-DryRun` 支持临时目录）：
    1. `QL World_v{x.y.z}.zip` — staging 打包
    2. `QL World_v{x.y.z}_source.zip` — `git archive`
    3. `QL World_v{x.y.z}_更新公告.txt` — 模板 `docs/release/RELEASE_NOTES.md`（本里程碑建立）
    4. `QL World_v{x.y.z}_使用说明.txt` — 由 USER_GUIDE 精简生成
    5. `QL World_v{x.y.z}_manifest.json` — 版本/commit/构建时间/五件 sha256；**最后原子落地**
  - dist 永不覆盖：目标已存在 → 报错退出
- `build.ps1` — `Finalize` 模式实现：校验工作区干净 → 跑 Prepare 门禁 → 调 ReleaseTool → 输出 annotated tag 命令（`git tag -a v{x.y.z} -m "release-manifest-sha256:<sha>"`，ps1 只读 Git 不做写操作 → tag 由脚本打印命令、人工/确认后执行，符合 PRD「build.ps1 只读 Git」约定）

**测试**：`tests/QL.World.Tests/Release/ReleaseContractTests.cs` — 直接引用 ReleaseTool 库：五件套齐全、命名规范、manifest schema 字段完整、sha256 与实际文件一致、重复发布报错、-DryRun 无残留。

**验收**：契约测试全绿；Finalize dry-run 在临时目录产出符合规范的五件套；独立 commit。

---

## M4-Z：收尾与真实发布

1. `QL.World.csproj` `<Version>` → `1.0.0`
2. `CHANGELOG.md` — `[1.0.0]` 节（按 M4-A…F 组织）
3. `GEMINI.md` — 里程碑状态「M0–M4 全部完成，v1.0.0 已发布」
4. `docs/UI_NUMBERING.md` — 补齐 B1-4/A6 编号；全表冻结走查（M2 惯例逐项核对）
5. 门禁：`powershell -File build.ps1 -Mode Prepare` 0 警告 + 全绿 + R2R publish
6. **真实发布**：`build.ps1 -Mode Finalize` → dist/ 五件套 → 确认后执行 annotated tag `v1.0.0`（消息含 `release-manifest-sha256:`）
7. staging 体积对比记录（0.5.0 → 1.0.0）
8. 交付总结：commit 列表、压测指标、发布产物清单、人工验收项（605 MB 模型包下载端到端语义搜索、真实 dist 包在 `QL_WORLD_DATA_ROOT` 空目录首启验收）
9. project_memory 沉淀 M4 实测经验

---

## 提交节奏

M4-A → M4-B → M4-C → M4-D →（M4-E ∥ M4-F，可 subagent 并行）→ M4-Z；每子阶段独立中文 commit（`git commit -F` 临时文件模式）。
