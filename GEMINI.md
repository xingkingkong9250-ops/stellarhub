# GEMINI.md — 千星汇图（StellarHub）工程规范真源

本文件是千星汇图（StellarHub，原 QL World）唯一的工程规范来源，与 QianLan.YinWu v1.10.1 的 GEMINI.md 同构。
README/AGENTS/PRD 与本文件冲突时，以本文件为准。

## 1. 项目身份

- 产品名：千星汇图（StellarHub）—— 3D 美术图片素材管理系统
- 技术栈：C# / .NET 10 (`net10.0-windows`) / WPF，纯 MVVM
- 形态：Windows 10 21H2+ / 11 原生桌面应用；**禁止** WebView2、Electron、任何 HTML UI
- 产品基线：`PRD/QL_World_产品需求与重写方案.md`（已评审，M0–M4 基线）
- 里程碑状态：见 `CHANGELOG.md`；当前 **M0–M6 功能完成并经 U6 补全，版本号 0.1.1** ——PRD 核心功能 100% 覆盖基线不变
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
