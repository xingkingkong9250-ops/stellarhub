# M8 实施规划 — 多 NAS 聚合 + 主 NAS 强制 + 副库权限分级 + 主 NAS 迁移

**版本**：v0.2.1 → v0.3.0（NO.M，+0.1.0，6 子阶段独立提交）
**指令代号**：用户原指令 NO.P，与改动规模冲突，经确认升级为 NO.M

---

## Context（为什么做）

v0.2.1 已支持多 NAS **配置**（`NasConnections` 列表 + `IsPrimary` 标记 + `SetPrimary` 命令），但运行时仅"用主 NAS"——`ServiceRegistry.SharedStorage/Accounts/SharedLibrary/Submissions` 全是单实例，登录后只绑定一个 connector。

用户实际需求：
1. **聚合访问**：多个 NAS 的数据在主窗口统一呈现，用户视角不区分主/副库
2. **强制主 NAS**：必须有一个 IsPrimary=true 的 NAS 存放用户必要数据（accounts/library/pending/submissions）
3. **副库扩充存储**：副库（含 ShareLink 公开链接）仅作存储扩充——管理员读写下载、普通用户只读可下载
4. **主 NAS 可切换 + 自主迁移**：管理员可把任意副库改为主 NAS，迁移所有必要数据，旧主降级为副库
5. **管理员数据搬运**：把主 NAS 的部分数据移到副库以减轻主库压力
6. **显示来源徽章开关**：管理员可开启"显示数据来源"，每条数据加 nas://{id} 标识

底层基础设施已部分具备（关键发现）：
- `ImageRecord.NasConnectionId` 字段（schema v6，M5-D 已加）
- `GalleryListItem.NasConnectionId` 字段已存在但 U6-A `SharedGalleryViewModel.Load` 未填值
- `GalleryThumbnailProvider` 已按 spec.Id 缓存 connector（`Dictionary<int, INasConnector>`，line 46）
- `NasConnectionSpec.IsPrimary` 字段（v0.2.1 已加）
- `DbMigrator` forward-only 迁移机制（当前 v7）

瓶颈：`ServiceRegistry` 单实例假设 + `SharedStorageService` 单 connector 构造 + `SharedImageRecord` 无来源标记字段。

---

## 子阶段

### M8-A · 配置数据模型 + NasRole 枚举 + 强制主 NAS

**目标**：把"主/副/分享链接"三态升级为 `NasRole` 枚举；保留 `IsPrimary` 反序列化兼容；启动强制有主 NAS。

**关键文件**：
- `src/QL.World/Services/Nas/NasRole.cs`（新建）— `enum NasRole { Primary=0, Secondary=1, ShareLink=2 }`（Primary=0 让旧 config 缺失 Role 字段时默认 Primary 不破坏既有 IsPrimary=true 的连接）
- `src/QL.World/Services/Nas/NasConnectionSpec.cs` — 新增 `NasRole Role { get; set; } = NasRole.Primary`；保留 `IsPrimary` 不标 Obsolete（计算属性 `Role == NasRole.Primary`）；`[OnDeserialized]` 钩子：若 JSON 含旧 `IsPrimary` 字段，按 IsPrimary 反推 Role；ShareLink 角色校验 Username/EncryptedPassword 必须空
- `src/QL.World/Services/AppSettingsService.cs` — 新增 `ValidateNasRoles()`：≤1 个 Primary；无 Primary 时列表第一项设为 Primary（沿用 v0.2.1 兜底）；Load() 后调用
- `src/QL.World/Models/SharedImageRecord.cs` — 新增 `[JsonPropertyName("source_nas_id")] public int? SourceNasId { get; init; }`（持久化于 library.json，便于跨重启保留来源；合并器仍按 spec.Id 覆盖以防 spec 被删除后 stale）
- `src/QL.World/App.xaml.cs` line 394-422 ShowLoginGate — 改为强制取 Role==Primary 的 spec（沿用 v0.2.1 兜底逻辑但语义更清晰）；无 Primary 时提示用户去 A7 配置主 NAS

**复用**：`AppSettingsService.AddNasConnection/UpdateNasConnection` 签名不变；`AccountService.InitializeAdminAsync(username, password, ct)` 不变

**测试**：旧 config 反序列化兼容；多 Primary 收敛为 1；ShareLink 强制无密码；启动无 Primary 自动收敛

---

### M8-B · 多实例 ServiceRegistry + 副库连接注册

