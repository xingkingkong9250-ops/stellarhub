# M5 实施计划 — NAS 远程库 + 低带宽浏览（v1.6.0）

## 背景与范围

- 对齐《软件开发文档20260926.docx》FR-3（NAS 总库与本地下载）与 FR-4（低带宽流畅浏览）核心功能需求。
- M5 是首个引入**远程数据源**的里程碑：当前 QL_World_dotnet 全部基于本地文件系统（`ImportService`/`ThumbnailService`/`ImageQueryService`/`FileWatcherService` 全用 `Path`/`File`/`Directory`/`FileSystemWatcher` API），M5 在保留本地库基线的同时新增 WebDAV 远程库。
- 多用户管理（FR-7）**不在 M5 范围**，留待 M6 单独里程碑；但 M5 引入的平台抽象层（`INasConnector`/`IFileSystemAccessor`/`IUserIdentity`）为 M6+ 的 macOS/Linux/移动端与多用户权限预留口子（U5 决策）。
- 当前基线 v1.5.0（U5 品牌升级已发布）。M5 目标版本 v1.6.0。

## 已确认决策

### U5 阶段已确认（2026-09-29，不可推翻）

1. 品牌已改为 千星汇图 / StellarHub（U5 完成）。
2. 跨平台：先做 Windows 跑通核心，**M5+ 预留平台抽象口子**（`INasConnector`/文件系统/用户识别抽象层），便于后续 macOS/Linux 及移动端。M5 应引入这层抽象。
3. 启动顺序：U5 品牌补丁已完成，现在做 M5 NAS。
4. 数据目录：U5 已迁移 `%APPDATA%\Qianlan_App\QL_World\` → `StellarHub\`（原子移动 + `.migrated` 标记 + 失败 fallback）。

### M5 阶段已确认（2026-09-29 AskUserQuestion）

5. **M5 范围切片**：NAS + 低带宽（FR-3 + FR-4）。多用户（FR-7）留 M6 单独做。范围聚焦、可发布、抽象层已为 M6 留口子。
6. **NAS 协议优先级**：WebDAV 优先。基于 `HttpClient`（.NET 内置）零新依赖；局域网 + 远程通吃；跨平台友好（为 M6+ macOS/Linux 留路）；Windows/群晖/威联通 NAS 都内置 WebDAV。SMB/SFTP 后续里程碑加。
7. **NAS 库索引模式**：客户端扫描 + 本地索引。软件连 NAS 后扫描原图目录，元数据 + hash 写本地 SQLite（与现有 `ImportService` 流程一致，只是源路径变 NAS WebDAV URL/path）。零服务端依赖、与现有库结构兼容。缺点：首次扫描慢（M5-H 预加载 + 缓存策略缓解）。
8. **新 NuGet 依赖策略**：零新依赖。WebDAV 走 `HttpClient`（.NET 内置）；SMB 走 Win32 P/Invoke（`mpr.dll`，后续里程碑）；SFTP 暂缓或后续里程碑加 `SSH.NET`。staging 体积预期 ≈190.46MB 不变（符合 QianLan_YinWu 最小依赖原则）。

## 差距分析（文档 vs 当前 QL_World_dotnet v1.5.0）

| 文档需求 | 当前实现 | M5 缺口 |
|---|---|---|
| FR-3 NAS 作为唯一总库 | 仅本地 FS，`DatabaseService` 单一 SQLite `qlworld.db` | 引入 NAS 远程库作为**可选第二数据源**（本地库保留，不强制"唯一总库"——单用户桌面场景本地库仍有价值） |
| FR-3 SMB/WebDAV/SFTP 连接 | 无 NAS 连接抽象 | M5-B 实现 WebDAV；`INasConnector` 抽象为 SMB/SFTP 留口子 |
| FR-3 浏览只拉缩略图+元数据，原图按需加载 | `ThumbnailService` 生成 WebP 后 `file_path` 直接读本地原图 | M5-E 远程图片走"下载到临时目录→解码→生成缩略图→删除临时文件"路径；原图按需 `RangeDownload` |
| FR-3 下载队列/断点续传/失败重试 | 无 | M5-G 新增 `DownloadQueueService` + `download_queue` 表 + 断点续传（Range）+ 3 次指数退避 |
| FR-4 三级图片策略（thumb/preview/original） | 仅一级缩略图（256px WebP，M4-A 已有虚拟滚动） | M5-F 加 `preview` 级（1024px）+ `original` 级按需加载；旧 `<hash>.webp` 迁移为 `<hash>_thumb.webp` |
| FR-4 缩略图本地缓存 + 懒加载 + 虚拟滚动 | 本地缓存有，虚拟滚动有（M4-A），懒加载有 | 已满足，M5 仅扩展为多级 |
| FR-4 预加载邻近图片 | 无 | M5-H 预加载当前视图前后各 10~20 张缩略图 |
| FR-4 弱网优先显示已缓存内容 | 无弱网检测 | M5-H 弱网检测（基于最近请求失败率/延迟），弱网下只显示已缓存、不发远程请求 |
| FR-4 渐进式加载 thumbnail→preview→original 三级淡入 | 无 | M5-H 三级淡入（每层淡入替代前一层，避免闪烁） |
| FR-4 LRU 缓存上限 | 无上限（thumbnails/ 目录无限增长） | M5-F `AppSettings.ThumbnailCacheLimitMb`（默认 1024MB），后台清理超额 |
| FR-7 身份识别/角色/隔离 | 无 | **不在 M5 范围**（M6）；但 `IUserIdentity` 抽象在 M5-A 引入，M5 仅用于日志/设置分区，不做权限校验 |

## 改动文件清单（按子阶段）

### M5-A：平台抽象层（INasConnector / IFileSystemAccessor / IUserIdentity）

新增 `src/QL.World/Services/Nas/` 子目录：

| 文件 | 改动 |
|---|---|
| `Services/Nas/INasConnector.cs` | NAS 连接抽象：`TestConnectionAsync`/`ListFilesAsync`/`GetMetadataAsync`/`DownloadFileAsync`/`DownloadRangeAsync`/`UploadFileAsync`/`MakeDirectoryAsync`/`DeleteAsync`。返回 `NasFileInfo`（Name/Path/Size/ModifiedAt/IsDirectory/ContentType） |
| `Services/Nas/IFileSystemAccessor.cs` | 统一本地/远程文件访问抽象：`FileExists`/`GetFileInfo`/`OpenRead`/`EnumerateFiles`/`GetFileSize`。两个实现：`LocalFileSystemAccessor`（包装 `System.IO`，现有行为）+ `RemoteFileSystemAccessor`（基于 `INasConnector`，M5-D 引入） |
| `Services/Nas/NasConnectionSpec.cs` | 连接配置 Model：`Id`/`Name`/`Protocol`（WebDAV）/`Host`/`Port`/`RootPath`/`Username`/`EncryptedPassword`/`UseTls`。`Protocol` 枚举为 M6+ SMB/SFTP 预留 |
| `Services/Nas/NasFileInfo.cs` | 远程文件元数据 record |
| `Services/Nas/NasConnectionException.cs` | 连接失败/超时/认证失败区分异常类型 |
| `Helpers/IUserIdentity.cs` | 用户识别抽象：`Username`/`DeviceName`/`CombinedId`（`用户名@设备名`）。M5 仅用于日志/设置分区；M6+ 用于权限校验 |
| `Helpers/UserIdentityService.cs` | Windows 实现：`Environment.UserName` + `Environment.MachineName`。抽象方法签名便于 M6+ macOS 实现（`NSUserName`/`SCDynamicStoreCopyComputerName`） |

**测试**（`tests/QL.World.Tests/Services/Nas/`）：
- `INasConnector` 契约测试（stub 实现，验证接口形状）
- `UserIdentityServiceTests`：Windows 下 `Username`/`DeviceName` 非空，`CombinedId` 格式 `xxx@yyy`

**风险与对策**：
- `INasConnector` 接口形状必须为 M6+ SMB/SFTP 留路：`DownloadRangeAsync(offset, length)` 是 SMB/SFTP 也支持的抽象（SMB 走 `Seek`/`Read`，SFTP 赴 `Seek`/`Read`），不要硬编码 WebDAV 的 `Range` header 语义到接口签名里。
- `IFileSystemAccessor` 与现有 `ImportService`/`ThumbnailService` 的本地 FS 调用一一对应，M5-D/E 才真正替换实现。

### M5-B：WebDAV 连接器实现

| 文件 | 改动 |
|---|---|
| `Services/Nas/WebDavNasConnector.cs` | 基于 `HttpClient` 的 WebDAV 实现：PROPFIND（`ListFilesAsync`/`GetMetadataAsync`，解析 multistatus XML）、GET（`DownloadFileAsync`）、GET + Range header（`DownloadRangeAsync`）、PUT（`UploadFileAsync`）、MKCOL（`MakeDirectoryAsync`）、DELETE。Basic Auth / Bearer Auth。超时 30s（M3-D 学到的 HttpRequestMessage 不能重发：416/重试必须新建 request） |
| `Services/Nas/WebDav/WebDavResponseParser.cs` | PROPFIND multistatus XML 解析（`XDocument` 解析 `D:response`/`D:propstat`/`D:prop/D:getcontentlength`/`D:getlastmodified`/`D:resourcetype`） |
| `Services/Nas/WebDav/WebDavRequestBuilder.cs` | 构造 PROPFIND/PROPPATCH 请求体（避免字符串拼接 XML） |
| `Interop/DpApiProtector.cs` | 已存在（M3 用）；M5 复用，NAS 密码同样走 DPAPI `CurrentUser` 加密 |

**测试**：
- `WebDavNasConnectorTests`：用 `StubHttpMessageHandler`（实现 `HttpMessageHandler`）返回固定的 PROPFIND XML / GET 字节流，验证：
  - `ListFilesAsync` 解析 multistasis 返回正确 `NasFileInfo[]`
  - `DownloadRangeAsync` 请求带 `Range: bytes=0-1023` header
  - 401 Unauthorized → `NasConnectionException(AuthFailed)`
  - 超时 → `NasConnectionException(Timeout)`
  - 416 Range Not Satisfiable → 新建 request 重试一次（M3-D 教训）
  - `HttpRequestMessage` 不能重发：验证 `DownloadRangeAsync` 内部每次新建 request

**风险与对策**：
- WebDAV 服务器返回 XML 命名空间差异（群晖 `D:` 前缀 vs 威联通 `DAV:` 默认命名空间）：解析器必须用 `XNamespace D = "DAV:"` 而非硬编码前缀。
- PROPFIND `Depth: 1` 必须显式设置（部分服务器默认 `Depth: 0` 只返回根）。
- TLS 证书：默认信任系统 trust store；自签证书选项在 M5-C UI 提供（`ServerCertificateValidationCallback`，仅当用户在连接配置勾选"信任自签"时启用）。

### M5-C：NAS 连接配置 UI + 设置持久化

| 文件 | 改动 |
|---|---|
| `Models/AppSettings.cs` | 新增 `NasConnections` 节（`List<NasConnectionSpec>`）+ `ThumbnailCacheLimitMb`（默认 1024）。`[JsonIgnore(Condition = JsonIgnoreCondition.Never)]` 加在 bool 属性上（U3-C 教训：`WhenWritingDefault` 吞 true 默认 bool） |
| `Services/AppSettingsService.cs` | 暴露 `NasConnections` 读写 + `AddNasConnection`/`UpdateNasConnection`/`DeleteNasConnection`/`TestNasConnectionAsync`。密码字段走 DPAPI（复用 `SetCloudApiKey` 模式：`SetNasPassword(id, plaintext)` / `GetNasPassword(id)`） |
| `ViewModels/SettingsViewModel.cs` | 新增 NAS 连接页签 ViewModel：连接列表 + 增删改 + 测试连接按钮 + 密码框 + 自签证书勾选 |
| `Views/SettingsWindow.xaml` | 新增 N 页签「NAS 连接」（M5 编号 N1~N6：N1 列表/N2 名称/N3 协议+地址+端口/N4 用户名+密码/N5 根路径+TLS+自签/N6 测试连接+保存按钮） |
| `docs/UI_NUMBERING.md` | 新增 N 系列编号 |
| `tools/smoke/UiSmokeSettings.ps1` | 扩展 N 页签冒烟步骤 |

**测试**：
- `AppSettingsServiceNasTests`：NasConnections 持久化 round-trip；密码 DPAPI 加密后 config.json 中无明文；删除连接后密码同步清除
- `SettingsViewModelNasTests`：新增/编辑/删除连接；测试连接调用 `INasConnector.TestConnectionAsync`（stub）

### M5-D：远程库扫描与索引（ImportService 抽象化 + schema v6 迁移）

| 文件 | 改动 |
|---|---|
| `Services/ImportService.cs` | 重构：`ScanFiles`/`LoadExisting`/`InsertBatch` 内部的 `Directory.EnumerateFiles`/`File.Exists`/`Path.GetFullPath`/`new FileInfo(path).Length`/`Md5Hasher.ComputeFile(path)` 抽到 `IFileSystemAccessor`。`ImportOptions` 新增 `Source`（Local/Remote）+ `NasConnectionId`。本地路径走 `LocalFileSystemAccessor`（行为不变），远程路径走 `RemoteFileSystemAccessor`（M5-D 引入） |
| `Services/Nas/RemoteFileSystemAccessor.cs` | 基于 `INasConnector`：`EnumerateFiles` 走 `ListFilesAsync` 递归；`OpenRead` 走 `DownloadFileAsync` 流；`GetFileSize` 走 `GetMetadataAsync`。**远程文件 hash 不下载完整原图**：流式 MD5（`MD5.Create()` + `DownloadFileAsync` 返回 `Stream` + `ComputeHashAsync`） |
| `Services/Nas/RemoteLibraryScanner.cs` | 扫描 NAS 根目录编排：递归 PROPFIND → 过滤图片扩展名 → 对每个文件走 `ImportService.ImportAsync(ImportOptions{Source=Remote, NasConnectionId=...})`。进度报告复用 `IProgress<ImportProgress>` |
| `Services/ImportOptions.cs` | 新增 `Source` enum + `NasConnectionId` 字段 |
| `Models/ImageSource.cs` | enum：`Local`/`Remote` |
| `Data/Schema.v6.sql` | `ALTER TABLE images ADD COLUMN source TEXT NOT NULL DEFAULT 'local';` + `ALTER TABLE images ADD COLUMN nas_connection_id INTEGER NULL REFERENCES nas_connections(id) ON DELETE SET NULL;` + `CREATE INDEX idx_images_source ON images(source);` + `CREATE INDEX idx_images_nas ON images(nas_connection_id) WHERE nas_connection_id IS NOT NULL;` + `CREATE TABLE nas_connections (id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE, protocol TEXT NOT NULL, host TEXT NOT NULL, port INTEGER, root_path TEXT, username TEXT, encrypted_password TEXT, use_tls INTEGER NOT NULL DEFAULT 1, trust_self_signed INTEGER NOT NULL DEFAULT 0, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);` |
| `Data/DbMigrator.cs` | `CURRENT_VERSION` 5 → 6 |
| `QL.World.csproj` | 新增 `<EmbeddedResource Include="Data/Schema.v6.sql" />` |
| `Services/DatabaseService.cs` | 启动时迁移（`DbMigrator.Migrate` 已自动跑 v6） |
| `ViewModels/ShellViewModel.cs` | 新增「扫描 NAS 库」命令（M5 编号 N7）：弹连接选择 → 走 `RemoteLibraryScanner` → 进度条 |
| `tests/QL.World.Tests/Data/DbMigratorTests.cs` | v6 迁移测试：images 表有 source/nas_connection_id 列；nas_connections 表存在；DEFAULT 'local' 对旧数据生效 |
| `tests/QL.World.Tests/Services/Import/ImportServiceRemoteTests.cs` | stub `IFileSystemAccessor`（远程模式）：验证远程文件 hash 流式计算；`InsertBatch` 写入 source='remote' + nas_connection_id |
| `tests/QL.World.Tests/Services/Nas/RemoteLibraryScannerTests.cs` | stub `INasConnector`：`ListFilesAsync` 返回多文件 → `RemoteLibraryScanner` 调用 `ImportService` 写入正确条数 |

**风险与对策**：
- 远程文件 hash 流式计算：WebDAV `DownloadFileAsync` 必须返回 `Stream`（不能 `byte[]`，否则大文件 OOM）。`Md5Hasher` 加 `ComputeStreamAsync(Stream)` 重载。
- `LoadExisting` 现在按全表 `file_path` 去重；远程库的 `file_path` 用 WebDAV URL（`https://host:port/path/to/file.jpg`）—— 与本地路径命名空间不冲突，去重逻辑不变。
- `RelocateFile`（M3 起）的 `Md5Hasher.ComputeFile(newPath)` + `new FileInfo(newPath).Length` 仅适用本地；M5 不动 RelocateFile（远程库无"重定位到本地新路径"语义）。
- LegacyPythonDbBuilder 自建 images 表（无 source/nas_connection_id）但 LegacyPythonImporter 不读该表——若未来改 importer 读远程序列则需同步 builder schema（U2-D 教训）。

