# Changelog

本项目遵循语义化版本（major.minor.patch）。里程碑：M0 骨架 → M1 核心素材管理 →
M2 组织与可靠性 → M3 AI 双轨 → M4 专业化与发布（v1.0.0）。

> **版本重编码说明（2026-09-30）**：自 0.0.1 起启用新版本编码规则。
> 历史 0.1.0–1.6.0 条目保留为开发记录，不再作为版本序列参考。
> 新规则：小修改 +0.0.1；新增功能/里程碑 +0.1.0；大版本 1.0.0 需用户确认。

## [Unreleased]

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
