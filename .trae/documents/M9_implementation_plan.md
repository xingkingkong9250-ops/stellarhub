# M9 实施计划：NAS SFTP 协议扩展（目标 v0.4.0，NO.M）

> 2026-09-30 制定。M0–M8 已完成（v0.3.0，553 测试全绿）。M7 预留的 SFTP
> 占位（`UnsupportedProtocolFactory(Sftp)`）是扩展路线唯一功能缺口。M9
> 落地真实 SFTP 驱动，三协议（WebDav/Smb/Sftp）齐备。

## 0. 关键决策（已与用户确认）

1. **驱动实现 = SSH.NET 2026.0.0 NuGet**（MIT；net8.0/net9.0/netstandard2.0，
   .NET 10 可消费；3.4 亿下载）。选型理由：纯托管实现，Windows/macOS/Linux
   行为一致，与 INasConnector 抽象「为 macOS 留路」方向吻合；原生流式 API
   支持 Range/取消。否决 sftp.exe 方案：依赖 Windows 可选 OpenSSH 组件、
   进程式调用无流式 API、跨平台行为不可控、无法沙盒测试。
   - 传递依赖：BouncyCastle.Cryptography ≥2.7.0、
     Microsoft.Extensions.Logging.Abstractions ≥8.0.3。预计 staging +3~5MB，
     M9-Z 记录实测差值。nuget.config 仅允许 nuget.org，符合约束。
2. **主机密钥策略 = 复选框信任首次密钥**：复用 `TrustSelfSigned` 字段
   （不改 config schema），SFTP 下 UI 文案动态变为「信任未知主机密钥
   （首次连接自动接受）」；默认 false（严格）：`HostKeyReceived` 回调中
   `CanTrust=false` 并直接抛带 SHA256 指纹的 `NasConnectionException`
   （Kind=ProtocolError），错误消息指导用户勾选。指纹格式对齐 OpenSSH
   `SHA256:<base64>`。
3. **认证**：密码认证（`PasswordAuthenticationMethod`），DPAPI 解密与
   WebDav/Smb 一致；SFTP 不支持匿名——用户名为空时工厂抛
   NasConnectionException(ProtocolError)。ShareLink+SFTP 组合不支持
   （ShareLink 仅适用 WebDav 匿名），在文案/消息中说明。
4. **端口**：spec.Port null/0 → 22。

## 1. 子阶段划分

- M9-A：NuGet 接入 + SFTP 会话抽象 + SftpNasConnector（10 方法）+ 异常映射 + 连接器测试
- M9-B：SftpConnectorFactory + App.xaml.cs 注入替换占位 + 工厂/路由测试
- M9-C：UI 协议联动（下拉新增 Sftp、端口默认 22、提示文案、TLS 隐藏、
  信任勾选动态文案）+ VM 测试 + 过时文案清理
- M9-Z：收尾（版本/CHANGELOG/GEMINI/UI_NUMBERING/USER_GUIDE + Prepare 门禁 +
  staging 体积对比 + project_memory 沉淀）

每子阶段一个中文 commit，测试与实现同阶段交付。

## 2. M9-A：会话抽象 + 连接器核心

新增目录 `src/QL.World/Services/Nas/Sftp/`：

- `ISftpSession.cs`（internal）：对 `Renci.SshNet.SftpClient` 的窄抽象，
  只暴露连接器需要的原语（全部异步签名带 CancellationToken）：
  - `bool IsConnected { get; }`
  - `Task ConnectAsync(CancellationToken)`
  - `Task<IReadOnlyList<SftpListEntry>> ListAsync(string path, CancellationToken)`
  - `Task<SftpListEntry> StatAsync(string path, CancellationToken)`（文件或目录元数据）
  - `Task<Stream> OpenReadAsync(string path, CancellationToken)`
  - `Task DownloadToFileAsync(string path, string localPath, CancellationToken)`
  - `Task UploadFromFileAsync(string localPath, string path, CancellationToken)`（overwrite=true）
  - `Task CreateDirectoryAsync(string path, CancellationToken)`（幂等）
  - `Task DeleteAsync(string path, CancellationToken)`（幂等）
  - `Task RenameAsync(string src, string dst, CancellationToken)`（overwrite=true）
  - 会话方法失败统一抛 internal `SftpTransportException(NasConnectionKind, msg, inner)`，
    把 Renci 异常类型映射集中在生产实现内（见下）。
