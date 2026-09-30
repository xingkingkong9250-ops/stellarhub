namespace QL.World.Services.Nas;

/// <summary>
/// M8-A: NAS 角色枚举。把 v0.2.1 的 IsPrimary 布尔字段升级为三态枚举，
/// 以支持"主 NAS / 副库 / 分享链接"三类连接。NasConnectionSpec.IsPrimary
/// 保留为计算属性（getter => Role == Primary），向后兼容旧 config 反序列化。
/// </summary>
public enum NasRole
{
    /// <summary>
    /// 主 NAS：唯一存放 accounts.json / library.json / pending/ / submissions/
    /// 等用户必要数据的连接。AppSettingsService.ValidateNasRoles() 保证全列表
    /// 至多 1 个 Primary，无 Primary 时第一项自动收敛为 Primary。
    /// </summary>
    Primary = 0,

    /// <summary>
    /// 副库：完整 NAS 连接（WebDav/Smb/Sftp），仅作扩充存储空间使用。
    /// 管理员可读写下载；普通用户只读可下载。不参与账号验证、提交审核流程。
    /// </summary>
    Secondary = 1,

    /// <summary>
    /// 分享链接副库：公开/匿名 WebDAV 链接，无账号密码。构造期强制校验
    /// Username/EncryptedPassword 必须为空；WebDavConnectorFactory 创建时
    /// 跳过 Basic-auth header。
    /// </summary>
    ShareLink = 2,
}
