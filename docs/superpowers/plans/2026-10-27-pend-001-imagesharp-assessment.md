# PEND-001 评估：ImageSharp 3.x → 4.x 与 Six Labors Split License

> 日期：2026-10-27 · 结论：**暂缓升级**，先取得免费 Community 许可 key 再排期。
> 当前依赖：`SixLabors.ImageSharp 3.1.12`（`src/QL.World/QL.World.csproj`）。

## 1. 现状与风险

- 3.1.x 分支已停止安全维护；仓库已在 csproj 中用 `NuGetAuditSuppress`
  压掉 5 条通告（修复版本均为 4.1.2）。
- 2026-10-08 的逐条 PoC 复核结论仍然有效：本仓库不使用
  `HistogramEqualization` / `TiffEncoder` / `TiffCompression` / `HalfVector4` /
  ICC 颜色转换 / `IccProfile.Entries`；TIFF 仅单向解码，输出统一 WebP。
  3 条 High 内存破坏路径不可达；仅恶意 BigTIFF 存在单线程解码 DoS，
  桌面本地导入场景影响有限。
- 结论：**当前不升级也不会立即引入可实际利用的风险**，但长期停留在
  无维护分支不可接受。

## 2. 许可结论（关键阻塞点）

- ImageSharp 采用 **Six Labors Split License 1.0**。以下情形可免费使用：
  开源/源码可见项目、非营利组织/注册慈善机构、以及年总营收低于
  **100 万美元**的营利实体。
- 从 **4.0.0** 起，直接引用 ImageSharp 的项目在**构建时**必须提供有效
  许可（`sixlabors.lic` 或 `SixLaborsLicenseKey`），缺失或过期会直接
  编译失败；传递依赖不受影响。
- 免费档需要到 https://licensing.sixlabors.com/ 申请 Community 许可；
  官方示例 key 已于 **2026-09-04 过期**，不能用于长期构建。
- 本项目为闭源桌面软件。若发行主体年营收 ≥ 100 万美元，则需购买商业
  订阅；若低于该阈值（或作为个人/非营利项目），可免费申请 Community key。
  **发行主体的营收口径需由项目所有者确认**，这是升级前唯一无法由代码
  侧闭环的事项。

## 3. 技术影响

- 4.x 基线为 .NET 8+；本项目已是 `net10.0-windows`，运行时无阻碍。
- 4.0/4.1 在颜色转换、变换、抗锯齿、卷积、元数据、像素访问等区域有
  API 清理；本项目在 15 个文件中直接使用 ImageSharp（缩略图、WebP
  编解码、Magick 桥接、感知哈希、CLIP 预处理、颜色分析、共享提交）。
  预计以编译错误驱动迁移为主，不涉及架构调整。
- 4.1.2 同时修复上述 5 条通告，升级后可删除全部 `NuGetAuditSuppress`。

## 4. 决策

**暂缓升级，保持 3.1.12 + 现有风险论证。** 原因：升级在缺少有效许可
key 时无法通过 Release 构建，属于硬阻塞；而在现有 PoC 论证下，3.1.12
的可利用面为零。

## 5. 恢复升级的前置条件与步骤

1. 确认发行主体年营收口径；低于 100 万美元则申请免费 Community 许可，
   否则评估商业订阅成本。
2. 将 `sixlabors.lic` 放在仓库外（或通过环境变量
   `SIXLABORS_LICENSE_KEY`），**不得提交到仓库**；在 `build.ps1` 中以
   `-p:SixLaborsLicenseKey=...` 传入。
3. 升级 `SixLabors.ImageSharp` 至 4.1.2，逐项修复编译错误。
4. 删除 csproj 中 5 条 `NuGetAuditSuppress`，确认 `dotnet build -c Release`
   无警告。
5. 回归重点：缩略图生成/WebP 质量、TIFF/PSD/TGA 解码回退、感知哈希
   稳定性、CLIP 预处理、颜色提取、共享库提交缩略图。
6. 全量测试 + 真机冒烟通过后，随下一个功能版本提交（不单独升版本号）。
