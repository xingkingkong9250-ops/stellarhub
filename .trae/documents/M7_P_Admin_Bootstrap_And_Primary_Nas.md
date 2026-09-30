# StellarHub 补丁 v0.2.1：预置管理员 + 主 NAS 标记 + 登录窗口简化

## Context（为什么做这个改动）

M7 已发布 v0.2.0（SMB 协议扩展），但用户实际启动测试时遇到两个体验痛点：
1. 首次启动要手动点「初始化管理员」按钮且默认用户名是 admin/密码 admin123，繁琐且易忘
2. 登录窗口让用户从 NAS 下拉中选连接，但实际多数用户只有一个 NAS，多此一举；且普通用户改 NAS 配置入口（W3/A7）无门禁，存在误改风险

本轮目标：预置管理员账号 XingKongKong/zzh18254359250 便于启动测试；登录窗口移除 NAS 下拉改为系统自动取主 NAS；W3/A7 NAS 配置页签加管理员门禁（未登录时可见以便首次配置）。

**版本号**：用户指令「补丁更新版本号」=NO.P → +0.0.1 → v0.2.0 → **v0.2.1**。注意：本轮实际改动级别接近 NO.M（功能调整），但按用户显式指令代号走 NO.P。

## 改动方案

### 1. 数据层：NasConnectionSpec 加 IsPrimary 字段

