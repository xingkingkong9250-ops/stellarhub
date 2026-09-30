# M6 实施计划 — 多用户管理（账号系统 + NAS 共享库 + 提交审核）

> 当前基线 v0.0.1（版本重编码基线，功能含 M0–M5 全部）。M6 目标版本 v0.1.0（新增功能里程碑，+0.1.0）。

## 1. 范围与目标

M6 在 M5 NAS 远程库基础上引入**应用内账号系统**与 **NAS 共享图库**，使团队成员可以：
- 用管理员分发的账号登录（非 OS 账号）
- 浏览 NAS 共享图库（所有用户可见）
- 把本地图片提交到共享库，管理员审核后入库
- 管理员对共享库有最高权限（增删改 + 审核）

### 与 M5 远程库的区别

| 维度 | M5 远程库（已有） | M6 共享库（新增） |
|---|---|---|
| 数据来源 | NAS 上的图片文件目录 | 经审核入库的共享图库 |
| 索引位置 | 本地 SQLite（source='remote'） | NAS 上的 JSON 元数据 |
| 权限 | 任何配置了 NAS 连接的用户 | 需登录账号，按角色分权 |
| 内容控制 | 用户自行扫描 | 管理员审核后入库 |

### 核心目标

1. **账号系统**：管理员在应用内创建/禁用/重置账号，账号存在 NAS 共享目录
2. **登录**：启动时登录窗口（选 NAS 连接 + 输入账号密码）
3. **NAS 共享库**：独立于本地库的共享图库，客户端加载到内存浏览
4. **提交审核**：用户提交本地图片 → pending → 管理员审核 → 入库
5. **权限**：管理员对共享库完全控制；普通用户只读 + 可提交；本地库所有用户自我管理

## 2. 已确认决策（2026-09-30 AskUserQuestion）