**目标**：打破单实例假设，引入 `NasConnectionRegistry` 持多 connector + SharedStorageService 副本；旧字段保留为"主 NAS 视图"别名避免大面积改调用点。

**关键文件**：
- `src/QL.World/Services/Nas/NasConnectionRegistry.cs`（新建）— `Dictionary<int, (INasConnector Connector, SharedStorageService Storage)>`；`GetPrimary()` / `GetSecondary(int id)` / `EnsureAll(IReadOnlyList<NasConnectionSpec>)`（懒构造并缓存，复用 `GalleryThumbnailProvider._connectors` 同款模式 line 269-285）；实现 `IDisposable` 统一释放
- `src/QL.World/Services/ServiceRegistry.cs` — 旧 `SharedStorage/Accounts/SharedLibrary/Submissions`（line 68/71/74/77）保留为主 NAS 视图（向后兼容外部调用）；新增 `NasConnectionRegistry NasRegistry { get; set; }` + `IReadOnlyList<SharedStorageService> SecondaryStorages`
- `src/QL.World/App.xaml.cs` line 441-472 登录后链路 — 改为先 `NasRegistry.EnsureAll(settings.NasConnections)` 再让旧字段引用 `registry.GetPrimary()` 实例
- `src/QL.World/Services/PermissionService.cs` — 新增 `CanWriteToSecondaryNas` (IsAdmin)、`CanDownloadFromSecondaryNas` (IsLoggedIn)；保留 `CanSubmitToLibrary` 行为不变（仍指主库）

**复用**：`NasConnectorFactory.Create(spec, plaintextPassword)`（line 35）零改动；`SharedStorageService`/`SharedLibraryService`/`SubmissionService` 实例化模式不变

**测试**：3 NAS（1 主 2 副）启动后 NasRegistry 缓存 3 connector；副库 connector 失败主库仍可用；Dispose 后无 socket 泄漏

---

### M8-C · 聚合视图 + 来源注入

**目标**：`SharedGalleryViewModel.Load` 保留单参签名，内部按多源合并并填 `NasConnectionId`；用户视角无主副之分。

**关键文件**：
- `src/QL.World/Services/Nas/AggregatedSharedLibraryService.cs`（新建）— 包装 `NasConnectionRegistry`：`LoadAllAsync(ct)` 并发调每库 `SharedLibraryService.LoadAsync`，给每条 `SharedImageRecord` 用 `with { SourceNasId = spec.Id }` 注入；按 `Id`（hash）去重，主库优先；返回单一 `IReadOnlyList<SharedImageRecord>`
- `src/QL.World/Services/ServiceRegistry.cs` — 新增 `AggregatedLibrary` 字段；旧 `SharedLibrary` 仍指向主库实例（兼容）
- `src/QL.World/ViewModels/SharedGalleryViewModel.cs` line 86-117 `Load` — 在 `GalleryListItem` init 列表里塞 `NasConnectionId = record.SourceNasId`、`Source = ImageSource.Remote`，让 `GalleryThumbnailProvider.CreateSource`（line 250-261）自动按 NasConnectionId 路由到对应副库 connector
- `src/QL.World/ViewModels/SharedDetailViewModel.cs` line 16 — `_connector` 单实例改为 `NasConnectionRegistry`；`SaveCopyAsync` 按 `_record.SourceNasId` 取 connector
- `src/QL.World/ViewModels/ShellViewModel.cs` line 238-250 `LoadSharedGalleryAsync` — 改为先调 `AggregatedSharedLibraryService.LoadAllAsync` 再喂给 `SharedGallery.Load`

**复用**：`GalleryThumbnailProvider.GetConnector`（line 269）/ `RemoteImageSource` / `GalleryListItem.NasConnectionId`（已有，只需被填值）；`SharedThumbnailCache` 暂保留为主库 pending 缩略图服务

**测试**：主+副各 50 条聚合后 100 条；同 hash 在主副都有时仅保留主库版本；`SourceNasId==2` 的 item 通过 GalleryThumbnailProvider 走副库 connector 不报"连接不存在"

---

### M8-D · 副库权限分级 + ShareLink 副库

**目标**：管理员可写副库（上传/删除），普通用户对副库只读但可下载；ShareLink 走匿名 WebDAV。