- `SftpListEntry.cs`（internal record：Name/FullName/IsDirectory/Length/LastWriteTimeUtc）。
- `RenciSftpSession.cs`（internal sealed，: ISftpSession）：包装
  `Renci.SshNet.SftpClient`（构造接受 `ConnectionInfo`，由工厂构建）。
  - 异步优先用 SftpClient 的 *Async 重载并传 token；包内无异步重载的原语
    （Stat/部分元数据）用 `Task.Run` 包装同步调用，前后置
    `ThrowIfCancellationRequested`。
  - `MapException` 静态映射（internal 供单测）：
    - `SshAuthenticationException` / `SftpPermissionDeniedException` → AuthenticationFailed
    - `SshOperationTimeoutException` / 超时型 SocketException → Timeout
    - SocketException(网络不可达) / `SshConnectionException`（非主机密钥）→ NetworkUnreachable
    - `SftpPathNotFoundException` → NotFound
    - 其它 `SshException`/`SftpException` → ProtocolError；兜底 Unknown
- `SftpNasConnector.cs`（public sealed，: INasConnector）：结构对齐
  SmbNasConnector——
  - 持有 spec + 已连接的 `ISftpSession` + 规范化后的绝对根路径
    （RootPath 必须以 `/` 开头；`/` 即服务器文件系统根；ResolvePath 把
    调用方正斜杠相对路径拼到根，`.`/`..` 不做特殊展开，按 SFTP 服务器语义）。
  - 10 方法语义与 SMB/WebDav 完全一致：
    - TestConnection：session.IsConnected（工厂已 Connect）或 StatAsync(root) 探测；
    - ListFiles：映射 SftpListEntry → NasFileInfo，Path 输出相对根的正斜杠路径
      （根自身→"/"），跳过 `"."`/`".."`；
    - GetMetadata：不存在抛 NotFound；
    - OpenRead：透传会话流（调用方 dispose；session 生命周期随连接器）；
    - DownloadFile：流式落盘，覆盖目标；
    - DownloadRange：OpenRead → Seek(offset) → 读满 length，不足抛
      RangeNotSatisfiable（与 SMB 语义一致，支撑下载队列 416 重试）；
    - UploadFile：上传前确保父目录存在（SftpClient 不会自动建目录），
      overwrite=true；
    - MakeDirectory：逐段创建已存在即跳过（SFTP mkdir 已存在会抛错，需吞
      「already exists」）；
    - Delete：文件直接删；目录仅删空目录；路径已消失视为成功；
    - Move：RenameAsync(overwrite=true) 纯服务端改名；失败按接口 SHOULD
      降级 download→upload→delete（与 WebDav/Smb 一致）。
  - Dispose：释放 session（Dispose 不抛异常）。
  - **internal 构造重载**注入 `ISftpSession`（InternalsVisibleTo 已开），
    与 LocalAiService 注入假对象同一测试模式。

测试（同阶段）：
- `tests/QL.World.Tests/Nas/Sftp/FakeSftpSession.cs`：本地临时目录沙盒实现
  ISftpSession（全部原语用 File/Directory 落地），可配置抛指定
  SftpTransportException（模拟鉴权/网络失败）。
- `tests/.../Nas/Sftp/SftpNasConnectorTests.cs`：对齐 SmbNasConnectorContractTests
  的 14 项边界行为（含 RangeNotSatisfiable、幂等 Delete/Mkdir、Move 改名、
  Upload 自动建父目录、List 相对路径输出、失败 Kind 透传）。
- 参数化 `INasConnectorContractTests.ConnectorCases` 新增
  `SftpLocalFake()`（第三 case），9 个 Theory 自动三跑。
- `RenciExceptionMapTests`：直接构造 Renci 各异常实例验证 MapException Kind。

验收：553 → 预计 580+ 全绿；生产代码仅 Sftp* 文件引用 Renci 命名空间
（异常映射封死在 RenciSftpSession，业务层只见 ISftpSession）。

## 3. M9-B：工厂 + App 注入

- `Sftp/ SftpConnectorFactory.cs`（public sealed，: INasConnectorFactory）：
  - 用户名空 → NasConnectionException(ProtocolError,
    "SFTP 协议必须提供用户名（ShareLink 匿名分享仅支持 WebDav）")；
  - 密码：plaintextPassword 优先，否则 DpApiProtector.Unprotect；
  - internal static `BuildConnectionInfo(spec, password)`（可单测，不触网）：
    host、port ?? 22、`PasswordAuthenticationMethod`、Timeout=15s；
  - 主机密钥：订阅 `ConnectionInfo.HostKeyReceived`，策略抽 internal static
    `SftpHostKeyPolicy.OnKeyReceived(trust, hostKeyBytes)`（纯函数可单测）：
    trust=true → CanTrust=true；trust=false → 返回 false 并产出
    `SHA256:<base64>` 指纹，工厂随即抛带指纹的 NasConnectionException(ProtocolError)；
  - 构造 RenciSftpSession 并 **eager ConnectAsync**（与 SMB 工厂 WNet
    握手对齐：NasConnectionRegistry 据此实现「主库失败抛异常/副库仅 Debug」）；
  - 返回 `new SftpNasConnector(spec, session, rootPath)`。
- `App.xaml.cs`：`new UnsupportedProtocolFactory(Sftp)` →
  `new SftpConnectorFactory()`，注释三协议改述。