1. **NAS 即服务器**：账号、共享库元数据、pending 提交均存在 NAS 共享目录，通过 M5 的 `INasConnector`（WebDAV）读写。**不新建服务端程序**。
2. **账号存储在 NAS 共享目录**：所有用户共享同一份账号表。
3. **同应用内管理**：管理员账号登录后，设置窗口显示用户管理页签。
4. **数据目录保持按 OS 用户隔离**：`%APPDATA%\Qianlan_App\StellarHub\` 不变，本地库仍按 OS 用户隔离。
5. **共享库不用 SQLite**：WebDAV 不支持文件锁与随机写入，共享库元数据用 JSON 文件存储（整体读写），避免 SQLite 网络锁问题。

## 3. NAS 共享目录结构

通过 NAS 连接的 `RootPath` 下创建 `StellarHubShared/` 目录：

```
{nas_root}/StellarHubShared/
├── accounts.json                 # 账号表（密码 PBKDF2 哈希，不存明文）
├── library.json                  # 共享图库元数据索引（所有已审核图片）
├── thumbnails/                   # 共享图库缩略图
│   └── <hash前2位>/<hash>_thumb.webp
├── pending/                      # 待审核提交
│   └── <submit_id>.json          # 每个提交的元数据 + 缩略图引用
└── collections.json              # 共享收藏夹（M6 可选，默认不做）
```

### 数据格式

**accounts.json**：
```json
{
  "version": 1,
  "accounts": [
    {
      "username": "admin",
      "password_hash": "base64(pbkdf2)",
      "salt": "base64(salt)",
      "role": "admin",
      "status": "active",
      "created_at": "2026-09-30T...",
      "created_by": "system"
    }
  ]
}
```

**library.json**（共享图库索引）：
```json
{
  "version": 1,
  "images": [
    {
      "id": "sha256-hash",
      "file_name": "brick_01.png",
      "file_hash": "sha256...",
      "file_size": 1234567,
      "width": 2048,
      "height": 2048,
      "format": "png",
      "rating": 0,
      "primary_category": "贴图/纹理",
      "dominant_colors": ["#A0522D", "#8B4513"],
      "tags": ["砖墙", "红色"],
      "source_nas_path": "/photos/brick_01.png",
      "submitted_by": "alice",
      "approved_at": "2026-09-30T...",
      "approved_by": "admin"
    }
  ]
}
```

**pending/<submit_id>.json**：
```json
{
  "submit_id": "uuid",
  "file_name": "brick_01.png",
  "file_hash": "sha256...",
  "file_size": 1234567,
  "width": 2048,
  "height": 2048,
  "format": "png",
  "primary_category": "贴图/纹理",
  "tags": ["砖墙"],
  "submitted_by": "alice",
  "submitted_at": "2026-09-30T...",
  "thumbnail_nas_path": "pending/<submit_id>_thumb.webp",
  "status": "pending"
}
```

## 4. 子阶段划分

### M6-A：账号系统 + NAS 共享存储服务

**目标**：实现账号模型与 NAS 上的账号存储读写，不涉及 UI。

**改动**：
- `Models/UserAccount.cs`：账号 record（Username/PasswordHash/Salt/Role/Status/CreatedAt/CreatedBy）
- `Models/UserRole.cs`：枚举 Admin / User
- `Services/Nas/SharedStorageService.cs`：通过 `INasConnector` 读写 NAS 共享目录的 JSON 文件（accounts.json / library.json / pending/*.json）
  - `EnsureSharedStructureAsync`：创建目录结构
  - `LoadAccountsAsync` / `SaveAccountsAsync`
  - `LoadLibraryAsync` / `SaveLibraryAsync`
  - `LoadPendingAsync` / `SavePendingAsync` / `DeletePendingAsync`
- `Services/AccountService.cs`：账号业务逻辑
  - `CreateAccountAsync(username, password, role)`：PBKDF2 哈希（10000 轮），唯一性校验
  - `AuthenticateAsync(username, password) → UserAccount | null`
  - `DisableAccountAsync` / `EnableAccountAsync`
  - `ResetPasswordAsync`
  - `DeleteAccountAsync`（不能删自己）
  - `ListAccountsAsync`
- `Helpers/PasswordHasher.cs`：PBKDF2（Rfc2898DeriveBytes），salt 16 字节，hash 32 字节
- 测试：`AccountServiceTests.cs`（创建/认证/禁用/重置/删除/重复用户名）、`PasswordHasherTests.cs`、`SharedStorageServiceTests.cs`（stub INasConnector）

**风险**：
- NAS 上 accounts.json 并发写入：管理员同时操作可能冲突。M6 采用"读取-修改-写回"简单策略，冲突时后写覆盖前写（少量用户可接受）。M6+ 可加乐观锁（version 字段）。
- 首个管理员账号：NAS 上无 accounts.json 时，需要一个"初始化管理员"流程。M6-A 提供 `InitializeAdminAsync(defaultPassword)`，首次使用时由用户在登录窗口触发。

### M6-B：登录界面 + 启动流程

**目标**：启动时弹出登录窗口，验证后进入主界面。

**改动**：
- `Views/LoginWindow.xaml(.cs)`（W4）：NAS 连接下拉 + 账号 + 密码 + 登录按钮
  - NAS 连接列表来自 `AppSettings.NasConnections`
  - 登录失败显示错误（账号不存在/密码错误/账号禁用）
  - "初始化管理员"入口（accounts.json 不存在时显示）
- `ViewModels/LoginViewModel.cs`：登录逻辑
- `Models/CurrentUser.cs`：当前登录用户上下文（Username/Role/NasConnectionId）
- `App.xaml.cs`：启动流程改为 `LoginWindow` → 成功 → `ShellWindow`
- `ServiceRegistry`：注入 `IUserIdentity`、`AccountService`、`SharedStorageService`
- 测试：`LoginViewModelTests.cs`

**风险**：
- 登录窗口与单实例互斥体的顺序：先登录再获取互斥体？还是先获取？M6 先获取互斥体再登录（保证单实例）。
- 离线场景：NAS 不可达时无法登录。M6 提供"离线模式"（仅使用本地库，不登录共享库）。登录窗口增加"仅本地模式"按钮。

### M6-C：管理员用户管理界面

**目标**：管理员在设置窗口管理账号。

**改动**：
- `SettingsWindow.xaml` 新增页签 A7「用户管理」（仅管理员可见）
- `ViewModels/UserManagementViewModel.cs`：
  - 账号列表（用户名/角色/状态/创建时间）
  - 创建账号命令（用户名 + 密码 + 角色）
  - 禁用/启用命令
  - 重置密码命令
  - 删除命令（不能删自己，不能删最后一个管理员）
- 测试：`UserManagementViewModelTests.cs`

### M6-D：NAS 共享库基础

**目标**：加载共享库元数据，在图库中显示共享库视图。

**改动**：
- `Services/SharedLibraryService.cs`：
  - `LoadAsync(INasConnector)`：从 NAS 下载 library.json → 内存列表
  - `GetImages()`：返回共享图片列表
  - `GetThumbnailAsync(hash)`：从 NAS 下载缩略图到本地临时目录
- `Models/SharedImageRecord.cs`：共享图片模型（与本地 ImageRecord 字段对齐）
- `GalleryViewModel`：新增"共享库"模式（与"全部/收藏夹"并列）
- `LibraryFilter`：新增 `LibraryScope` 枚举（Local / Shared）
- `ShellWindow.xaml` B1：新增"共享库"入口
- 测试：`SharedLibraryServiceTests.cs`

### M6-E：数据提交审核流程

**目标**：用户提交本地图片到共享库，管理员审核后入库。

**改动**：
- `Services/SubmissionService.cs`：
  - `SubmitAsync(imageId)`：把本地图片元数据 + 缩略图上传到 NAS pending 区
  - `GetPendingAsync()`：列出所有 pending 提交（管理员）
  - `ApproveAsync(submitId)`：从 pending 移入 library.json，删除 pending 文件
  - `RejectAsync(submitId)`：删除 pending 文件
- `GalleryItemViewModel`：新增"提交到共享库"命令（本地库图片可见）
- `ViewModels/ReviewQueueViewModel.cs` + `Views/ReviewQueueWindow.xaml`（W5）：管理员审核队列
- `ShellViewModel`：管理员可见"审核队列"入口
- 测试：`SubmissionServiceTests.cs`

### M6-F：权限校验集成

**目标**：按角色控制操作权限。

**改动**：
- `Services/PermissionService.cs`：
  - `CanManageUsers(role)`：仅 admin
  - `CanApproveSubmissions(role)`：仅 admin
  - `CanEditSharedLibrary(role)`：仅 admin
  - `CanSubmitToShared(role)`：admin + user
  - `CanViewSharedLibrary(role)`：admin + user
- UI 层按权限显隐：用户管理页签、审核队列入口、共享库编辑操作
- 共享库图片对普通用户只读（不能删标签/改分类）
- 测试：`PermissionServiceTests.cs`

### M6-Z：收尾

- `QL.World.csproj` `<Version>` 0.0.1 → 0.1.0（+0.1.0，新增功能里程碑）
- `AppInfo.MilestoneLabel` = "M6 多用户管理"
- `CHANGELOG.md` 新增 [0.1.0] 节
- `GEMINI.md` 里程碑状态更新
- `docs/UI_NUMBERING.md` 补齐 W4/W5/A7 编号
- `docs/USER_GUIDE.md` 新增账号/登录/共享库/审核章节
- 门禁：build.ps1 -Mode Prepare（0 警告 + 全测试绿 + R2R）

## 5. 验收标准

1. 管理员可创建账号，普通用户可登录
2. 账号密码 PBKDF2 哈希存储，NAS 上无明文
3. 共享库图片所有登录用户可见
4. 普通用户可提交图片，管理员可审核通过/拒绝
5. 管理员可删除/禁用账号
6. 权限控制正确（普通用户看不到用户管理页签）
7. NAS 不可达时可进入"仅本地模式"
8. 全测试绿，0 警告

## 6. 不在 M6 范围

- **服务端程序**：不新建独立服务端，NAS 即服务器
- **NAS 上的 SQLite**：共享库用 JSON 不用 SQLite（WebDAV 锁限制）
- **增量同步**：共享库每次全量加载 JSON（万级以下可接受）
- **共享收藏夹**：collections.json 预留但不实现
- **并发冲突解决**：简单后写覆盖，不加乐观锁
- **账号密码找回**：仅管理员重置密码，无自助找回