**关键文件**：
- `src/QL.World/Services/PermissionService.cs` — 新增 `CanMigratePrimary` (IsAdmin)；`CanSubmitToLibrary` 行为不变（仍指主库）；新加 `CanSubmitToSecondaryNas` (IsAdmin)
- `src/QL.World/Services/Nas/NasConnectionSpec.cs` — `Role==ShareLink` 时构造期校验 `Username/EncryptedPassword` 必须为空
- `src/QL.World/Services/Nas/WebDav/WebDavConnectorFactory.cs` — ShareLink 创建 connector 时跳过 Basic-auth header，使用 `HttpClientHandler.Credentials=null`
- `src/QL.World/ViewModels/ShellViewModel.cs` line 253-291 `SubmitToSharedAsync` — 增加 `targetNasId` 参数；非 admin 提交副库时返回新 `Outcome=NotPermitted`
- `src/QL.World/Views/ShellWindow.xaml` — 提交到共享库菜单：admin 显示主库/各副库子项；普通用户右键"提交到副库"项不显示
- `src/QL.World/ViewModels/SettingsViewModel.cs` `AddNasConnection` — 默认 `Role=Secondary`；ShareLink 单独入口"添加分享链接"，隐藏密码框
- `src/QL.World/Views/SettingsWindow.xaml` A7 — ShareLink 副库列表用 🔗 图标区分；管理员副库 Tab 加"删除文件"按钮（普通用户隐藏）

**复用**：`WebDavNasConnector` 零改动（HttpClient 配置由 factory 控制）；`SubmissionService` 主流程不变

**测试**：普通用户调用 `SubmitToSecondaryNas` 返回 `NotPermitted`；ShareLink connector 无 Authorization header；管理员可删除副库 originals/shard 文件

---

### M8-E · 主 NAS 迁移 + 管理员数据搬运 + 来源徽章开关

**目标**：管理员"设为主 NAS"按钮触发自主迁移 + 切换；管理员可手动搬运主→副特定文件；A7 加"显示数据来源"开关。

**关键文件**：
- `src/QL.World/Services/Nas/PrimaryMigrationService.cs`（新建）— 核心 `MigrateAsync(int newPrimaryId, IProgress<MigrationProgress>, CancellationToken)`：
  1. 校验目标 connector 可写（`TestConnectionAsync`）
  2. `EnsureSharedStructureAsync`（line 36-43）
  3. **阶段 1**：复制 `accounts.json` / `library.json` / `pending/*.json`（逐文件 OpenReadAsync → UploadFileAsync）
  4. **阶段 2**：遍历 `originals/` + `thumbnails/`，按 shard 拷贝；优先同主机 MOVE，跨主机 download→upload；每文件先 GetMetadataAsync 探测存在则跳过（幂等）
  5. **阶段 3**：旧主库 `Role=Secondary`，新主库 `Role=Primary`，落盘 `AppSettingsService`
  6. 失败回滚：保留旧主标记，新主库写 `.migration-backup/<ts>.json` 记录已上传文件清单，下次启动提示"上次迁移失败，已回滚"
  7. 成功后 `ServiceRegistry.NasRegistry` 重新解析
- `src/QL.World/ViewModels/Support/DataTransferService.cs`（新建）— 管理员手动数据搬运：选 N 张共享图 → `MoveAsync(sourceConnector, sourcePath, destConnector, destPath)` 走 download→upload fallback；搬运后目标库 `library.json` AddOrUpdate + 源库 Remove
- `src/QL.World/ViewModels/SettingsViewModel.cs` line 648-665 `SetPrimary` — 增加重载 `SetPrimaryWithMigrationAsync`：弹确认框"将迁移 N 个文件（约 X MB），期间共享库不可用，是否继续？" → 调 PrimaryMigrationService；保留原 `SetPrimary` 仅切标记（用于已含完整 StellarHubShared 的目标库）
- `src/QL.World/Views/SettingsWindow.xaml` A7 — "设为主 NAS"按钮改为下拉：① 仅切换标记 ② 切换并迁移数据；新增"显示数据来源"CheckBox（绑定 `AppSettingsService.ShowSourceBadge`，新布尔设置项默认 false）；副库列表加"搬运到此处..."按钮（admin）
- `src/QL.World/Services/AppSettingsService.cs` — 新增 `bool ShowSourceBadge { get; set; } = false`（持久化 config.json）
- `src/QL.World/Views/ShellWindow.xaml` 共享画廊 ItemTemplate — 新增 `<TextBlock Text="{Binding SourceBadge}" Visibility="{Binding ...ShowSourceBadge...}" />` 徽章；`SourceBadge` 在 `SharedGalleryViewModel.Load` 设为 `nas://{NasConnectionId}` 字符串（覆盖原 "共享" 静态值 line 109）

