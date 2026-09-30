# U6 实施计划 — M6 多用户功能 UI 补全（v0.1.1 补丁）

> 当前基线 v0.1.0（M0–M6）。U6 是**补丁版本**（用户 2026-09-30 决策：不立大里程碑，修订号 +0.0.1），目标版本 **v0.1.1**。
> U6 完成后，下一里程碑为 **M7 NAS 协议扩展（SMB/SFTP，目标 v0.2.0）**。

## 1. 背景：M6 的服务层/UI 缺口

M6 交付了完整服务层与登录、用户管理页签，但下列 M6 计划承诺项**未交付 UI**，功能用户实际无法通过界面使用：

| 缺口 | 现状 | U6 交付 |
|---|---|---|
| 共享库画廊视图 | `ShellViewModel.SharedImages` 有数据，但 ShellWindow 无入口、无渲染 | U6-A |
| 共享缩略图加载 | 无（NAS `thumbnails/` 无读取通道） | U6-A |
| 「提交到共享库」 | 无右键命令，SubmissionService 仅有元数据方法 | U6-B |
| 审核队列窗口（计划 W5） | 无窗口、无入口 | U6-C（实际编号 W6，见下） |
| 窗口/页签编号 | UI_NUMBERING 无登录窗口/用户管理页签条目 | U6-Z |
| 用户手册 | 无登录/账号/共享库/审核章节 | U6-Z |

**编号修正**：M6 计划把登录窗口称 W4，但 M5 的下载队列窗口已占用 W4。故：登录窗口 = **W5**，审核队列窗口 = **W6**。用户管理页签：A1–A7 已占用（A7=NAS 连接），故用户管理页签 = **A8**。

## 2. 范围与验收

1. 登录用户可在工具栏切换「本地库 / 共享库」，共享库用现有网格/列表模板渲染
2. 共享缩略图从 NAS 下载到本地共享缓存，滚动虚拟化懒加载 + 预取
3. 本地库图片右键「提交到共享库」：上传原图 + 缩略图 + pending JSON，重复哈希被拒绝
4. 管理员工具栏可见「审核队列」入口（带待审数量角标），W6 窗口可通过/拒绝
5. 审核通过：原图与缩略图归位到 `originals/`、`thumbnails/`，记录提升到 library.json
6. 共享库选中项：详情栏显示只读元数据；可「保存副本到…」
7. 仅本地模式（未登录）：以上入口全部不可见，本地库行为不变
8. 门禁：0 警告 0 错误 + 全测试绿 + R2R；UI_NUMBERING / USER_GUIDE / 冒烟脚本同步

## 3. NAS 共享目录（在 M6 结构上补 originals 与 pending/files）

```
StellarHubShared/
├── accounts.json
├── library.json
├── thumbnails/<hash前2位>/<hash>_thumb.webp      # 入库缩略图（审核通过时归位）
├── originals/<hash前2位>/<hash><ext>             # 入库原图（审核通过时归位）【新增】
└── pending/
    ├── <submit_id>.json                           # 提交元数据
    └── files/<submit_id>/
        ├── <file_name>                            # 待审原图【新增】
        └── thumb.webp                             # 待审缩略图【新增】
```

## 4. 子阶段

### U6-A：共享库画廊视图

**改动**：
- `Helpers/WebpImageDecoder.cs`【新增】：从 `GalleryThumbnailProvider.DecodeWebp` 抽出的 internal static 解码工具（WebP bytes → frozen BitmapSource，ImageSharp 解 RGBA → BGRA32），供本地与共享两条路径共用
- `Services/Nas/SharedThumbnailCache.cs`【新增】：
  - `EnsureAsync(hash)`：从 NAS `StellarHubShared/thumbnails/<hh>/<hash>_thumb.webp` 下载到本地 `%APPDATA%\Qianlan_App\StellarHub\shared-thumbs\<hash>.webp`（已存在则跳过），in-flight 去重
  - 解码后推送到 item.ThumbnailImage；失败静默保留占位
  - 弱网（NetworkConditionMonitor）时跳过