- [NasConnectionSpec.cs](file:///c:/AI_Programming/APP/StellarHub/src/QL.World/Services/Nas/NasConnectionSpec.cs) 加 `public bool IsPrimary { get; set; } = false;`，注释说明「管理员标记的主 NAS，登录时自动取此连接；为 false 时登录回退取列表第一个」
- JSON 序列化默认值 false 兼容旧 config.json（M3-C 教训：bool 默认 true 必须加 `[JsonIgnore(Condition = JsonIgnoreCondition.Never)]`，但本字段默认 false 不受影响）
- AppSettingsService 的 AddNasConnection/UpdateNasConnection 不需改（IsPrimary 跟随 spec 一起序列化）

### 2. AccountService.InitializeAdminAsync 改造

- [AccountService.cs:26-46](file:///c:/AI_Programming/APP/StellarHub/src/QL.World/Services/AccountService.cs) `InitializeAdminAsync(string defaultPassword, CancellationToken ct)` → `InitializeAdminAsync(string username, string defaultPassword, CancellationToken ct)`
- 内部硬编码 `Username = "admin"` → `Username = username`
- 注释更新

### 3. 启动流程：自动初始化管理员

- [App.xaml.cs:393-443](file:///c:/AI_Programming/APP/StellarHub/src/QL.World/App.xaml.cs) `ShowLoginGate` 改造：
  - 找主 NAS：`var primarySpec = settings.NasConnections.FirstOrDefault(c => c.IsPrimary) ?? settings.NasConnections.FirstOrDefault()`
  - 若 primarySpec 为 null → 显示登录窗口（按钮禁用 + 提示「请管理员点『打开 NAS 设置』配置 NAS」）
  - 若 primarySpec 非空 → 尝试连 NAS + 读 accounts.json
    - accounts.json 为空 → 调 `AccountService.InitializeAdminAsync(username: "XingKongKong", password: "zzh18254359250")` 自动创建
    - 显示登录窗口，LoginViewModel.Username 预填 "XingKongKong"
  - catch 连接异常 → 显示登录窗口 + 错误提示「无法连接主 NAS：{ex.Message}，请管理员检查配置」

### 4. 登录窗口简化

- [LoginViewModel.cs:25-45](file:///c:/AI_Programming/APP/StellarHub/src/QL.World/ViewModels/LoginViewModel.cs)：
  - 移除 `NasConnections` / `SelectedConnection` 字段（不再绑 NAS 下拉）
  - 加 `OpenNasSettingsCommand` 命令（触发事件 `NasSettingsRequested`，由 App.xaml.cs 打开 SettingsWindow 的 A7 页签）
  - `LoginAsync(password)` 改用主 NAS（逻辑同 App.xaml.cs 的 primarySpec 查找）
  - `InitializeAdminAsync` 删除（不再需要——启动时自动初始化）
- [LoginWindow.xaml:40-48](file:///c:/AI_Programming/APP/StellarHub/src/QL.World/Views/LoginWindow.xaml)：
  - 移除 NAS 连接 TextBlock + ComboBox（Grid.Row=2）
  - Grid 重排行号（Row 2 改为用户名、Row 3 改为密码、Row 4 错误、Row 5 按钮）
  - 按钮区加「打开 NAS 设置」按钮（Style=SubtleButtonStyle，Command=OpenNasSettingsCommand）
- [LoginWindow.xaml.cs](file:///c:/AI_Programming/APP/StellarHub/src/QL.World/Views/LoginWindow.xaml.cs)：加 OnOpenNasSettingsClick 转发到 ViewModel 命令
- App.xaml.cs 加 `OpenNasSettings` 方法：构造 SettingsViewModel（accounts=null, currentUser=null，因为未登录）+ 打开 SettingsWindow + 选 A7 页签

### 5. W3/A7 NAS 页签管理员门禁 + 主 NAS 标记

- [SettingsViewModel.cs](file:///c:/AI_Programming/APP/StellarHub/src/QL.World/ViewModels/SettingsViewModel.cs)：
  - 加 `public bool IsNasManagementEnabled => _currentUser is null || _currentUser.IsAdmin;`（未登录可见以便首次配置；普通用户隐藏）
  - 加 `SetPrimaryCommand`（RelayCommand）：把选中 NAS 的 IsPrimary 设 true，其他设 false，调 `_settings.UpdateNasConnection` 持久化
  - `IsUserManagementEnabled` 不动（仅管理员）
- [SettingsWindow.xaml:289](file:///c:/AI_Programming/APP/StellarHub/src/QL.World/Views/SettingsWindow.xaml)：
  - A7 TabItem 加 `Visibility="{Binding IsNasManagementEnabled, Converter={StaticResource BoolVis}}"`
  - L303 过时注释 "M5 仅 WebDAV；SMB/SFTP 预留" → "WebDav + Smb；Sftp 留待 M8"
  - NAS 列表 ListBox 的 ItemTemplate 加「主」徽标（IsPrimary=true 时显示 ★ 或文本「主」）
  - 详情区 N6 操作行加「设为主 NAS」按钮（Command=SetPrimaryCommand，选中项 IsPrimary=true 时禁用）

### 6. 文档与版本号同步

- [docs/UI_NUMBERING.md](file:///c:/AI_Programming/APP/StellarHub/docs/UI_NUMBERING.md)：
  - W5 登录窗口条目：移除 NAS 下拉 + 加「打开 NAS 设置」按钮；首次启动自动初始化 XingKongKong
  - W3/A7-N1 条目：加「主 NAS 标记 + 管理员门禁（未登录可见 / 普通用户隐藏）」
  - W3/A7 状态更新为「M5 WebDav + M7 Smb + v0.2.1 主 NAS 标记」
- [CHANGELOG.md](file:///c:/AI_Programming/APP/StellarHub/CHANGELOG.md)：在 [Unreleased] 后加 `## [0.2.1] — 2026-09-30 预置管理员 + 主 NAS 标记 + 登录窗口简化`
- [GEMINI.md](file:///c:/AI_Programming/APP/StellarHub/GEMINI.md)：里程碑状态补一句 v0.2.1 + 简述本轮改动
- [QL.World.csproj:11](file:///c:/AI_Programming/APP/StellarHub/src/QL.World/QL.World.csproj)：`<Version>0.2.0</Version>` → `<Version>0.2.1</Version>`（UTF-8 BOM）
- [AppInfo.cs:22](file:///c:/AI_Programming/APP/StellarHub/src/QL.World/Helpers/AppInfo.cs)：MilestoneLabel 可保持「M7 NAS SMB 协议扩展」（补丁不改里程碑标签）

### 7. 测试

- `tests/QL.World.Tests/Services/AccountServiceTests.cs`：`InitializeAdminAsync` 改造后测试用例同步（接受 username 参数）
- `tests/QL.World.Tests/Services/NasConnectionSpecTests.cs`（若不存在则新建）：IsPrimary 字段默认 false + JSON round-trip
- `tests/QL.World.Tests/ViewModels/SettingsViewModelTests.cs`（若存在）：加 `IsNasManagementEnabled` 三态测试（currentUser=null true / User false / Admin true）
- 测试总数预期 512 → ~518（+6 左右）

### 8. 门禁与发布

- `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Mode Prepare`：Release 零警告零错误 + 全测试绿 + win-x64 R2R staging
- 工作树干净后 `powershell.exe -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Mode Finalize`：产 dist 五件套
- 不自动 push、不自动打 tag（按既定规则）

## 复用现有资产

- `IsUserManagementEnabled` 模式（SettingsViewModel.cs:807）— `IsNasManagementEnabled` 同形复刻
- `BoolToVisibilityConverter`（WPF 内置 + SettingsWindow.xaml 已注册 BoolVis）
- `AccountService.InitializeAdminAsync` 现有逻辑（改 username 参数即可，密码哈希 + accounts.json 写入逻辑不动）
- `AppSettingsService.UpdateNasConnection`（IsPrimary 跟随 spec 序列化，不需新方法）
- `NasConnectorFactory` + `SmbConnectorFactory` + `WebDavConnectorFactory`（不动，登录时仍走 nasFactory.Create）

## 风险与缓解

| 风险 | 缓解 |
|---|---|
| 首次启动无 NAS 时登录按钮卡死 | 加「打开 NAS 设置」按钮 + 错误提示，用户配好 NAS 重启即可 |
| 自动初始化管理员失败（NAS 不可达） | catch 异常 + 显示错误提示，登录窗口仍可用（用户检查 NAS 配置） |
| 旧 config.json 无 IsPrimary 字段 | JSON 默认 false 兼容；登录回退取第一个 |
| 普通用户登录后改 NAS | IsNasManagementEnabled=false 隐藏 A7 页签 |
| 未登录打开 SettingsWindow 缺服务 | SettingsViewModel 构造已支持 accounts=null / currentUser=null；A8 用户管理页签自动隐藏（IsUserManagementEnabled=false） |

## 验证

1. 首次启动（config.json 无 NAS）：登录窗口显示「打开 NAS 设置」按钮 + 登录按钮禁用
2. 点「打开 NAS 设置」→ SettingsWindow A7 页签可见，配好 NAS + 标记主 NAS + 保存 → 关闭
3. 重启：自动连接主 NAS + 自动创建 XingKongKong 管理员 + 登录窗口用户名预填 XingKongKong
4. 输密码 zzh18254359250 → 登录成功 → 进入 Shell
5. 普通用户登录后打开设置：A7 页签隐藏、A8 用户管理页签隐藏
6. `dotnet test` 全绿；`build.ps1 -Mode Prepare/Finalize` 通过

## 单 commit 还是拆分

此轮虽是补丁（NO.P）但改动跨 8 文件 + 测试。建议**单 commit**（按补丁语义一次性提交），commit message 中文，工作流：
- 全部改动 + 测试 → dotnet build -c Release → dotnet test → Prepare → commit → Finalize → 用户手动 tag v0.2.1