**复用**：`INasConnector.MoveAsync` / `DownloadFileAsync` / `UploadFileAsync` / `SharedStorageService.EnsureSharedStructureAsync`（line 36）；`SubmissionService.TryMoveAsync` 模式借鉴

**测试**：迁移 100 张图 + library.json 后新主库可登录且画廊完整；中途断网 → 旧主仍可读，重试成功；`accounts.json` 必须最优先复制；二次幂等；管理员搬运后主库 library 移除条目、副库 library 添加条目

---

### M8-Z · Finalize 门禁 + 版本/UI_NUMBERING/CHANGELOG 同步

**目标**：按项目规则收尾发布 v0.3.0。

**关键文件**：
- `src/QL.World/QL.World.csproj` — `<Version>0.3.0</Version>`
- `docs/UI_NUMBERING.md`（如存在）— 新增 A7 "设为主 NAS（含迁移）"按钮编号、共享画廊"显示数据来源"徽章编号、副库"搬运到此处"按钮编号
- `CHANGELOG.md` — 新增 `## v0.3.0 (2026-XX-XX) — M8 多 NAS 聚合 + 主 NAS 迁移`
- `GEMINI.md` — 里程碑状态 `M0–M8 功能完成，版本号 0.3.0`，新增 M8 段落
- `src/QL.World/Helpers/AppInfo.cs` — `MilestoneLabel` 改为 `"M8 多 NAS 聚合与主 NAS 迁移"`

**门禁**：
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Mode Prepare` 必须全过（restore + Release 零警告 + 全测试 + R2R 发布）
- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Mode Finalize` 产出 `dist/StellarHub_v0.3.0/` 五件套 + manifest sha256
- 工作树干净，commit 历史清晰（6 个子阶段分别提交）

---

## 关键技术决策

1. **NasRole 枚举**：`Primary=0`（默认值）、`Secondary=1`、`ShareLink=2`。`IsPrimary` 保留不标 Obsolete，作为计算属性 `Role == NasRole.Primary`；`[OnDeserialized]` 钩子按旧 IsPrimary 反推 Role，兼容旧 config。

2. **SharedImageRecord.SourceNasId**：`init` only + `[JsonPropertyName("source_nas_id")]`，持久化于 library.json 便于跨重启保留来源；合并器运行时仍按 spec.Id 覆盖以防 spec 删除后 stale。

3. **ServiceRegistry 多实例**：`NasConnectionRegistry` 持 `Dictionary<int,(INasConnector, SharedStorageService)>`；旧 `SharedStorage/Accounts/SharedLibrary/Submissions` 字段保留为"主 NAS 视图"别名，避免大面积改 ShellViewModel/SubmissionService 调用点。

4. **SharedGalleryViewModel 聚合**：保留 `Load(IReadOnlyList<SharedImageRecord>)` 签名（line 86）；`ShellViewModel.LoadSharedGalleryAsync` 改为先调 `AggregatedSharedLibraryService.LoadAllAsync` 再喂 Load。`GalleryListItem.NasConnectionId` 在 init 时由 `record.SourceNasId` 填入，复用 `GalleryThumbnailProvider` 已有多 connector 缓存。

5. **主 NAS 迁移事务/回滚**：NAS 无文件锁，无法做真正跨主机事务；采用"先复制后切换 + 失败时新主库写 `.migration-failed` 标记"策略；`accounts.json` 必须最后覆盖（避免登录到一半状态）；提供"重试上次迁移"入口。文件级幂等：每个目标路径先 GetMetadataAsync 探测，存在则跳过。

6. **ShareLink 副库认证**：`Role==ShareLink` 时 `Username/EncryptedPassword` 必须为空；`WebDavConnectorFactory` 检测 Role 后构造无 Credentials 的 HttpClient；UI 上 ShareLink 副库在 A7 列表用 🔗 图标区分。SmbNasConnector 同理跳过 `WNetAddConnection2` 凭据。

7. **"显示数据来源"开关**：`AppSettingsService.ShowSourceBadge` 布尔（持久化 config.json，默认 false）；`ShellWindow.xaml` 共享画廊 ItemTemplate 内 `<TextBlock Visibility="{Binding DataContext.ShowSourceBadge, RelativeSource={RelativeSource AncestorType=ItemsControl}, Converter={StaticResource BoolVis}}" Text="{Binding SourceBadge}" />`，`SourceBadge` 在 `SharedGalleryViewModel.Load` 设为 `nas://{NasConnectionId}` 字符串（覆盖原 "共享" 静态值 line 109）。