- `ViewModels/SharedGalleryViewModel.cs`【新增】：
  - 经 `SharedLibraryService.LoadAsync` 加载，映射 `SharedImageRecord → GalleryListItem → GalleryItemViewModel`
    - `Id` = 加载序号（共享库选择态不触达本地 DB 操作）；`FileHash = record.Id`；`Source=Local`（缩略图走共享缓存，避免 M5 远程分支）
  - `Items / SelectedItems / SelectionChanged / IsGridView` 与 GridZoom 等尺寸属性委托 `AppSettingsService`（复用现有 GridCard/ListRow 模板对 DataContext.GridZoom 的引用）
  - 加载完成批量 `EnsureAsync` 预取
- `Views/ShellWindow.xaml`：
  - 工具栏新增「本地库 / 共享库」分段切换（B1-SCOPE，仅 `IsSharedLibraryAvailable` 可见）
  - B2 区域新增第二个 ListBox（共享库），与现有 ListBox 互斥显隐，复用 `GridCardTemplate`/`ListRowTemplate` 与虚拟化面板；缩略图 Attach/Detach 行为接 `SharedThumbnailCache`（现有 attached behavior 的 provider 参数以接口/委托形式兼容；如改造面大，则新增 `Views/Support/SharedThumbnailBehavior.cs` 薄行为复用同一模板）
  - 左侧树/筛选与排序在共享模式禁用并提示（共享库检索在 U6-D 提供基础文本过滤：见下，可顺延）
- `App.xaml.cs` / `ShellViewModel.cs`：持有 `SharedGalleryViewModel`，切换命令，登录后创建
- 测试：`SharedGalleryViewModelTests.cs`（空库/加载映射/数量）、`SharedThumbnailCacheTests.cs`（首次下载/已缓存跳过/in-flight 去重/404 静默）

### U6-B：提交到共享库

**改动**：
- `ViewModels/GalleryItemViewModel.cs`：新增 `bool CanSubmitToShared`（已登录 + 本地来源 + 非缺失；由 Shell 刷新时赋值，默认 false 保持既有测试不变）
- `Services/Nas/SubmissionService.cs` 扩展：
  - `SubmitFromLocalAsync(GalleryItemViewModel item, string submittedBy, CancellationToken)`：
    1. 哈希查重：library.json 命中 → 抛「已在共享库」；pending 命中 → 抛「已提交待审」
    2. `pending/files/<submitId>/` MKDIR；`UploadFileAsync` 原图
    3. 缩略图：本地 `ThumbnailService.GetThumbnailPath(hash, Thumb)` 存在则直接上传，否则先 `ThumbnailService.Regenerate` 生成后上传为 `thumb.webp`
    4. 保存 pending JSON（含 file 尺寸/格式/标签、thumbnail_nas_path = `pending/files/<id>/thumb.webp`）
  - 全部为 async，UI 显示「提交中」（复用 TopBar.IsImporting? 否——新增独立状态经 NotificationService 进度提示）
- `Views/ShellWindow.xaml` 右键菜单 B2 新增「提交到共享库」项（权限 + 选中项 CanSubmitToShared 双门控）
- 成功/失败经 `NotificationService` 出通知
- 测试：扩展 `SubmissionServiceTests`（提交产出三件套/库内重复拒绝/pending 重复拒绝/缺失图拒绝）

### U6-C：审核队列窗口（W6）

**改动**：
- `Services/Nas/INasConnector.cs`：新增 `Task MoveAsync(string sourceRemotePath, string destRemotePath, CancellationToken)`（WebDAV MOVE；SMB/SFTP 里程碑各自实现）
  - `WebDavNasConnector` 实现 MOVE（覆盖目标时群晖需 overwrite 头/先 DELETE 后 MOVE 兜底）
  - `InMemoryNasConnector` 实现字典移动（含目标已存在覆盖、源不存在抛 NotFound）
  - 测试 stubs（INasConnectorContractTests/RemoteImageSourceTests/ThumbnailServiceRemoteTests/StubNasConnectorFactory）补空实现
