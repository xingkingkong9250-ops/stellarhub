# GEMINI.md — 千星汇图（StellarHub）工程规范真源

本文件是千星汇图（StellarHub，原 QL World）唯一的工程规范来源，与 QianLan.YinWu v1.10.1 的 GEMINI.md 同构。
README/AGENTS/PRD 与本文件冲突时，以本文件为准。

## 1. 项目身份

- 产品名：千星汇图（StellarHub）—— 3D 美术图片素材管理系统
- 技术栈：C# / .NET 10 (`net10.0-windows`) / WPF，纯 MVVM
- 形态：Windows 10 21H2+ / 11 原生桌面应用；**禁止** WebView2、Electron、任何 HTML UI
- 产品基线：`PRD/QL_World_产品需求与重写方案.md`（已评审，M0–M4 基线）
- 里程碑状态：见 `CHANGELOG.md`；当前 **M0–M12 功能完成，版本号 0.13.3（B1-1 二级分类标签改气泡 chips 自动换行）** ——PRD 核心功能 100% 覆盖基线不变
- M12 标签驱动分类体系（2026-10-09，v0.13.0）：10 固定分类 → **6 分类（角色/生物/场景/道具/质感/素材）**，标签命中分类组词表→`TaxonomyService.DeriveCategory` 推导 primary_category（命中数最多→平局 confidence 和最高；纯筛选组标签→NULL）；7 筛选组（美术风格/文件格式/色彩倾向/画面构图/灯光氛围/题材气质/状态形容）不参与推导。关键坑：①跨组标签（「锈蚀」在质感组+状态形容组）TagToGroup 构建时**分类组优先 TryAdd**，TagsByGroup 仍保留全组列表供 UI 两处展示；②schema v10 只增不删——新增 legacy_primary_category 备份+旧 5 分类映射/5 个置 NULL+对有标签行重推导；③CloudAiService breaking：提示词从 TagTaxonomy 动态构建、只输出 tags 不输出 category；LocalAiService 先 DeriveCategory(rank.Tags) 再回退 CLIP rank.PrimaryCategory；④ClipTextIndex 加 promptTemplateVersion 校验（v0.13.2 起常量=**2**，JSON 已用内置 text_model.onnx 经 ClipSpike gen 重生成 174 条目；新增内嵌 JSON↔TagTaxonomy 一致性守卫测试，改词表两边必须同步）；⑤LibraryFilter 7 新字段（5 标签组 EXISTS+IN 组内 OR 组间 AND / FileFormats 走 format 列 / ColorPaletteHexes 经 ColorFamily.HexToFamily 映射色族），**Clone() 必须逐字段同步**；⑥B1-1 树改 HierarchicalDataTemplate 式手搓（ItemsControl 内 ItemsControl + 独立 ▸/▾ 箭头 ToggleButton TwoWay 绑 IsExpanded，箭头 Focusable=False 不抢选中），二级标签复用 Filter.Tag 单标签语义与 B1-3 chips 同步活跃态；⑦B1-6 折叠区默认收起持久化 sidebar_filter_groups_expanded，色板 19 色选 hex 非色族；⑧语义结果集（SemanticResultIds）在 AppendClauses 提前 return，新筛选维度被有意忽略，FindSimilarAsync 无需清理新字段；⑨ClearFilters 须显式重置七个新字段+调 FilterGroups.ResetSelection()。测试 728→774，Release 0 警告 0 错误。**v0.13.1 补齐教训**：服务方法落地≠语义接通——RecomputeCategoryForImage 当时零调用，DetailViewModel 的 AddTag/RemoveTag/BatchAddTags/BatchRemoveTags 四个手动入口全部漏接（批量删除须先删完再逐图重推一次）；旧 Categories.All 在 UI 层有两处残留（DetailViewModel.CategoryOptions、ShellViewModel.CategoryMenuOptions），grep `Categories\.` 全量扫生产代码才能抓净；冒烟脚本硬编码的折叠计数 (12) 也要随节点数改 (8)。测试 774→780。
- v0.5.1 深色主题文字撞色修复（2026-10-03，v0.5.1）：登录窗输入内容不可见的真实根因——`Control.Padding` 可继承，全局 TextBox 自定义模板中 Padding 被 ScrollViewer 的 Margin 与其内部 ScrollContentPresenter（ScrollViewer 默认模板把继承来的 Padding 绑为自己的 Margin）双重应用，Height=28 输入框内容视口仅约 2px，字形被垂直裁成细线；关键坑：模板内对 ScrollViewer 写 `Padding="0"`（无论属性字面量还是 Style setter）都输给模板父级继承，A/B/C/D 隔离探针实证唯一可行形态是**控件自身 Padding 归零、Border 硬编码 Padding="8,4"**（Border.Padding 不会越过 ScrollViewer——模板父级 0 值优先级更高），ScrollViewer 无 Margin；滚动条可见性必须 TemplateBinding 否则多行框 Auto 失效。登录窗删除局部 LoginPasswordBoxStyle 与全局 PasswordBox 样式统一（设置窗 CloudApiKeyBox/NasPasswordBox 裸 PasswordBox 此前回落 Aero2 白底黑点），眼睛 34px 让位用 Padding="0,0,34,0"（仅水平分量）。新增全局深色样式：TabControl（透明底，PART_SelectedContentHost 标准命名）、TabItem（选中 TextPrimary+Accent 2px 下划线，悬停 SurfaceHover）、RadioButton（16px 圆环+8px 选中圆点）。全量 623 测试，0 警告。
- v0.5.0 登录窗口改版（2026-10-02，v0.5.0）：W5 登录窗内容统一为 6 项——W5-1 用户名、W5-2 密码（末端眼睛 ToggleButton：PasswordBox 套与全局 TextBox 同构的本地 Style，与明文 TextBox 互换显隐、双向同步、登录始终读 PasswordBox.Password）、W5-3 通宽登录按钮、W5-4 记住账户（config.json rememberedLoginUsername）、W5-5 记录密码（rememberedLoginPassword 走 DpApiProtector DPAPI CurrentUser，复用云端 API Key 同款加密；勾选级联：记录密码→自动记住账户，取消账户→连带取消密码；仅登录成功落盘，启动 VM ctor 回填标志位+用户名，明文由 View 调 GetRememberedPasswordForPrefill 一次性回填 PasswordBox 不经绑定属性）、W5-6 右上角 30×30 齿轮（未登录身份打开 W3，默认 A1 常规页不再强制跳 A7，关闭后 RefreshNasState）。移除「仅本地模式」（关窗 X/Esc 即本地模式，ShowDialog 非 true 路径不变）与「打开 NAS 设置」按钮；事件/命令改名 LocalModeRequested 删除、NasSettingsRequested/OpenNasSettingsCommand→SettingsRequested/OpenSettingsCommand，App.OpenNasSettings→OpenLoginSettings。新增 AppSettingsService RememberedLoginUsername 属性 + SetRememberedLoginPassword/GetRememberedLoginPassword/HasRememberedLoginPassword。全量 623 测试。
- v0.4.1 AI 标签置信度落库修复（2026-10-02，v0.4.1）：M9 收尾核验发现批量分析把 `AiAnalysisResult.Tags` 合并成字符串列表，导致每个标签自带的 Confidence 未写入 `image_tags.confidence`。TagService 新增逐标签 confidence 写入重载并复用手动标签保护 UPSERT；AiAnalysisService 改为传递每个 AiTag 的 Confidence。全量 614 测试。
- M9 NAS SFTP 协议扩展（2026-09-30，v0.4.0）：多协议路线最后一个缺口——部分绿联 NAS 环境只开放 22 端口或按 Linux 用户体系管理共享。引入 SSH.NET 2026.0.0（纯托管，传递 BouncyCastle 2.7.0/Logging.Abstractions 8.0.3，项目首个传输层第三方库）。M9-A 新增 ISftpSession 窄接口 + RenciSftpSession 封装 SftpClient（ListDirectoryAsync 为 IAsyncEnumerable 需过滤 ./..；OpenRead/RenameFile 同步-only 用 Task.Run 包装；5 参 UploadFileAsync 第 4 参为 IProgress<UploadFileProgressReport> 须位置传参；GetAttributesAsync 返回具体类 SftpFileAttributes 无接口），SftpExceptionMap 集中映射 Renci 异常（SshAuthenticationException/SftpPermissionDeniedException→AuthenticationFailed；SshOperationTimeoutException 与 SocketException TimedOut→Timeout；SftpPathNotFoundException→NotFound；SshConnectionException→NetworkUnreachable；SftpException 派生自 SshException 同 case→ProtocolError）；SftpNasConnector 实现 10 方法（POSIX rename 优先失败降级 download→upload→delete 临时文件 %TEMP%\stellarhub-sftp-move-*；Range 用 Seek 语义；上传 EnsureDirectoryAsync 逐级建父目录）；测试用 FakeSftpSession 本地目录沙盒（remoteRoot→localRoot 映射），无真实服务器。M9-B SftpConnectorFactory：空用户名连接前拒 ProtocolError（不支持匿名/ShareLink），密码明文优先否则 DpApiProtector，热切 .GetAwaiter().GetResult()（SSH.NET 无 SynchronizationContext 捕获不死锁）；HostKeyReceived 事件挂在 SftpClient（BaseClient）而非 ConnectionInfo；TrustSelfSigned 复用为主机密钥开关，拒绝时异常携带 SHA256: 指纹（FingerPrintSHA256 不带前缀）。M9-C 三协议 UI 联动（443/445/22，SFTP 隐藏 TLS 但显示主机密钥信任框，NasTrustLabelText/NasUsernameHintText 动态文案）。全量 613 测试
- M8 多 NAS 聚合与主 NAS 迁移（2026-09-30，v0.3.0）：v0.2.1 单实例假设限制——登录后只能访问主 NAS，副库需重新登录；画廊无主副聚合视图；管理员切换主 NAS 需手动搬数据。M8 打破单实例瓶颈：① NasRole 枚举（Primary/Secondary/ShareLink）替换 IsPrimary 布尔 + SourceNasId 字段持久化 library.json；② NasConnectionRegistry 多实例容器按 spec.Id 缓存 connector+Storage 副本，fingerprint 变化触发重建，主库失败抛异常副库失败仅 Debug 日志；③ AggregatedSharedLibraryService 并发加载主+副 library.json 注入 SourceNasId 主库优先去重；④ PermissionService 副库权限分级（CanWriteToSecondaryNas/CanDownloadFromSecondaryNas/CanMigratePrimary/CanSubmitToSecondaryNas），WebDavConnectorFactory 跳过 Basic-auth for ShareLink，SubmissionService 加 NotPermitted outcome；⑤ PrimaryMigrationService 4 阶段迁移（元数据/文件树/accounts 最后覆盖/Role 切换）幂等复制失败不切 Role 保留旧主，DataTransferService 管理员手动搬运 N 张共享图同 connector 优先 MOVE 跨主机 download→upload→delete 源；⑥ A7 加「设为主 NAS 并迁移数据」+「搬运到此处...」+ 迁移进度文案 + ShowSourceBadge CheckBox 开启后画廊显示 nas://{id} 徽章。全量 553 测试
- v0.2.1 预置管理员 + 主 NAS 标记 + 登录窗口简化（2026-09-30，v0.2.1）：v0.2.0 发布后实测痛点——首次启动初始化管理员繁琐、登录窗口 NAS 下拉对单 NAS 用户多余、W3/A7 无管理员门禁。本轮补丁：① 启动检测主 NAS 可达 + accounts.json 为空 → 自动创建预置管理员 XingKongKong/zzh18254359250 + 用户名预填；② NasConnectionSpec 加 IsPrimary 字段，登录自动取主 NAS（无主取第一个），W3/A7 加「★ 主」徽标 + 「设为主 NAS」按钮；③ 登录窗口移除 NAS 下拉，加「打开 NAS 设置」入口（未登录可开 A7 页签配置），关闭后 RefreshNasState 立即生效；④ W3/A7 加管理员门禁 IsNasManagementEnabled（_currentUser is null || IsAdmin）。AccountService.InitializeAdminAsync 加 username 参数（不再硬编码 admin）。全量 519 测试
- M7 NAS SMB 协议扩展（2026-09-30，v0.2.0）：v0.1.1 已支持 WebDAV，但用户使用绿联成品 NAS（UGOS）——UGOS 默认开 SMB，WebDAV 需额外配置对成品 NAS 用户不友好。M7 在 INasConnector 抽象上加 SMB 协议驱动（仅 SMB；SFTP 推 M8 单独里程碑）。选型与 enum 注释一致——Win32 mpr.dll P/Invoke（WNetAddConnection2 CONNECT_TEMPORARY=4 不写凭据管理器；WNetCancelConnection2 force=true 释放），zero new NuGet，与项目「零依赖」风格统一。M7-A 多协议路由工厂 NasConnectorFactory + UnsupportedProtocolFactory 占位（Create 抛 NotSupportedException 含协议名+目标里程碑）；M7-B SmbNative P/Invoke + SmbNasConnector 实现 INasConnector 10 方法（ResolveUncPath 拼 uncRoot+remotePath，>260 加 \\?\UNC\ 长前缀绕过 MAX_PATH；MoveAsync 跨卷失败降级 download→upload→delete；dispose 强制释放会话）+ SmbConnectorFactory（RootPath 拆 share/subPath；username 为空走集成认证；DPAPI 解密密码与 WebDav 一致）；M7-C UI 联动 NasProtocolOptions=[WebDav,Smb]，派生属性 IsWebDavSelected/IsSmbSelected/IsTlsApplicable/IsTrustSelfSignedApplicable/NasPortHintText/NasRootPathHint/NasTlsLabelText，协议 ComboBox SelectionChanged 转发到 ViewModel（spec POCO 无 INPC 借由 View 事件桥接），切换协议自动填默认端口（443/445，非默认值保留），UseTls/TrustSelfSigned Visibility 按协议显隐；M7-D INasConnectorContractTests 8 个 [Fact]→9 个 [Theory]+[MemberData]，Stub+Smb-localTemp 共跑，保留 SmbNasConnectorContractTests 14 项边界行为测试。全量 512 测试
- U6 M6 UI 补全（2026-09-30，v0.1.1）：补丁版本，接通 M6 服务层遗留的 UI。U6-A 共享库画廊视图（SharedPaths 统一 NAS 路径：thumbnails/originals 按 hash 分片 + pending 容器；SharedGalleryViewModel 复用本地画廊视图/缩放设置，支持内存过滤；B1-SCOPE 本地/共享分段切换）；U6-B 本地图片右键一键「提交到共享库」（上传原图+WebP 缩略图+pending JSON，hash 查重）；U6-C 审核队列窗口 W6（INasConnector.MoveAsync：RFC4918 MOVE 优先、下载→上传→删除兜底；ApproveAsync 归位文件并写 SourceNasPath；管理员工具栏入口+pending 角标）；U6-D 共享只读详情（SharedDetailViewModel + 保存副本到本地/复制 NAS 路径；共享作用域搜索走内存过滤不查本地库）。登录窗口编号修正为 W5，用户管理页签修正为 A8。全量 482 测试
- M6 多用户管理（2026-09-30）：NAS 共享目录多用户体系。账号表存 NAS 共享目录（accounts.json，PBKDF2 哈希），所有用户共享同一份；登录窗口（LoginWindow，U6 编号修正为 W5）→ 认证成功 → ShellWindow；管理员用户管理页签（SettingsWindow，U6 编号修正为 A8，新建/启用禁用/重置密码/删除）；NAS 共享库（library.json，SharedLibraryService 去重读写）；数据提交审核流程（SubmissionService：pending/ → 审核通过提升到共享库 → 删除 pending）；权限校验（PermissionService：管理员专属用户管理/审核，普通用户查看共享库/提交审核）。不新建服务端，通过 INasConnector（WebDAV）读写 NAS JSON。INasConnector 继承 IDisposable。ServiceRegistry 注入 CurrentUser/SharedStorage/Accounts/SharedLibrary/Submissions/Permissions
- M5 NAS 远程库（2026-09-30）：WebDAV 协议接入群晖/威联通等 NAS；零新增第三方依赖（仅 BCL HttpClient + System.Xml）。平台抽象层（INasConnector / IFileSystemAccessor / IUserIdentity）+ WebDAV 连接器（XNamespace D="DAV:" 兼容群晖/威联通，ServerCertificateCustomValidationCallback 适配自签证书）+ 远程库扫描（schema v6 source/nas_connection_id）+ IImageSource 抽象（本地/远程同源）+ 三级缩略图（Thumb 256px / Preview 1024px / Original 按需）+ LRU 缓存上限（ThumbnailCacheService 后台 Timer 清理）+ 下载队列（schema v7，2 并发 worker，.part 断点续传 + 416 重启 + 3 次指数退避重试）+ 弱网检测（NetworkConditionMonitor 10 样本滑动窗口，失败率 >50% 或延迟 >3000ms 跳过远程请求）+ 邻近图片预加载（RequestPrefetch 低优先级批量入队）+ ProgressiveImage 三层 Image 渐显控件（PlaceholderLayer→PreviewLayer→FinalLayer，200ms QuadraticEase 淡入，上层完成才折叠下层避免闪烁）。为 M6 多用户/macOS/SMB/SFTP 留路
- U5 品牌升级（2026-09-29）：窗口标题/Logo/关于页=千星汇图；exe=StellarHub.exe；程序集 Product/Copyright/Description 改为千星汇图/StellarHub；五件套命名 QL World_v{ver}→StellarHub_v{ver}；数据目录 %APPDATA%\Qianlan_App\QL_World\→StellarHub\（原子迁移+.migrated 标记+失败 fallback 旧路径）。内部 mutex/event 名与 db 文件名 qlworld.db 保留以减少升级期副作用

