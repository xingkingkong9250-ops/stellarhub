# 千星汇图（StellarHub）开发进度总览

> 生成日期：2026-10-27 · 基准：master @ 1e47c9b（0.18.0 已提交）· 当前版本 **0.19.0**
> 真源：`CHANGELOG.md`、`docs/UI_NUMBERING.md`、`GEMINI.md`、
> `docs/superpowers/specs/2026-10-27-duplicate-detection-design.md`
> 本文是阶段性汇总快照，版本变更仍以 CHANGELOG 与 UI 编号表为准。

## 1. 结论摘要

| 维度 | 状态 |
|---|---|
| 产品里程碑 | M0–M12（M10 无独立记录）、U1–U6 UI 补全、0.13~0.19 迭代均已完成 |
| 查重里程碑 0.16.0 | DUP-A ~ DUP-E 全部提交，UI 冒烟 18/18 通过，用户手册已补 |
| 素材组织入口 0.17.0 | W9 素材组织窗口 + 顶栏 A1-12 入口已提交（PEND-002 完成） |
| 共享收藏夹 0.18.0 | collections.json 协议 + 服务层 + 共享作用域工具栏（PEND-004 完成） |
| 库目录在线迁移 0.19.0 | 整库复制 + 路径重写 + quick_check 校验 + 失败回滚 + 进度显示（PEND-003 完成） |
| 词条 chip 搜索框 0.20.0 | W1/A1-4 chip 编辑器（多词条 AND + 筛选 chip 混排）+ search_log 落库（schema v13） |
| UI 编号实现率 | 129 个已实现锚点、3 暂隐藏（功能已完成，仅 B1 位置隐藏，入口已移入 W9）→ 活跃项实现率 ≈ 97.7% |
| 测试门禁 | `dotnet test -c Release` **847 通过 / 0 失败** |
| 编译门禁 | `dotnet build -c Release` **0 警告 0 错误** |
| 发布状态 | 版本号 0.20.0；未执行发布打包（发布需用户指令） |

结论：PRD 核心功能已 100% 覆盖（M0–M12 基线）；0.16.0 查重、0.17.0 素材组织入口、
0.18.0 共享收藏夹、0.19.0 库目录在线迁移均已完成。剩余工作集中在
**PEND-001 ImageSharp 4.x 评估**、**PEND-005 跨平台评估**，其后进入 1.0.0 决策。

## 2. 里程碑进度

| 阶段 | 主题 | 版本 / 提交 | 状态 |
|---|---|---|---|
| M0–M4 | PRD 基线：核心素材管理 → 组织与可靠性 → AI 双轨 → 专业化与发布 | 历史 1.0.0 后按新版本规则重编码 | 完成 |
| M5 | NAS 远程库（WebDAV、远程扫描、多级缩略图、下载队列、弱网策略、ProgressiveImage） | 0.0.x 系列 | 完成 |
| M6 | 多用户管理（账号/登录/共享库/提交审核/权限） | 0.1.0、0.1.1（U6） | 完成 |
| M7 | NAS SMB 协议扩展（Win32 P/Invoke，零新依赖） | 0.2.0 | 完成 |
| M8 | 多 NAS 聚合、主 NAS 迁移、数据搬运 | 0.3.0 | 完成 |
| M9 | NAS SFTP 协议扩展（SSH.NET） | 0.4.0 | 完成 |
| M11 | 图片原始文件统一托管（CAS + NTFS 硬链接分类） | 0.11.0 | 完成 |
| M12 | 标签驱动分类体系重构（6 分类 + 11 筛选组 168 标签） | 0.13.0、0.13.1 | 完成 |
| U1–U6 | UI 补全与品牌焕新（品牌 Logo、共享画廊、审核队列、卡片操作组等） | 0.1.1 ~ 0.7.x | 完成 |
| 0.13.3–0.13.7 | B1-1 气泡标签、属性筛选合并、B1 三分区隐藏、质感移入筛选组 | 0.13.3 ~ 0.13.7 | 完成 |
| 0.14.x | 布局瘦身（B1 图标/文字双模式、B3 可折叠、顶栏合并、菜单栏移除、点击修复） | 0.14.0 ~ 0.14.8 | 完成 |
| 0.15.x | 画廊按显示宽度选档高清化、UI 编号图鉴 | 0.15.0、0.15.1 | 完成 |
| **0.16.0** | **图片查重 DUP-A~E**（感知哈希、导入查重、全库扫描、W8 审核窗口、L3 VLM 终审） | 029bf76 / 4646f94 / 0cf6360 / a74fd94 / 6626dc5 + d2514a5 | **完成** |
| **0.17.0** | **PEND-002 素材组织入口**（W9 窗口：收藏夹 / 高频标签 / 贴图组；顶栏 A1-12） | a2b004a | **完成** |
| **0.18.0** | **PEND-004 共享收藏夹**（collections.json、SharedCollectionService、共享作用域工具栏） | 1e47c9b | **完成** |
| **0.19.0** | **PEND-003 托管库在线迁移**（整库复制、路径重写、quick_check、失败回滚、进度显示） | 本轮提交 | **完成** |

