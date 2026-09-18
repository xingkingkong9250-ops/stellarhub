# M2 实现计划 — 组织与可靠性

## 背景

M1 核心素材管理已完成（数据层/导入/缩略图/查询/标签/VM/UI 全通，40 测试绿，0 警告门禁过，UIA E2E 全流程验证）。M2 聚焦"组织与可靠性"：收藏夹、文件监控、设置窗口、消息中心、浅色主题、旧版 Python 导入器、缺失文件重新定位。

## 子里程碑拆分与执行顺序

```
M2-A (Light.xaml + Background 键补齐)          ──┐
M2-B (CollectionService + Models)                 ──┤── 无依赖，可并行
M2-D (NotificationService)                        ──┘
       │
M2-C (FileWatcherService + AppSettings watcher)   [依赖 M2-D]
       │
M2-E (B1-2 收藏夹树 + 拖拽 + B3-2 重新定位)       [依赖 M2-B]
M2-F (A1-6 铃铛 + 消息面板)                       [依赖 M2-D]
       │
M2-G (W3 设置窗口 + 旧版导入器)                   [依赖 M2-B/C/D/E/F]
```

顺序：M2-A → M2-B → M2-D → M2-C → (M2-E ‖ M2-F) → M2-G。每个子里程碑完成后跑 `dotnet build -c Release` 0 警告 + 全测试绿。

---

## M2-A — Light.xaml + Background 键补齐

**目标**：补 Dark.xaml 缺失的 `Background`/`BackgroundBrush` 键（ShellWindow.xaml L133 引用但 Dark.xaml 未定义，只有未加载的 ModernDark.xaml 有），创建 Light.xaml。

**新建文件**：
- `src/QL.World/Themes/Light.xaml` — 与 Dark.xaml 相同的 17 个 Color + 17 个 Brush 键，浅色配色

**修改文件**：
- `src/QL.World/Themes/Dark.xaml` — 添加 `<Color x:Key="Background">#0D0E12</Color>` + `<SolidColorBrush x:Key="BackgroundBrush" .../>`

**测试**：
- `tests/QL.World.Tests/ThemeResourcesTests.cs` — 加载两个 ResourceDictionary，断言 17 个 brush 键全部存在

---

## M2-B — CollectionService + Models

**目标**：收藏夹 CRUD 服务（DB 表已存在于 Schema.v1.sql）。

**新建文件**：
- `src/QL.World/Models/CollectionModels.cs` — `Collection`、`CollectionCount`、`CollectionImage` records
- `src/QL.World/Services/CollectionService.cs` — 仿 TagService 模式（`DatabaseService _db` 构造），方法：Create/Rename/UpdateDescription/Delete/SetCover/AddImages/RemoveImage/Reorder/GetAll/GetImageIds/GetImageCount/GetCoverHash

**修改文件**：
- `src/QL.World/Services/ServiceRegistry.cs` — 添加 `CollectionService` 属性（第 8 参）
- `src/QL.World/App.xaml.cs` — 构造 CollectionService 传入 registry
- `tests/QL.World.Tests/ShellViewModelTests.cs` — 更新 ServiceRegistry 构造

**测试**：
- `tests/QL.World.Tests/CollectionServiceTests.cs` — CRUD 幂等/级联/排序

---

## M2-D — NotificationService

**目标**：会话级消息中心（内存，不持久化）。

**新建文件**：
- `src/QL.World/Models/NotificationModels.cs` — `NotificationKind` 枚举 + `NotificationEntry` 类
- `src/QL.World/Services/NotificationService.cs` — `ObservableCollection<NotificationEntry> Items`，Publish/PublishBatch/MarkRead/MarkAllRead/Clear/ClearByKind，UnreadCount 属性

**修改文件**：
- `src/QL.World/Services/ServiceRegistry.cs` — 添加第 9 参
- `src/QL.World/App.xaml.cs` — 构造并传入
- `tests/QL.World.Tests/ShellViewModelTests.cs` — 更新构造

**测试**：
- `tests/QL.World.Tests/NotificationServiceTests.cs` — 发布/已读/清空/批量聚合/按类型清除

---

## M2-C — FileWatcherService + AppSettings watcher 字段

**目标**：监控目录，2s 防抖，新增→自动导入，删除→is_missing 标记+通知。

**新建文件**：
- `src/QL.World/Services/FileWatcherService.cs` — `IDisposable`。内部 `IFileWatcher` 接口包装 `FileSystemWatcher` 便于测试。`System.Threading.Timer` 2s 防抖。Start/Stop/UpdateDirectories。依赖 NotificationService（M2-D）。
- `src/QL.World/Services/IFileWatcher.cs` — 测试用的抽象接口

**修改文件**：
- `src/QL.World/Models/AppSettings.cs` — 添加 `List<string> WatcherDirectories` + `bool AutoImportOnWatch`
- `src/QL.World/Services/AppSettingsService.cs` — 添加对应属性 + 规范化
- `src/QL.World/Services/ServiceRegistry.cs` — 添加第 10 参
- `src/QL.World/App.xaml.cs` — 构造/启动/Dispose
- `tests/QL.World.Tests/ShellViewModelTests.cs` — 更新构造

**测试**：
- `tests/QL.World.Tests/FileWatcherServiceTests.cs` — 用 stub IFileWatcher 注入事件，验证防抖/导入/缺失标记/通知/目录热插拔

---

## M2-E — B1-2 收藏夹树 + 拖拽 + B3-2 重新定位

**目标**：左栏收藏夹树 UI，画廊拖拽到收藏夹，缺失文件重新定位。