## 2. 解决方案布局

```
QL.World.sln
nuget.config                      # 只允许 nuget.org
src/QL.World/                     # 主程序（WinExe）
  Models/                         # 纯数据/契约，无 UI 类型
  Data/                           # SQLite、Schema、迁移（M1 起）
  Services/                       # 业务逻辑，不引用任何 UI 类型
    Ai/                           # AI 抽象与实现（M3 起）
  Helpers/                        # MVVM 基元、纯工具
  Interop/                        # 所有 P/Invoke（DPAPI、Shell 等）隔离在此
  ViewModels/                     # 每个窗口/分区一个 ViewModel
  Views/                          # Window 与分区 UserControl
  Themes/                         # Dark.xaml / Light.xaml / Generic.xaml
  Assets/
tests/QL.World.Tests/             # xUnit，命名 {Subject}Tests.cs
tests/release/                    # 发布契约测试（M4）
tools/release/                    # 发布 PowerShell 逻辑（仅本地执行）
docs/                             # UI_NUMBERING.md、USER_GUIDE.md
docs/superpowers/{specs,plans,reports}/
PRD/                              # 产品需求基线
run.bat / run-hidden.vbs / run.ps1
build.bat / build.ps1
```

## 3. 命名与代码约定

- 命名空间一律 `QL.World.{Folder}`（如 `QL.World.Services`）
- 类/方法/属性 PascalCase；私有字段 `_camelCase`；常量 UPPER_SNAKE_CASE
- 文件编码 UTF-8（无 BOM）；启用 `Nullable` 与 `ImplicitUsings`
- MVVM：View 只绑定，不写业务逻辑；ViewModels 继承 `Helpers.ObservableObject`，
  命令使用 `RelayCommand` / `AsyncRelayCommand`