- 路由测试更新：
  - `NasConnectorFactoryTests`：Sftp 占位抛错用例改为「记录式假子工厂」
    验证三协议各自路由到对应子工厂（WebDav 仍走真实工厂）；
  - 新增 `SftpConnectorFactoryTests`：空用户名报错、默认端口 22、
    ConnectionInfo 构建参数、主机密钥策略（信任/拒绝两路径）。

## 4. M9-C：UI 协议联动

- `SettingsViewModel`：
  - NasProtocolOptions 增加 `NasConnectionProtocol.Sftp`；
  - 派生属性新增 `IsSftpSelected`（已存在，核对）；
    `IsTlsApplicable = WebDav || Smb`（Sftp 隐藏：SSH 全程加密无开关）；
    `IsTrustSelfSignedApplicable = WebDav || Sftp`（SMB 不显示）；
  - 新增 `NasTrustLabelText`：WebDav→「信任自签名证书（仅限受信任内网）」；
    Sftp→「信任未知主机密钥（首次连接自动接受）」；
  - NasPortHintText：Sftp→「端口（留空=默认 22）」；
  - NasRootPathHint：Sftp→「根路径（如 /volume1/StellarHubShared）」；
  - NasTlsLabelText 不变（Sftp 下整个 CheckBox 隐藏）；
  - OnNasProtocolChanged 端口联动改为三协议 switch（443/445/22）：当前端口
    为 null 或任一已知默认值时填新协议默认，自定义值保留；
  - RaiseNasProtocolDerived 补刷新增属性。
- `SettingsWindow.xaml`：
  - N1 标题「NAS 连接（WebDav + Smb；Sftp 留待 M8）」→
    「NAS 连接（WebDav + Smb + Sftp）」；
  - N5「信任自签名证书」CheckBox 文案改绑 `NasTrustLabelText`。
- 过时文案/注释清理：App.xaml.cs 111-113、NasConnectorFactory.cs 类注释、
  NasConnectionProtocol.cs 枚举注释、INasConnector.cs 顶部注释、
  UnsupportedProtocolFactory.cs（保留类——仅测试使用，注释更新为
  「测试/未来协议占位」，去掉「SFTP reserved for M8」表述）、
  INasConnectorFactory.cs 注释。
- VM 测试：SettingsViewModelNasTests 新增——Options 含三协议、
  切 Sftp 端口填 22/自定义端口保留、IsTlsApplicable=false、
  IsTrustSelfSignedApplicable=true、Hint/TrustLabel 文案正确、
  Sftp→WebDav 切回 443。

## 5. M9-Z：收尾

1. csproj `<Version>` 0.3.0 → 0.4.0（NO.M）；AppInfo.MilestoneLabel
   →「M9 NAS SFTP 协议扩展」；改完复查 UTF-8 BOM。
2. CHANGELOG 顶部新增 [0.4.0]，按 M9-A/B/C 组织。
3. GEMINI.md 里程碑状态 + 项目身份行更新（三协议齐备、SFTP 技术要点）。
4. docs/UI_NUMBERING.md：N 系列无新增控件（复选框复用 N5 既有编号），
   更新 N3/N5 描述注明 Sftp；核对编号无变动。
5. docs/USER_GUIDE.md：NAS 章节补 SFTP 小节（绿联 UGOS 开启 SSH/SFTP
   路径、22 端口、根路径示例、主机密钥勾选说明、SFTP 必须用户名）。
6. 门禁：`powershell -File build.ps1 -Mode Prepare`——0 警告 0 错误 +
   全测试绿 + R2R publish；记录 staging 体积与 v0.3.0 差值
   （SSH.NET+BouncyCastle 实际增量）。
7. 交付总结 + project_memory 沉淀（只记实测坑：SSH.NET API 细节、
   HostKeyReceived 行为、BOM/警告等）。
8. tag 命令给用户手动执行（不自动 push）。

## 6. 风险与对策

| 风险 | 对策 |
|---|---|
| SSH.NET 2026 API 与记忆有出入（异步重载/HostKeyReceived 签名） | M9-A 先 restore 后用对象浏览器/XML 文档核对真实 API，再写实现；最小可编译路径 |
| BouncyCastle 传递依赖拉大体积或触发 NU 警告 | Prepare 门禁抓；若 NU1902 类告警评估升级或 NoWarn（仅在确认为传递依赖且无修复版本时） |
| SftpClient 同步 API 无法取消 | token 注册 session.Disconnect() 作为取消兜底；耗时路径优先异步重载 |
| 无真实 SFTP 服务器，端到端缺口 | 沙盒 Fake 保证契约；真实绿联 SFTP 实测列为需用户人工验收项（与 M5/M7 同策略） |
| 中文 XAML/注释编码 | 仅 XAML/CS（UTF-8 无 BOM 即可）；PS1 不新增中文脚本 |