- `ViewModels/ReviewQueueViewModel.cs`【新增】：加载 pending 列表（行：缩略图 via `SharedThumbnailCache` 的 pending 路径重载或下载 `pending/files/<id>/thumb.webp`、文件名/提交人/时间/尺寸）；`ApproveCommand/RejectCommand`
- `Views/ReviewQueueWindow.xaml(.cs)`【新增 W6】：720×540，CenterOwner，风格对齐下载队列窗口；操作后刷新行列表
- `SubmissionService.ApproveAsync` 增强归位：
  - MOVE 原图 `pending/files/<id>/<file>` → `originals/<hh>/<hash><ext>`
  - MOVE 缩略图 → `thumbnails/<hh>/<hash>_thumb.webp`
  - 删除 `pending/files/<id>` 空目录（best effort）
  - 提升记录时 `SourceNasPath = StellarHubShared/originals/<hh>/<hash><ext>`
- `ShellViewModel` + 工具栏：管理员可见「🛡 审核队列」按钮（`Permissions.CanApproveSubmissions`），pending 数量角标（登录后 + 审核窗口关闭后刷新）；事件 `ShowReviewQueueDialog` 由 App.xaml.cs 订阅开窗
- 测试：`ReviewQueueViewModelTests.cs`（列表/通过后 pending 清空 + library 命中 + 文件归位/拒绝删除/角标计数）

### U6-D：共享库详情与保存副本

**改动**：
- `ViewModels/SharedDetailViewModel.cs`【新增】：绑定共享选中项，只读展示文件名/尺寸/格式/大小/评分/分类/标签/提交人/审核人/审核时间
- `Views/ShellWindow.xaml` B3 区域：共享库模式下详情栏切换为 SharedDetail 模板（本地模式仍是现有 DetailViewModel）
- 命令「保存副本到…」：SaveFileDialog 选路径 → `INasConnector.DownloadFileAsync(sourceNasPath)` 下载
- 命令「复制 NAS 路径」：复制 SourceNasPath 到剪贴板
- 共享库基础过滤：工具栏搜索框在共享模式下对已加载 SharedImages 做文件名/标签内存过滤（非服务端检索）
- 测试：`SharedDetailViewModelTests.cs`（字段映射/只读）、保存副本命令测试（InMemory 下载断言）

### U6-Z：收尾

1. csproj `<Version>` 0.1.0 → **0.1.1**；`AppInfo.MilestoneLabel` → "M6 多用户管理（补全）"
2. `CHANGELOG.md` 新增 [0.1.1] 节，按 U6-A~D 组织
3. `GEMINI.md` 更新状态（M6 UI 补全、originals/pending-files 结构、INasConnector.MoveAsync 为 M7 铺路）
4. `docs/UI_NUMBERING.md` 补齐：
   - W5 登录窗口（W5/各控件）、W6 审核队列窗口
   - W3/A8 用户管理页签
   - W1 B1-SCOPE 本地/共享切换、B2 共享库 ListBox、右键「提交到共享库」、B3 共享详情
   - 工具栏「审核队列」+ 角标
5. `docs/USER_GUIDE.md` 新增章节：登录与「仅本地模式」、账号与用户管理（管理员）、共享图库浏览与保存副本、提交图片、审核队列
6. `tools/smoke/UiSmoke*.ps1`：评估加入登录窗口/审核窗口控件步骤（无真实 NAS 环境时以窗口可打开/控件存在为限，中文脚本存 UTF-8 BOM）
7. 门禁：`powershell -ExecutionPolicy Bypass -File .\build.ps1 -Mode Prepare`（0 警告 0 错误 + 全测试绿 + R2R）；记录 staging 体积对比
8. Finalize 发布 dist/StellarHub_v0.1.1 五件套；tag 不自动创建

## 5. 不在 U6 范围

- SMB/SFTP 协议实现（下一大里程碑 M7，v0.2.0）
- 共享收藏夹 collections.json、乐观锁并发控制
- 共享库服务端检索/标签树联动（仅内存过滤）
- 账号自助修改密码、密码找回
- macOS

## 6. 风险

- 复用现有卡片模板要求 SharedGalleryViewModel 暴露与 GalleryViewModel 同名尺寸属性；若模板内绑定路径分叉，以新增薄模板兜底，不强改本地模板
- WebDAV MOVE 跨服务器实现差异（群晖/威联通）：先 DELETE 目标再 MOVE 的兜底顺序，404/409 均按场景处理
- 大原图上传阻塞：全部后台线程 + 通知，禁止 UI 线程调用