- **不引入**第三方 MVVM 框架或 DI 容器；服务聚合使用手写 `ServiceRegistry`（构造注入）
- Services 不引用 `System.Windows.*`；图像/系统交互通过自有结构或 Interop 封装
- 所有 P/Invoke 放在 `Interop/`，不得散落在业务代码中
- SQL 一律参数化，禁止字符串拼接；长事务按批（导入 50 条/批）

## 4. 数据与状态

- 数据根目录：`%APPDATA%\Qianlan_App\StellarHub\`（U5 由 `QL_World\` 迁移；首次启动原子迁移 + `.migrated`/`.migration-failed` 标记 + 失败 fallback 旧路径）
  - `qlworld.db`（SQLite，单长连接，WAL，`PRAGMA user_version` 顺序迁移；M5 升至 v7：v6 加 source/nas_connection_id，v7 加 download_queue 表）
  - `thumbnails\<hash前2位>\<hash>_thumb.webp` / `<hash>_preview.webp`（M5-F 多级；旧 `<hash>.webp` 启动迁移为 `_thumb.webp`）
  - `models\`（M3 本地 AI 模型）
  - `config.json` + `config.json.backup`（含 NasConnections 列表，密码 DPAPI 加密）
  - `logs\error.log`（1MB 自动截断保留后半）
- 小文本状态（配置/导出元数据）必须经 `AtomicTextFile`：
  同目录临时文件 → 原子替换 → 保留上一完整副本为 `.backup`；
  主文件损坏时只允许从可解析的备份恢复，禁止静默覆盖唯一副本
- 标签来源 `ai`/`manual`：manual 行不得被 AI 的 UPSERT 降级覆盖
- 向量 BLOB 自描述（model_id + dim + 小端 float），禁止 pickle 等 Python 序列化

## 5. Debug 隔离环境变量（仅 DEBUG 编译生效）

| 变量 | 作用 |
|---|---|
| `QL_WORLD_DATA_ROOT` | 重定向数据根目录（测试/开发隔离） |
| `QL_WORLD_UI_TEST_MODE` | `1`/`true` 时使用独立单实例互斥体与关机事件 |
| `QL_WORLD_BUILD_OUTPUT` | run.ps1 的构建输出目录覆盖 |

Release 构建必须完全忽略这些变量。

## 6. 启动（run 三件套）

`run.bat` → `run-hidden.vbs`（无控制台）→ `run.ps1`：

1. 检查 .NET SDK
2. 若有已启动实例：置位命名事件请求**优雅退出**，等待最多 8 秒；失败则停止，不强杀
3. `dotnet build -c Debug`
4. 启动窗口程序；启动日志 `%TEMP%\QL_World.run.log`；PID 文件 `%TEMP%\QL_World.dev.pid`

单实例互斥体：`Global\QL_World_SingleInstance`（UI 测试态 `Local\QL_World_UiTest_SingleInstance`）；
关机事件：`Local\QL_World_GracefulShutdown`（UI 测试态带 `UiTest`）。

## 7. 构建与发布

- 版本号唯一来源：`src/QL.World/QL.World.csproj` 的 `<Version>`，格式 major.minor.patch；
  脚本与 UI 均从程序集/csproj 读取，**禁止在任何脚本中硬编码版本号**
- 日常构建：`build.bat`（= `build.ps1 -Mode Prepare`）：
  Release 编译（**0 警告**）→ `dotnet test` 全绿 → Self-Contained win-x64 + ReadyToRun 发布到 `artifacts/staging/`
- 正式发布（M4 落地）：两阶段 Prepare/Finalize；
  Finalize 只读 Git 不做推送，产出五件套到 `dist/`，历史版本永不覆盖：
  1. `StellarHub_v{x.y.z}.zip`（自包含应用包）
  2. `StellarHub_v{x.y.z}_source.zip`
  3. `StellarHub_v{x.y.z}_更新公告.txt`
  4. `StellarHub_v{x.y.z}_使用说明.txt`
  5. `StellarHub_v{x.y.z}_manifest.json`（最后原子落地；annotated Tag message 含
     `release-manifest-sha256: <64hex>`）
- 目标机免装运行时；不自动推远端，不自动打 Tag 除非显式要求

## 8. 测试门禁

- `dotnet build -c Release`：0 错误 **0 警告**
- `dotnet test`：全绿；xUnit，断言用 xUnit 原生 Assert
- 核心 Service 必须有 `{Service}Tests.cs`，不依赖网络/显卡/真实模型：
  AI 网络与模型调用经接口假对象测试
- 破坏性场景（断电/损坏/断网/鉴权失败）在里程碑验收时实测并记录

## 9. UI 编号体系

- W = 窗口；A = 顶部横区/页签；B = 主分区；C = 底部区；编号可继续下钻到控件（B3-3）
- 编号是位置锚点：改功能必须核对编号；删除即作废永不复用；新增顺延
- 唯一真源：`docs/UI_NUMBERING.md`，每次 UI 变更同步

## 10. 文档四件套

| 文件 | 读者 | 职责 |
|---|---|---|
| GEMINI.md | 工程/AI | 规范真源（本文件） |
| AGENTS.md | AI 代理 | 开工指令入口，指向本文件 |
| README.md | 人类 | 项目介绍与快速开始 |
| CHANGELOG.md | 人类 | 版本变更记录 |

变更记录：设计/计划/验证报告放 `docs/superpowers/{specs,plans,reports}/`。