### M5-E：远程缩略图生成（ThumbnailService 抽象化）

| 文件 | 改动 |
|---|---|
| `Services/ThumbnailService.cs` | `Request(hash, sourcePath)` 重构为 `Request(hash, IImageSource)`，其中 `IImageSource` = `LocalImageSource(path)` / `RemoteImageSource(INasConnector, remotePath)`。`ProcessJob` 内：本地直接 `Image.Load(path)`；远程走"完整下载到临时目录→`Image.Load`→生成 WebP→删除临时文件"（M5 简单路径，M6+ 优化为 Range 部分下载解码） |
| `Services/Nas/IImageSource.cs` | 抽象：`Stream OpenReadAsync(CancellationToken)` + `bool IsLocal` + `string DisplayName` |
| `Services/Nas/LocalImageSource.cs` | 包装本地路径 |
| `Services/Nas/RemoteImageSource.cs` | 包装 `INasConnector` + 远程 path |
| `ViewModels/GalleryViewModel.cs` | 创建 `IImageSource`：本地项用 `LocalImageSource`，远程项（`source='remote'`）用 `RemoteImageSource(connector, file_path)` |
| `Services/ServiceRegistry.cs` | 注入 `ThumbnailService` 时传入当前活跃 `INasConnector`（null 时仅本地模式） |

