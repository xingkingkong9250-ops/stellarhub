# AGENTS.md — 协作指令入口

在本仓库开始任何工作前：

1. **先读 `GEMINI.md`**。它是工程规范真源；本文件只是入口。
2. 检查 `.agents/skills/` 与 `.agents/workflows/`（当前不存在；若后续添加则优先遵循）。
3. 产品基线在 `PRD/QL_World_产品需求与重写方案.md`；UI 编号真源在 `docs/UI_NUMBERING.md`。

## 执行规则

- 启动应用一律使用仓库自带 `run.bat`（静默构建 + 单实例优雅重启），不要直接 `dotnet run`
- 构建/发布一律使用 `build.bat` / `build.ps1`，版本号只能来自 csproj
- 里程碑门禁：Release 0 警告 0 错误 + `dotnet test` 全绿，缺一不可
- 架构红线：纯 MVVM；Services 不引用 UI；P/Invoke 只进 Interop/；
  不引入第三方 MVVM/DI 框架；不引入 WebView2/Electron/HTML UI
- 数据红线：状态文件原子写 + `.backup`；不得把数据写到程序安装目录
- `dist/` 保留全部历史版本，永不覆盖；不自动 `git push`、不自动打 Tag
- 阶段性设计/计划/验证报告放 `docs/superpowers/` 对应子目录