**新建文件**：
- `src/QL.World/ViewModels/CollectionTreeViewModel.cs` — 仿 TagTreeViewModel 模式，`ObservableCollection<CollectionNodeViewModel>`
- `src/QL.World/ViewModels/CollectionNodeViewModel.cs` — Id/DisplayName/Count/CoverHash/SelectCommand
- `src/QL.World/Views/InputDialog.xaml` + `.cs` — 通用输入对话框（用于新建/重命名收藏夹）

**修改文件**：
- `src/QL.World/ViewModels/ShellViewModel.cs` — 添加 CollectionTree 属性 + 收藏夹命令（Create/Rename/Delete/SetCover/AddSelectedToCollection）+ Gallery.Refreshed 刷新树
- `src/QL.World/ViewModels/DetailViewModel.cs` — 添加 `RelocateFileCommand`（OpenFileDialog 选新路径→调用 ImageQueryService.RelocateFile）
- `src/QL.World/Services/ImageQueryService.cs` — 添加 `RelocateFile(id, newPath)` 方法（重算 hash/size，更新路径，清 is_missing，hash 冲突返回 false）
- `src/QL.World/Views/ShellWindow.xaml` — B1 区域插入收藏夹树段；B3-2 缺失警告改为可点击"重新定位"按钮
- `src/QL.World/Views/ShellWindow.xaml.cs` — OnCollectionDrop 拖放处理

**测试**：
- `tests/QL.World.Tests/CollectionTreeViewModelTests.cs` — 刷新/选中/取消
- `tests/QL.World.Tests/ImageQueryServiceTests.cs` 补充 — RelocateFile 成功/hash 冲突

---

## M2-F — A1-6 铃铛 + 消息面板

**目标**：顶栏铃铛显示未读红点，点击弹出消息列表面板。

**新建文件**：
- `src/QL.World/ViewModels/NotificationListViewModel.cs` — 包装 NotificationService，IsPopupOpen/HasUnreadBadge/MarkAllReadCommand/ClearAllCommand

**修改文件**：
- `src/QL.World/ViewModels/TopBarViewModel.cs` — 添加 NotificationService 依赖（第 3 参），启用 NotificationsCommand，添加 NotificationListViewModel
- `src/QL.World/ViewModels/ShellViewModel.cs` — 更新 TopBar 构造
- `src/QL.World/Views/ShellWindow.xaml` — 铃铛按钮包 Grid + 未读红点 Ellipse + Popup 消息列表

**测试**：
- `tests/QL.World.Tests/NotificationListViewModelTests.cs` — 弹窗切换/已读/清空/红点

---

## M2-G — W3 设置窗口 + 旧版 Python 导入器

**目标**：设置窗口 A1/A2/A4/A5 页签，Python 旧库导入器。

**新建文件**：
- `src/QL.World/Services/LegacyPythonImporter.cs` — 读 Python SQLite（images/image_tags/collections/collection_images），映射列差异（补 sortable_name/updated_at/is_missing），走正常去重路径，跳过 embeddings/dominant_colors
- `src/QL.World/Models/LegacyImportModels.cs` — Report/Progress records
- `src/QL.World/ViewModels/SettingsViewModel.cs` — A1 主题/完整性/库位置，A2 缩略图边长/质量/重建/清空，A4 监控目录列表/添加/移除/自动导入，A5 版本/统计/旧版导入/诊断日志导出
- `src/QL.World/Views/SettingsWindow.xaml` + `.cs` — TabControl 480×640，A3 占位显示"M3 提供"

**修改文件**：
- `src/QL.World/Services/ServiceRegistry.cs` — 添加第 11 参 LegacyPythonImporter
- `src/QL.World/App.xaml.cs` — 构造 LegacyPythonImporter，订阅 ShowSettingsDialog + ThemeChanged 事件
- `src/QL.World/ViewModels/ShellViewModel.cs` — 添加 ShowSettingsDialog + ThemeChanged 事件，启用 TopBar.SettingsCommand
- `src/QL.World/ViewModels/TopBarViewModel.cs` — SettingsCommand 路由到 SettingsRequested 事件
- `tests/QL.World.Tests/ShellViewModelTests.cs` — 最终 11 参 ServiceRegistry

**测试**：
- `tests/QL.World.Tests/LegacyPythonImporterTests.cs` — 用 Support/LegacyPythonDbBuilder 建临时 Python DB，验证导入/去重/取消
- `tests/QL.World.Tests/SettingsViewModelTests.cs` — 主题切换事件/监控目录增删/统计计数

---

## 跨里程碑变更

### ServiceRegistry 演化（7→11 参）

```
AppSettingsService, DatabaseService, ImageInspector, ImportService,
ThumbnailService, ImageQueryService, TagService,
CollectionService (M2-B), NotificationService (M2-D),
FileWatcherService (M2-C), LegacyPythonImporter (M2-G)
```

### 收尾

- `docs/UI_NUMBERING.md` — W1/A1-6、W1/A1-7、W1/B1-2、W1/B3-2、W3/A1、W3/A2、W3/A4、W3/A5 标"已实现（M2）"
- `CHANGELOG.md` — 添加 M2 条目
- 记忆文件 — 记录 M2 关键经验

## 验证

1. 每个子里程碑：`dotnet build -c Release` 0 警告 0 错误 + `dotnet test` 全绿
2. M2-G 完成后：UIA 冒烟测试（导入→建收藏夹→拖拽→切主题→重新定位缺失文件→打开设置改监控目录→旧版导入）
3. 最终 `build.ps1 -Mode Prepare` 全量门禁