**测试**：
- `ThumbnailServiceRemoteTests`：stub `INasConnector.DownloadFileAsync` 返回固定字节流 → `Request` 生成 WebP → 临时文件已删除 → `ThumbnailReady` 事件触发
- `RemoteImageSourceTests`：`OpenReadAsync` 返回的 Stream 在 dispose 后不泄漏

**风险与对策**：
- 远程大图下载到临时目录占磁盘：临时文件用 `Path.GetTempFileName()` + try/finally `File.Delete`，异常也清。临时目录在 `%TEMP%\StellarHub\` 下按 hash 命名（避免并发冲突）。
- 临时文件删除失败（被占用）：M5 仅日志警告，不阻塞（缩略图已生成）。M6+ 可加清理任务。

### M5-F：低带宽三级缩略图（thumb / preview / original + LRU 缓存）

| 文件 | 改动 |
|---|---|
| `Services/ThumbnailService.cs` | 缩略图目录布局：`thumbnails/<hash2>/<hash>_thumb.webp`（256px）+ `<hash>_preview.webp`（1024px）。兼容旧 `<hash>.webp`：启动迁移（重命名为 `_thumb`）。`Request` 加 `level` 参数（Thumb/Preview）。原图不缓存到 thumbnails 目录，按需加载 |
| `Services/ThumbnailLevel.cs` | enum：`Thumb`/`Preview` |
| `Models/AppSettings.cs` | `ThumbnailCacheLimitMb`（默认 1024）已 M5-C 加 |
| `Services/ThumbnailCacheService.cs` | LRU 清理：扫描 `thumbnails/` 目录按最后访问时间排序，超过 `ThumbnailCacheLimitMb` 时删最旧（后台线程，启动后 5s 触发，每 1h 巡检） |
| `Services/ServiceRegistry.cs` | 注入 `ThumbnailCacheService` |
| `ViewModels/GalleryViewModel.cs` | 网格视图请求 `Thumb` 级；详情/大图预览请求 `Preview` 级；100% 放大走 `Original`（远程走 `DownloadFileAsync` 流，不缓存） |
| `Views/Controls/ProgressiveImage.xaml` | 渐进式图片控件：`Source` 绑定 `BitmapImage`，`PlaceholderSource`（thumb）→ `PreviewSource` → `FinalSource` 三层淡入（`DoubleAnimation`，每层 200ms 淡入替代前一层） |
| `Services/AppSettingsService.cs` | `ThumbnailCacheLimitMb` 读写 + clamping（128~8192） |

**测试**：
- `ThumbnailServiceLevelTests`：`Request(hash, src, Thumb)` 生成 `_thumb.webp`；`Request(hash, src, Preview)` 生成 `_preview.webp`；两者独立队列
- `ThumbnailCacheServiceTests`：构造 thumbnails 目录超上限 → 清理最旧文件至阈值内；正在使用的文件（`ThumbnailService` 持有句柄）不被删
- 旧 `<hash>.webp` 迁移测试：启动后存在 `<abc>.webp` → 重命名为 `<abc>_thumb.webp`；幂等（已迁移不重复）

**风险与对策**：
- 旧 `<hash>.webp` 迁移：用 `File.Move`（同卷原子），失败回退（不动）。已生成 `_thumb` 的不重复迁移。
- `GalleryViewModel` 当前 `ThumbnailReady` 事件只通知 hash；M5 加 level 参数：`ThumbnailReady(hash, level)`，UI 按级别更新对应层。
- ProgressiveImage 三层淡入避免闪烁：`FinalSource` 加载完成前 `PreviewSource` 保持显示，`FinalSource` 淡入到 1.0 后再隐藏 `PreviewSource`（不要先隐藏再淡入，会闪）。

### M5-G：下载队列（FR-3 下载到本地 + 断点续传 + 失败重试）

| 文件 | 改动 |
|---|---|
| `Data/Schema.v6.sql` | （M5-D 已建 v6，本阶段加表）`CREATE TABLE download_queue (id INTEGER PRIMARY KEY, user_id TEXT, asset_id INTEGER NOT NULL REFERENCES images(id) ON DELETE CASCADE, local_target TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'pending', progress REAL NOT NULL DEFAULT 0, error TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);` + `CREATE INDEX idx_download_queue_status ON download_queue(status);`。**user_id 留 M6 多用户用，M5 恒为当前 `IUserIdentity.CombinedId`** |
| `Services/DownloadQueueService.cs` | 队列管理：`EnqueueAsync(assetId, localTarget)` / `CancelAsync(id)` / `RetryAsync(id)` / `PauseAsync(id)`。后台 worker（2 并发，`SemaphoreSlim`）处理 pending 项。状态机：Pending→Downloading→Completed/Failed/Cancelled。断点续传：检测 `.part` 文件存在 → `Range: bytes={existingLen}-` 续传；服务器返回 200（不支持 Range）→ 从头覆写。失败重试：max 3 次 + 指数退避（1s/2s/4s）。完成删除 `.part` 改名最终文件 |
| `Models/DownloadQueueItem.cs` | record：`Id`/`AssetId`/`FileName`/`LocalTarget`/`Status`/`Progress`/`Error`/`CreatedAt` |
| `ViewModels/DownloadQueueViewModel.cs` | 队列 UI ViewModel：列表绑定 + 进度条 + 取消/重试/打开目录命令 |
| `Views/DownloadQueueWindow.xaml` | 队列窗口（M5 编号 D1~D5：D1 列表/D2 文件名/D3 进度/D4 状态/D5 操作按钮）。从 ShellWindow 工具栏 D 按钮打开 |
| `Views/ShellWindow.xaml` | 工具栏新增 D 按钮「下载队列」（M5 编号 D0） |
| `Services/ServiceRegistry.cs` | 注入 `DownloadQueueService` |
| `ViewModels/GalleryViewModel.cs` | 选中图片 → 右键「下载到本地...」→ 弹目录选择 → `EnqueueAsync` |
| `docs/UI_NUMBERING.md` | 新增 D 系列编号 |
| `tools/smoke/UiSmokeDownloadQueue.ps1` | 新增下载队列冒烟脚本（或扩展主冒烟） |

**测试**：
- `DownloadQueueServiceTests`：stub `INasConnector.DownloadFileAsync`/`DownloadRangeAsync`：①新任务从 0 下载完成；②`.part` 存在则 Range 续传；③服务器返回 200 忽略 Range → 从头覆写（M3-D 教训）；④失败 3 次后状态 Failed；⑤取消 → 状态 Cancelled + `.part` 删除；⑥并发限制 2（构造 5 项，最多 2 个 Downloading）
- `DownloadQueueViewModelTests`：列表绑定；Retry 调用 `RetryAsync`；打开目录调 `SystemNavigator`

**风险与对策**：
- 416 Range Not Satisfiable（`.part` 大于远程文件）：删除 `.part` 从头下载（M3-D 的 416 重试陷阱：必须新建 request）。
- 本地库图片（`source='local'`）也能加入下载队列？语义上是"复制到指定目录"——M5 允许，走 `File.Copy` 而非 `DownloadFileAsync`。`DownloadQueueService` 内部按 `image.source` 分流。
- 应用退出时进行中下载：M5 简单策略——退出时 `CancelAsync` 所有 Downloading 项（状态留 Failed，下次启动可 Retry）。M6+ 可加优雅退出等待。

### M5-H：弱网优先策略 + 预加载 + 渐进式加载（FR-4）

| 文件 | 改动 |
|---|---|
| `Services/NetworkConditionMonitor.cs` | 弱网检测：跟踪最近 10 次 `INasConnector` 请求的失败率 + 平均延迟。失败率 > 50% 或平均延迟 > 3000ms → `IsWeakNetwork = true`。事件 `Changed` 通知 UI |
| `ViewModels/GalleryViewModel.cs` | 弱网下：只显示已缓存缩略图（`ThumbnailExists` 为 true 的项），不发起远程 `Request`。`NetworkConditionMonitor.Changed` 订阅后切换行为 |
| `Services/ThumbnailService.cs` | 预加载：`RequestPrefetch(visibleHashes, prefetchRange)` —— 当前可见项前后各 10~20 张入队（高优先级）。`GalleryViewModel` 滚动时调用 |
| `Views/Controls/ProgressiveImage.xaml` | 渐进式加载逻辑完善：thumb（已缓存）→ 淡入；preview（加载完成）→ 淡入替代 thumb；original（按需）→ 淡入替代 preview。每层 200ms `DoubleAnimation` |
| `Services/ServiceRegistry.cs` | 注入 `NetworkConditionMonitor` |

**测试**：
- `NetworkConditionMonitorTests`：10 次请求 6 次失败 → `IsWeakNetwork = true`；恢复后延迟 < 3000ms 且无失败 → `IsWeakNetwork = false`
- `ThumbnailServicePrefetchTests`：`RequestPrefetch([a,b,c], range=5)` → 队列含 a/b/c 前后各 5 张（已缓存的不入队）

**风险与对策**：
- 弱网检测的"失败率"窗口太小会误判：用滑动窗口 10 次请求；启动初期无数据 → 默认非弱网（避免首次扫描被误判弱网阻塞）。
- 预加载与可见项加载冲突：预加载用低优先级队列（`_normalOrder`），可见项用 `_priorityOrder`（现有机制已支持，`Prioritize` 方法复用）。

### M5-Z：收尾（v1.6.0）

按 ql-world-milestone skill X-Z 清单执行：

1. `src/QL.World/QL.World.csproj` `<Version>` 1.5.0 → 1.6.0；**UTF-8 BOM 必须保留**（U5-Z 教训：PS 5.1 `[xml]` 强转按 GBK 解码 UTF-8 no-BOM 中文 csproj 失败）。
2. `Helpers/AppInfo.cs` `MilestoneLabel = "M5 NAS 远程库"`。
3. `CHANGELOG.md` 顶部新增 `[1.6.0]` 节，按 M5-A~M5-Z 组织条目。
4. `GEMINI.md` 里程碑状态更新：M0–M4 + U1–U5 完成，M5 NAS 远程库已发布（v1.6.0）；记录 WebDAV 连接器 + 远程库索引 + 三级缩略图 + 下载队列 + 弱网优化 + 平台抽象层（为 M6 多用户/macOS 留路）。
5. `docs/UI_NUMBERING.md` 补齐 N（NAS 连接）+ D（下载队列）系列编号。
6. `docs/USER_GUIDE.md` 新增「NAS 连接配置」+「远程库扫描」+「下载队列」章节。
7. `README.md`、`AGENTS.md` 顶部功能描述同步（增加 NAS/WebDAV/下载队列）。
8. 门禁：`powershell -File build.ps1 -Mode Prepare`（0 警告 + 全测试绿 + R2R publish 成功）。**含中文的 PS1/csproj 必须存 UTF-8 BOM**（U5-Z/M4-Z 教训）。
9. staging 体积对比：预期 ≈190.46MB 不变（零新依赖，仅 C# 代码 + schema v6.sql embedded resource 几 KB）。
10. 交付总结（commit 列表 + 门禁结果 + 人工验收项）。
11. 记忆沉淀：仅「编译期不可见、实测发现」的坑（如 WebDAV XML 命名空间解析陷阱、远程 hash 流式计算 OOM、断点续传 416 重试等）。

## 验收标准

### 功能验收

- NAS 连接：在设置 N 页签新增 WebDAV 连接（host/port/用户名/密码/根路径）→ 点「测试连接」→ 显示成功/失败；密码 DPAPI 加密后 `config.json` 无明文。
- 远程库扫描：选 NAS 连接 → 点「扫描 NAS 库」→ 进度条显示扫描进度 → 完成后画廊出现远程图片（`source='remote'` 徽标）。
- 远程缩略图：画廊滚动到远程图片 → 自动下载原图到 `%TEMP%\StellarHub\` → 生成 WebP 缩略图 → 临时文件已删除 → 缩略图显示。
- 三级缩略图：网格视图用 256px thumb；点击单张进详情 → 加载 1024px preview（淡入）；100% 放大 → 加载 original（远程走流式下载）。
- LRU 缓存：构造 thumbnails 目录超过 1024MB → 启动后台清理至阈值内；正在使用的文件不删。
- 下载队列：选中远程图片 → 右键「下载到本地...」→ 选目录 → 加入队列 → 进度条 → 完成提示 + 打开目录；中途中断 → `.part` 文件保留 → 重启后续传；失败 3 次后状态 Failed → 点重试。
- 弱网优先：构造弱网（stub `INasConnector` 6/10 失败）→ 画廊只显示已缓存缩略图，不发远程请求；恢复后正常加载。
- 预加载：滚动到第 100 张 → 前 95~115 张缩略图已入队生成。

### 门禁验收

- `build.ps1 -Mode Prepare` 通过：0 警告 + 全测试绿（预期 v1.5.0 的 316 → v1.6.0 ≥ 360，增量来自 NAS 抽象/WebDAV/扫描/三级缩略图/下载队列/弱网测试）+ R2R publish 成功。
- staging 体积 ≈190.46MB（零新依赖，仅代码 + 嵌入 schema）。
- 旧 `<hash>.webp` 缩略图启动迁移为 `<hash>_thumb.webp`，画廊显示正常。
- dist 五件套命名 `StellarHub_v1.6.0`。

## 人工验收项

- 全新机器：本地库基线（U5 之前）→ 升级到 v1.6.0 → 旧缩略图迁移正常，画廊不空。
- 配置 WebDAV 连接到群晖 NAS：测试连接成功；扫描原图目录生成索引；远程缩略图显示。
- 远程图片下载到本地：断点续传（中途 kill 进程 → 重启续传）；失败重试（断网 → 恢复 → 重试）。
- 弱网模拟（限速 1Mbps + 丢包）：画廊优先显示缓存内容，不卡顿。
- LRU 缓存清理：构造超 1GB 缩略图目录 → 启动后清理至 1GB 内。

## 不在 M5 范围

- **FR-7 多用户管理**（身份识别/角色/权限/数据隔离）—— 留 M6 单独里程碑。M5 仅引入 `IUserIdentity` 抽象，不做权限校验。
- **SMB / SFTP 连接器** —— 后续里程碑加（可能引入 `SSH.NET`/`SMBLibrary`）。M5 仅实现 WebDAV。
- **NAS 端索引服务** —— 文档标为可选；M5 走客户端扫描 + 本地索引路径。
- **远程图片 Range 部分解码**（只下载头部字节解码尺寸）—— M5 走完整下载到临时目录的简单路径；M6+ 优化。
- **专业图片格式（DDS/KTX/ASTC/RAW 等 FR-2 扩展）** —— 不在 M5 范围，留独立里程碑。
- **全屏浏览（FR-5）** —— 不在 M5 范围。
- **国内多厂商 AI（FR-6 扩展）** —— 不在 M5 范围。
- **Setup.exe 安装器** —— U5 已留待后续发布工程里程碑。

## 子阶段 commit 顺序

M5-A → M5-B → M5-C → M5-D → M5-E → M5-F → M5-G → M5-H → M5-Z

每个子阶段独立 commit，中文消息（`git commit -F` 临时文件，不要同命令行 `Remove-Item`——M4-Z 教训）。X-Z 收尾固定任务清单见上。

## 关键风险与对策汇总

| 风险 | 对策 |
|---|---|
| WebDAV XML 命名空间差异（群晖 vs 威联通） | 解析器用 `XNamespace D = "DAV:"` 而非硬编码前缀 |
| `HttpRequestMessage` 不能重发（M3-D 教训） | 416/重试每次新建 request |
| 远程 hash 流式计算 OOM | `DownloadFileAsync` 返回 `Stream`，`Md5Hasher.ComputeStreamAsync` 不读全字节 |
| 临时文件删除失败 | try/finally + 日志，不阻塞缩略图生成 |
| 416 Range Not Satisfiable（.part 大于远程） | 删 .part 从头下载 |
| csproj 含中文必须 UTF-8 BOM（U5-Z 教训） | 收尾时 `[xml]` 强转前确保 BOM |
| PS1 含中文注释必须 BOM（M4-Z 教训） | build.ps1 已 ASCII 英文 + BOM，新增 PS1 冒烟脚本同标准 |
| xUnit 全局禁并行（M2 教训） | SQLite 测试不引入并行假设；`TestAssemblyConfig.cs` 不动 |
| 旧 `<hash>.webp` 迁移冲突 | 用 `File.Move` 同卷原子 + 幂等检查 |
| ProgressiveImage 三层淡入闪烁 | FinalSource 淡入到 1.0 后再隐藏 PreviewSource |
| 弱网检测误判 | 滑动窗口 10 次请求；启动初期默认非弱网 |
