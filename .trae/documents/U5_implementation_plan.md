# U5 实施计划 — 品牌升级补丁（QL World → 千星汇图 / StellarHub）

## 背景与范围

- 对齐《软件开发文档》工程落地规范：窗口标题=千星汇图、桌面/图标简称=星汇、安装包=StellarHub_Setup.exe、内部标识/版本/版权=StellarHub。
- U5 是纯品牌升级补丁，不引入新功能依赖，不动业务逻辑；为 M5 NAS 远程库扫清品牌基线。
- 当前基线 v1.4.0（U4 已发布，PRD 核心功能 100%）。U5 目标版本 v1.5.0。

## 已确认决策（AskUserQuestion 2026-09-29）

1. 品牌改为 千星汇图 / StellarHub（窗口标题/安装包/内部标识/版权统一）。
2. 跨平台：先做 Windows 跑通核心，M5+ 预留平台抽象口子（`INasConnector`/文件系统/用户识别抽象层），便于后续 macOS/Linux 及移动端。U5 本身不引入抽象层，仅在品牌字段留好英文标识 StellarHub。
3. 启动顺序：先 U5 品牌补丁，再 M5 NAS。
4. 数据目录：自动迁移 `%APPDATA%\Qianlan_App\QL_World\` → `StellarHub\`，原子移动 + `.migrated` 标记防重复 + 失败 fallback 读旧路径。

## 改动文件清单（按子阶段）

### U5-A：显示层与程序集品牌

| 文件 | 改动 |
|---|---|
| `src/QL.World/Helpers/AppInfo.cs` | `ProductName` "QL World" → "千星汇图"；新增 `EnglishName = "StellarHub"`；`MilestoneLabel` "U4 发布基线" → "U5 品牌升级" |
| `src/QL.World/QL.World.csproj` | `<AssemblyName>` "QL World" → "StellarHub"（exe 产物 `StellarHub.exe`）；新增 `<Product>千星汇图</Product>`、`<AssemblyTitle>千星汇图 (StellarHub)</AssemblyTitle>`、`<Copyright>Copyright © 2026 StellarHub. 保留所有权利.</Copyright>`、`<Description>面向 3D 美术与概念设计师的全品类图像参考管理工具</Description>`；**`<RootNamespace>QL.World` 保持不变**（所有 C# 命名空间不受影响） |
| `src/QL.World/ViewModels/ShellViewModel.cs` | `Title` 属性改用 `AppInfo.ProductName` 拼接（如 `千星汇图`），不再硬编码 "QL World" |
| `src/QL.World/Views/ShellWindow.xaml` | A1 Logo 文字块（L270 `Text="QL World"`）→ "千星汇图"；Title 已绑 `{Binding Title}` 自动跟随 |
| `src/QL.World/Views/SettingsWindow.xaml` | L12 `Title="设置 — QL World"` → `设置 — 千星汇图`；L189 About 页 `Text="QL World"` → "千星汇图"（或绑 AppInfo.ProductName） |
| `src/QL.World/ViewModels/SettingsViewModel.cs` | 若 About 页产品名硬编码则改读 `AppInfo.ProductName`/`EnglishName` |

**风险与对策**：
- `<AssemblyName>` 改动会使 exe 名 `QL World.exe` → `StellarHub.exe`。`run.ps1` 从 `*.runtimeconfig.json` 推导 exe 名，自动适配无需改；`run.bat` 调 `run.ps1` 间接受益。需冒烟验证。
- `RootNamespace` 不变 → 所有 `QL.World.*` 命名空间、XAML `xmlns` 引用、InternalsVisibleTo 不受影响。
- 测试程序集 `QL.World.Tests` 名不变（独立 csproj，未改 AssemblyName）。

### U5-B：发布脚本与安装包命名

| 文件 | 改动 |
|---|---|
| `build.ps1` | L30 注释 + L36 `artifacts\staging\QL World_v$Version` → `StellarHub_v$Version`；Write-Host 标签 `[QLWorld]` 可保留（脚本内部日志标签，非用户可见品牌） |
| `tools/ReleaseTool/ReleaseArtifacts.cs` | L108/L132/L139 `QL World_v{version}` 目录与 zip 命名 → `StellarHub_v{version}`；manifest 内 product/name 字段若含 "QL World" 同步改 "StellarHub" |
| `tools/ReleaseTool/ReleaseTool.csproj` | 若有 `<Product>` 等元数据同步 |
| `tools/release/ReleaseContract.psm1` | 若契约常量含 "QL World" 字样同步；契约测试预期值同步 |
| `tests/QL.World.Tests/Release/ReleaseContractTests.cs` | 断言里 `QL World_v` 字样 → `StellarHub_v`；五件套命名断言同步 |
| `docs/release/RELEASE_NOTES.md` | 模板产品名占位同步 |

**安装包命名**：当前 dist 产物为 `app.zip`（无 Setup.exe 安装器）。文档要求 `StellarHub_Setup.exe`——U5 阶段先统一 zip/目录命名为 `StellarHub_v{version}`；真正的 `Setup.exe` 安装器（如需 Inno Setup/WiX）留待后续发布工程里程碑，U5 不引入安装器工具链。

### U5-C：数据目录迁移

`src/QL.World/Services/AppDataRootResolver.cs` 改造：

```
ProductFolder = "QL_World"  →  "StellarHub"
LegacyProductFolder = "QL_World"  // 旧路径回退
MigrationMarkerFileName = ".migrated"  // 防重复迁移
```

迁移逻辑（`ResolveDefault` 首次解析时）：
1. 计算新路径 `appData\Qianlan_App\StellarHub\`，确保存在。
2. 若新路径下存在 `.migrated` 标记 → 已迁移，返回新路径。
3. 若无标记，检测旧路径 `appData\Qianlan_App\QL_World\` 存在且非空：
   - 逐项原子移动（`qlworld.db` / `config.json` + `.backup` / `thumbnails/` / `models/` / `logs/`），用 `Directory.Move`/`File.Move`（同卷原子）。
   - 全部成功 → 写 `.migrated` 标记文件 → 返回新路径。
   - 任一失败（IO/权限/跨卷）→ 回滚已移动项（尽力），写 `.migration-failed` 日志，**fallback 返回旧路径**，不阻断启动。
4. 旧路径不存在 → 全新安装，直接用新路径。

**测试**（xUnit，新增 `AppDataRootResolverTests`）：
- 全新安装：旧路径不存在 → 用新路径，不产生 `.migrated`。
- 首次迁移：旧路径有 db/thumbnails → 移动到新路径 + `.migrated` + 旧路径清空 → 返回新路径。
- 幂等：`.migrated` 已存在 → 不重复移动，返回新路径。
- 失败 fallback：模拟移动抛异常 → 返回旧路径，写失败日志，启动不崩。
- `QL_WORLD_DATA_ROOT` 环境变量仍优先（DEBUG 路径不变，测试隔离不受影响）。

**注意**：`DatabaseFileName = "qlworld.db"` 文件名是否改？建议**不改**（db 文件名是内部资源，用户不可见；改名徒增迁移复杂度）。`config.json` 同理不改。

### U5-Z：收尾（v1.5.0）

1. `QL.World.csproj` `<Version>` 1.4.0 → 1.5.0；`AppInfo.MilestoneLabel = "U5 品牌升级"`。
2. `CHANGELOG.md` 顶部新增 `[1.5.0]` 节，按 U5-A/B/C 组织。
3. `GEMINI.md` 里程碑状态更新（注明品牌升级为千星汇图/StellarHub，PRD 核心功能 100% 基线不变）。
4. `docs/UI_NUMBERING.md` 全表核对（品牌相关行：窗口标题/Logo/关于页表述）。
5. `docs/USER_GUIDE.md` 品牌引用同步（产品名千星汇图）。
6. `README.md`、`AGENTS.md` 顶部产品名同步。
7. 门禁：`powershell -File build.ps1 -Mode Prepare`（0 警告 + 测试全绿 + R2R publish 成功）。
8. staging 体积对比（预期 ≈190.46MB 不变，无新依赖）。
9. 交付总结（commit 列表 + 门禁结果 + 人工验收项）；记忆沉淀（仅「编译不可见、实测发现」）。

## 验收标准

- 启动应用：窗口标题显示「千星汇图」；A1 Logo 与设置关于页显示「千星汇图」。
- exe 产物名为 `StellarHub.exe`；`run.bat` 静默启动正常。
- 数据目录：全新安装落在 `%APPDATA%\Qianlan_App\StellarHub\`；旧 `QL_World\` 目录存在时自动迁移并写 `.migrated`；迁移失败不崩、回退旧路径。
- `build.ps1 -Mode Prepare` 门禁通过；测试净增 ≥5（迁移逻辑）；staging 体积对比记录。
- dist 产物目录与 zip 命名为 `StellarHub_v1.5.0`。

## 人工验收项

- 全新机器无旧路径：启动正常，数据落新路径。
- 有旧库机器：启动后库内容（图片/标签/收藏/AI embedding）完整可见。
- run.bat 启动 exe 名正确。
- 设置关于页版本号显示 v1.5.0 · U5 品牌升级。

## 不在 U5 范围

- 不引入安装器工具链（Setup.exe 留后续）。
- 不改 db 文件名 `qlworld.db` / `config.json`。
- 不引入平台抽象层（M5+ 再做）。
- 不改 PRD 原始文档（历史文档不动）。
