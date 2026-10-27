# PEND-005 评估：跨平台（macOS / Linux / 移动端）可行性

> 日期：2026-10-27 · 结论：**1.0.0 前不做跨平台 UI**；先完成「跨平台核心层」
> 低风险抽取，Avalonia 重写 UI 作为 1.0 后的独立里程碑再评估。

## 1. 现状盘点

- 目标框架：`net10.0-windows` + WPF，UI 为 XAML 窗口/样式/虚拟化面板。
- 已具备的抽象口子：
  - `INasConnector` / `INasConnectorFactory`：WebDAV、SFTP、SMB 统一协议层；
  - `IFileSystemAccessor`：本地与远程（`LocalFileSystemAccessor` /
    `RemoteFileSystemAccessor`）导入/缩略图共用；
  - `IUserIdentity`：当前 Windows 实现，接口已为多平台预留；
  - Services/Data/Models 基本不引用 UI，构造注入集中在 `ServiceRegistry`。
- 跨平台无阻碍的依赖：Microsoft.Data.Sqlite、ImageSharp、
  Magick.NET（有 Linux/macOS 包）、ONNX Runtime（多 RID）、SSH.NET、
  WebDAV（纯 HTTP）。

## 2. 平台强绑定清单

| 依赖 | 位置 | 影响 |
|---|---|---|
| WPF / XAML / DWM 标题栏 | 全部 `Views/`、`App.xaml.cs` | UI 需整体替换（Avalonia/其他） |
| `crypt32.dll` DPAPI | `Interop/DpApiProtector.cs` | 密钥存储需 macOS Keychain / Linux libsecret |
| `mpr.dll` WNet* | `Services/Nas/Smb/SmbNative.cs` | SMB 驱动仅 Windows；其他平台需 SMBLibrary/libsmbclient |
| `CreateHardLinkW` | `Services/LibraryStorageService.cs` | NTFS 硬链接分类层；APFS/ext4 可行但语义/权限不同 |
| 对话框/文件选择/剪贴板 | `OpenFolderDialog`、`MessageBox`、`Clipboard`、`SystemNavigator` | 需抽成对话框服务，VM 不得直接调 WPF |
| 同盘硬链接校验 | `LibraryRootResolver.ValidateSameVolume` | 盘符概念仅 Windows；需改为卷/设备 ID |
| 打包脚本 | `build.ps1` / `run.bat` | 需按 RID 增加 macOS/Linux 产物与签名/公证 |

## 3. 方案比较

| 方案 | 优点 | 缺点 | 结论 |
|---|---|---|---|
| **Avalonia UI** | C#/XAML 心智接近 WPF，MVVM 可复用，MIT 许可，Windows/macOS/Linux 桌面成熟 | XAML 样式与控件模板需重写；虚拟化面板、主题资源需适配 | **推荐**（1.0 后） |
| .NET MAUI | 官方栈 | 桌面仅 Windows/macOS，Linux 缺官方支持；XAML 差异大 | 不推荐 |
| Uno Platform | 一套 XAML 多端（含 WebAssembly） | 复杂度/包体大，桌面体验与 WPF 差距需验证 | 备选 |
| Web/Electron | 生态丰富 | 违反 GEMINI「禁止 WebView2/Electron/HTML UI」红线 | **排除** |
| 仅 Windows + 远端服务 | 成本最低 | 不满足跨平台诉求 | 保底 |

## 4. 建议路线（分阶段，不排具体工期）

1. **核心层抽取（1.0 前可做，低风险）**
   - 新建 `QL.World.Core` 类库（Services/Data/Models/Helpers），目标
     `net10.0`，不引用 WPF；主程序引用它。
   - 把 `MessageBox` / `OpenFolderDialog` 等 VM 内 UI 调用替换为
     `IDialogService` / `IFilePickerService`，Windows 实现留在主程序。
   - 增加 `IUserIdentity`、`ISecretProtector`、`IHardlinkService` 的平台
     实现注册点；Windows 行为保持不变。
   - 验收：现有 843 项测试全部通过；主程序行为零变化。
2. **平台适配层（1.0 后）**
   - `ISecretProtector`：DPAPI → macOS Keychain / Linux libsecret。
   - `IHardlinkService`：Windows `CreateHardLinkW`；macOS/Linux 用
     `link(2)`；不支持时退化为复制并记录磁盘占用提示。
   - SMB：Windows 保留 `mpr.dll`，其他平台新增 SMBLibrary 驱动。
   - 文件监听、长路径、大小写敏感、卷 ID 校验逐项适配。
3. **Avalonia 外壳（独立里程碑）**
   - 复用 ViewModels/服务；重写窗口、样式、虚拟化画廊与深/浅主题。
   - 优先桌面三平台；移动端不在首期范围（触控/文件系统模型差异大）。
4. **发布与验证**
   - 按 RID 自包含打包；macOS 签名/公证、Linux AppImage/deb；
   - 回归重点：导入、缩略图、NAS 三协议、CAS/分类硬链接、查重、共享库。

## 5. 风险与结论

- 最大风险不是编译，而是**存储语义**（硬链接、大小写、卷校验）与
  **NAS 协议栈**（SMB/凭据管理）；UI 重写反而最可预测。
- 移动端涉及文件系统沙箱与后台任务模型，建议不纳入跨平台首个版本。
- **结论**：PEND-005 暂不启动完整跨平台；1.0.0 保持 Windows 优先。
  若确认跨平台是必需需求，先执行第 1 阶段「核心层抽取」，再以 Avalonia
  作为 1.x 独立里程碑推进。
