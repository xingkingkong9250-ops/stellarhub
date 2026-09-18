# QL World

3D 美术图片素材管理系统（参考图 / 贴图 / 概念图）。原生 Windows 桌面应用，
本地数据库，默认完全离线；AI 自动打标与语义搜图为用户可选能力（M3）。

- 技术栈：C# / .NET 10 / WPF（纯 MVVM，无第三方 MVVM 框架）
- 工程规范：[`GEMINI.md`](./GEMINI.md)
- 产品基线：[`PRD/QL_World_产品需求与重写方案.md`](./PRD/QL_World_产品需求与重写方案.md)
- 变更记录：[`CHANGELOG.md`](./CHANGELOG.md)

> 当前阶段：**M0 工程骨架**（可启动的 W1 空壳 + 基础设施 + 测试门禁）。
> 核心素材管理在 M1 交付。旧 Python 版保留在 `..\QL_World\` 作为只读参照。

## 开发环境

- Windows 10 21H2+ / 11
- .NET 10 SDK
- 仅使用 nuget.org 包源（见 `nuget.config`）

## 常用命令

```powershell
.\run.bat                                   # 静默构建并启动（推荐）
dotnet build QL.World.sln -c Release        # Release 编译（门禁：0 警告）
dotnet test QL.World.sln -c Release         # 全部测试
.\build.bat                                 # Prepare：编译 + 测试 + 自包含发布到 artifacts/staging/
```

## 数据位置

`%APPDATA%\Qianlan_App\QL_World\`：`qlworld.db`、`thumbnails\`、`models\`、
`config.json`(+ `.backup`)、`logs\`。

DEBUG 构建可用 `QL_WORLD_DATA_ROOT` 重定向数据根目录进行隔离测试。