8. **不动 SQLite schema**：`ImageRecord.NasConnectionId` 字段（v6 已加）足够支撑来源标记；M8 不需要 v8.sql；`DbMigrator.CURRENT_VERSION=7` 不变。

---

## 风险点 + 缓解

| 风险 | 缓解 |
|---|---|
| 副库 connector 长期持有 HttpClient，弱网泄漏 socket | `NasConnectionRegistry` 实现 `IDisposable`，App 退出时统一 Dispose；连接失败超过 3 次标记 stale |
| `library.json` 多源合并后 hash 重复导致数据丢失 | 主库永远优先；副库重复条目放入 `.duplicates` 临时列表供 admin 人工合并 |
| 主 NAS 迁移中断 → 双库不一致 | 阶段化 + 每文件幂等 + `.migration-backup` 元数据；切换 Role 是最后一步 |
| ShareLink 副库被主库权限误判为可写 | `PermissionService` 强校验 `Role==ShareLink ⇒ CanWrite=false`，独立于 IsAdmin |
| `ServiceRegistry` 单实例字段被外部组件假设仍单库 | 保留旧字段名作为"主 NAS 视图"别名 |
| UI_NUMBERING 漂移 | M8-Z 阶段集中更新文档，与 csproj 版本同提交 |
| 副库 library.json 可能没有 source_nas_id 字段（旧 NAS） | 合并器始终按 spec.Id 覆盖 SourceNasId，反序列化时 null 也安全 |

---

## 验证方法

1. **编译**：`dotnet build src\QL.World\QL.World.csproj -c Release`（TreatWarningsAsErrors 零警告）
2. **单元/集成测试**：新增 `NasConnectionRegistryTests`、`AggregatedSharedLibraryTests`、`PrimaryMigrationServiceTests`、`DataTransferServiceTests`（用 in-memory fake connector 实现 `INasConnector`），目标 +30 测试，总数从 519 提升到 ~550
3. **Prepare 门禁**：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Mode Prepare` 必须全过
4. **Finalize**：`powershell.exe -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Mode Finalize` 产出 `dist/StellarHub_v0.3.0/` 五件套 + MANIFEST_SHA
5. **手测脚本**：配置 1 主 + 1 副 WebDAV + 1 ShareLink HTTP，登录管理员后聚合视图显示 3 库条目；普通用户对副库"提交"按钮不可见；执行"设为主 NAS + 迁移"后重启登录新主库；管理员开启"显示数据来源"开关后共享画廊每张图带 nas:// 徽章

---

## 关键文件清单（按子阶段分组）

**M8-A**：`Services/Nas/NasRole.cs`(新)、`Services/Nas/NasConnectionSpec.cs`、`Services/AppSettingsService.cs`、`Models/SharedImageRecord.cs`、`App.xaml.cs`

**M8-B**：`Services/Nas/NasConnectionRegistry.cs`(新)、`Services/ServiceRegistry.cs`、`App.xaml.cs`、`Services/PermissionService.cs`

**M8-C**：`Services/Nas/AggregatedSharedLibraryService.cs`(新)、`Services/ServiceRegistry.cs`、`ViewModels/SharedGalleryViewModel.cs`、`ViewModels/SharedDetailViewModel.cs`、`ViewModels/ShellViewModel.cs`

**M8-D**：`Services/PermissionService.cs`、`Services/Nas/NasConnectionSpec.cs`、`Services/Nas/WebDav/WebDavConnectorFactory.cs`、`ViewModels/ShellViewModel.cs`、`Views/ShellWindow.xaml`、`ViewModels/SettingsViewModel.cs`、`Views/SettingsWindow.xaml`

**M8-E**：`Services/Nas/PrimaryMigrationService.cs`(新)、`ViewModels/Support/DataTransferService.cs`(新)、`ViewModels/SettingsViewModel.cs`、`Views/SettingsWindow.xaml`、`Services/AppSettingsService.cs`、`Views/ShellWindow.xaml`

**M8-Z**：`src/QL.World/QL.World.csproj`、`CHANGELOG.md`、`GEMINI.md`、`Helpers/AppInfo.cs`、`UI_NUMBERING.md`（如存在）
