using QL.World.Services.Nas;

namespace QL.World.Tests.Nas;

/// <summary>
/// M7-A 起 dispatcher 路由 smoke 测试；M9-B 起三个子工厂均为真实实现。
/// 验证 <see cref="NasConnectorFactory"/> 按
/// <see cref="NasConnectionSpec.Protocol"/> 路由到正确子工厂；WebDAV 与
/// SFTP 的真实装配在此做轻量验证（SFTP 以"空用户名即 ProtocolError"证明
/// 未走占位）。<see cref="UnsupportedProtocolFactory"/> 自身行为保留直测。
/// 各驱动的完整行为在各自专属套件（WebDav14 / SMB / SFTP M9）。
/// </summary>
public sealed class NasConnectorFactoryTests
{
    [Fact]
    public void Create_routes_WebDav_to_WebDav_subfactory()
    {
        var factory = new NasConnectorFactory(
            new WebDavConnectorFactory(),
            new SmbConnectorFactory(),
            new SftpConnectorFactory());

        var spec = new NasConnectionSpec
        {
            Id = 1,
            Name = "wdav",
            Protocol = NasConnectionProtocol.WebDav,
            Host = "nas.local",
            RootPath = "/",
            Username = "u",
        };

        using INasConnector connector = factory.Create(spec, plaintextPassword: "p");
        Assert.NotNull(connector);
        Assert.Equal(NasConnectionProtocol.WebDav, connector.Spec.Protocol);
        Assert.Equal("nas.local", connector.Spec.Host);
    }

    [Fact]
    public void Create_routes_Sftp_to_real_subfactory_not_placeholder()
    {
        // M9-B: SFTP 不再走 UnsupportedProtocolFactory。空用户名在任何网络
        // 尝试之前就被真工厂拒绝为 ProtocolError——以此断言路由落到真驱动。
        var factory = new NasConnectorFactory(
            new WebDavConnectorFactory(),
            new SmbConnectorFactory(),
            new SftpConnectorFactory());

        var spec = new NasConnectionSpec
        {
            Id = 2,
            Name = "sftp",
            Protocol = NasConnectionProtocol.Sftp,
            Host = "nas.local",
            RootPath = "/",
            Username = "",
        };

        var ex = Assert.Throws<NasConnectionException>(
            () => factory.Create(spec, plaintextPassword: "p"));
        Assert.Equal(NasConnectionKind.ProtocolError, ex.Kind);
    }

    [Theory]
    [InlineData(NasConnectionProtocol.WebDav)]
    [InlineData(NasConnectionProtocol.Smb)]
    [InlineData(NasConnectionProtocol.Sftp)]
    public void UnsupportedProtocolFactory_rejects_every_shipped_protocol(
        NasConnectionProtocol protocol)
    {
        // M9 起三协议均有真实驱动；占位工厂不得再为任何已发货协议构造，
        // 防止装配笔误把真驱动悄悄换回抛异常的占位。
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new UnsupportedProtocolFactory(protocol));
    }

    [Fact]
    public void Create_throws_ArgumentNull_for_null_spec()
    {
        var factory = new NasConnectorFactory(
            new WebDavConnectorFactory(),
            new SmbConnectorFactory(),
            new SftpConnectorFactory());

        Assert.Throws<ArgumentNullException>(() => factory.Create(null!, plaintextPassword: null));
    }

    [Fact]
    public void Constructor_rejects_null_subfactories()
    {
        Assert.Throws<ArgumentNullException>(() => new NasConnectorFactory(
            null!, new SmbConnectorFactory(), new SftpConnectorFactory()));
        Assert.Throws<ArgumentNullException>(() => new NasConnectorFactory(
            new WebDavConnectorFactory(), null!, new SftpConnectorFactory()));
        Assert.Throws<ArgumentNullException>(() => new NasConnectorFactory(
            new WebDavConnectorFactory(), new SmbConnectorFactory(), null!));
    }
}