说明：仓库中不存在 M10 的任何记录（CHANGELOG/GEMINI/记忆均无），推断其内容并入
U 系列 UI 补全，后续不再单独追溯。

## 3. 功能完成清单（按业务域）

| 业务域 | 已实现能力 | UI 锚点 |
|---|---|---|
| 导入与托管 | 文件/文件夹导入、格式过滤、MD5 与感知哈希去重、导入后自动 AI 分析、CAS 托管与硬链接分类、缺文件重新定位 | W2、W2/AI、W7、W3/A1-LIB |
| 画廊浏览 | 网格/列表/瀑布流三视图、虚拟化、自适应列数、256/1024 双档缩略图、排序、缩放、最近入库、来源徽标、标签摘要、hover 操作组 | W1/B2、B2-1~4、B2-3-* |
| 筛选与搜索 | 素材库树（5 分类 + 未分类）、11 组筛选标签（风格/格式/色彩/构图/灯光/题材/状态/分辨率/大小/评分/质感）、智能搜索（意图识别 + 拼音 + 语义 fallback） | W1/B1-1、B1-6-1~11、A1-4 |
| 详情与编辑 | 大图预览（三层渐显）、文件信息、评分、标签 chips、分类、备注自动保存、多选批量编辑、主色调、相关资产、找相似 | W1/B3-1~9 |
| AI 能力 | 三档（关闭/云端/本地 ONNX）、云端 OpenAI 兼容配置、本地模型下载管理、批量分析、语义搜索、CLIP 零样本、自适应学习、颜色分析 | W3/A3、A3-LEARN、A1-2、A1-8 |
| PBR 贴图组 | 尾缀规则、成组树（入口暂隐藏）、规则编辑页 | W3/A6、B1-4 |
| NAS / 远程 | WebDAV + SMB + SFTP、多 NAS 聚合、主 NAS 迁移、分享链接、下载队列、弱网跳过、远程缩略图缓存 | W3/A7 系列、W4、A1-*、W1/N7/N8 |
| 多用户 / 共享 | 登录窗、账号管理、共享库画廊、提交审核、审核队列、共享只读详情、保存副本 | W5、W3/A8、W6、B1-SCOPE、B2-SHARED、B3-SHARED |
| 查重（0.16.0） | dHash 感知哈希、导入时 L1 查重通知、全库 L1/L2 扫描、候选组落表、W8 审核窗口（保留/删除/撤销）、L3 VLM 终审 | W8、W8-TB/L/R/ACT、A1-11 |
| 框架与体验 | 纯 MVVM、手写 ServiceRegistry 注入、深色/浅色主题、通知中心、状态栏、快捷键、单实例、数据根隔离 | W1/A1、C1、W3/A1 |

## 4. 本轮 0.16.0 查重改动明细

| 阶段 | 提交 | 内容 |
|---|---|---|
| DUP-A | 029bf76 | `PerceptualHashService`（dHash 64 位）、schema v12（`images.phash` + `duplicate_candidates`）、Magick 流重载、导入落 phash |
| DUP-B | 4646f94 | 导入时 L1 查重（库内 + 同批），W2 日志「疑似重复」+ 通知中心 `DuplicateDetected`，不拦截入库 |
| DUP-C | 0cf6360 | `DuplicateScanService` 全库扫描：BK-tree L1、同 model_id 分块余弦 L2、传递闭包成组、pending 清理、取消不写表 |
| DUP-D | a74fd94 | `DuplicateReviewService` + W8 窗口 + A1-11「查重」入口；删除选中复用 `ImageQueryService.Remove`（8 秒撤销）；修复删除图片时外键未清候选行的问题 |
| DUP-E | 未提交 | `IVlmImageCompare` 能力接口；`CloudAiService` 双图对比；扫描边界带（phash 11~18 / 余弦 0.88~0.92）VLM 终审，确认落 `level='L3'`、否决丢弃、无判定跳过；W8 进度与汇总文案；6 + 4 项新测试 |

DUP-E 涉及文件：

- 新增：`Services/Ai/IVlmImageCompare.cs`、`docs/superpowers/plans/2026-10-27-dup-e-plan.md`
- 修改：`CloudAiService.cs`、`DuplicateScanService.cs`、`ServiceRegistry.cs`、
  `DuplicateReviewViewModel.cs`、`CHANGELOG.md`、`docs/UI_NUMBERING.md`、
  `CloudAiServiceTests.cs`、`DuplicateScanServiceTests.cs`、`ImportViewModelTests.cs`

## 5. 未完成 / 后续开发记录

### 5.1 PEND 待排期项

| 编号 | 内容 | 涉及编号 | 状态 |
|---|---|---|---|
| PEND-001 | ImageSharp 3.x → 4.x 升级与 Six Labors Split License 合规评估 | B2-3、W3/A7-PRE | **评估完成，暂缓升级**（见 `docs/superpowers/plans/2026-10-27-pend-001-imagesharp-assessment.md`；4.x 构建需 Community 许可 key） |
| PEND-002 | B1 三分区入口重挂（收藏夹 / 高频标签 / 贴图组） | B1-2、B1-3、B1-4 | **已完成（0.17.0 移入 W9）** |
| PEND-003 | 托管库目录在线迁移（当前「修改…」恒置灰） | W3/A1-LIB-CHG、W3/A1-LIB-PROG | **已完成（0.19.0）** |
| PEND-004 | 共享收藏夹（collections.json） | W1/B2-SHARED、W1/B2-SHARED-COL | **已完成（0.18.0）** |
| PEND-005 | 跨平台（macOS / Linux / 移动端，抽象口子已留） | 全局 | **评估完成**（见 `docs/superpowers/plans/2026-10-27-pend-005-cross-platform-assessment.md`；1.0 前不启动 UI 重写） |

### 5.2 本次核对新发现的待办

| 事项 | 说明 | 建议优先级 |
|---|---|---|
| 提交 DUP-E | 已提交（6626dc5）并随 d2514a5 收尾 | 已完成 |
| W8 冒烟验收 | `tools/smoke/UiSmokeDup.ps1` 18/18 通过；真机交互建议人工复查 | 已完成（自动化） |
| L3 真实链路实测 | 需配置云端 API Key 验证 VLM 双图对比质量、耗时与费用；当前仅单测覆盖 | P1 |
| `docs/USER_GUIDE.md` | 已补查重章节、共享收藏夹与过期内容修正 | 已完成 |
| `docs/ui-layout-map/ui-layout-map.html` 过期 | 停在 0.15.1，未含 W8、A1-11、0.16.0 查重 | P2 |
| `GEMINI.md` 版本行 | 原写 0.13.7，本次已同步为 0.19.0 | 已完成 |
| 3 个仅换行差异文件 | `Themes/Generic.xaml`、`Themes/Icons.xaml`、`Views/Support/VirtualizingTilePanel.cs` 长期留在工作树，禁止 stage | 保持现状 |
| 偶发测试 | `ImportViewModelTests` 的 `vm.Added` 断言受 `Progress<T>` 异步回填影响，DUP-E 期间已改为 `AddedOnClose` | 已完成 |

## 6. 建议的后续开发路线

1. ~~收尾 0.16.0~~（已完成）。
2. ~~PEND-002 入口重挂~~（已完成，0.17.0 W9 素材组织窗口）。
3. ~~共享与迁移能力补齐~~：PEND-004 共享收藏夹（0.18.0）、PEND-003 托管库在线迁移（0.19.0）均已完成。
4. **依赖与许可**：~~PEND-001 评估~~（已完成，结论：暂缓升级）。取得 Community 许可 key（或确认商业订阅）后，再按评估文档步骤升至 4.1.2 并删除审计抑制。
5. **跨平台**：~~PEND-005 评估~~（已完成，结论：1.0 前保持 Windows 优先）。1.0 后可先做核心层抽取，再以 Avalonia 独立里程碑推进。
6. **1.0.0 正式版**：上述收尾与用户确认后再升主版本（升 1.0.0 必须用户确认）。

## 7. 验证方式

```powershell
cd c:\AI_Programming\APP\StellarHub
dotnet build -c Release
dotnet test -c Release --no-build
powershell -ExecutionPolicy Bypass -File tools\smoke\UiSmokeDup.ps1   # W8 UI 冒烟（需桌面会话）
```

## 8. 文档真源与维护规则

| 文档 | 作用 | 更新时机 |
|---|---|---|
| `GEMINI.md` | 工程规范唯一真源 | 架构/规范/里程碑状态变更 |
| `CHANGELOG.md` | 版本变更记录 | 每个版本/每次功能提交 |
| `docs/UI_NUMBERING.md` | UI 编号唯一真源 | 任何 UI 变更（新增顺延、作废不复用） |
| `docs/DEVELOPMENT_STATUS.md`（本文） | 阶段性进度汇总 | 里程碑收尾或用户要求核对时刷新 |
| `docs/USER_GUIDE.md` | 用户手册 | 面向用户的功能变更 |
| `docs/superpowers/specs/`、`plans/` | 设计真源与阶段实施计划 | 新功能立项 / 阶段开工 |
