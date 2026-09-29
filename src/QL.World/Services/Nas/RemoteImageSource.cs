using System.IO;

namespace QL.World.Services.Nas;

/// <summary>
/// <see cref="IImageSource"/> backed by a remote (NAS) file via an
/// <see cref="INasConnector"/>. <see cref="LocalPath"/> is <c>null</c> so
/// <see cref="ThumbnailService"/> takes the temp-file download branch:
/// <see cref="OpenReadAsync"/> stream-copies the bytes into
/// <c>%TEMP%\StellarHub\&lt;hash&gt;.tmp</c>, then
/// <c>Image.Load(tempFile)</c> + WebP encode + delete temp.
///
/// The connector is NOT owned by this instance —
/// <see cref="GalleryThumbnailProvider"/> caches connectors by
/// <see cref="NasConnectionSpec.Id"/> and disposes them on shutdown. This
/// keeps a single HttpClient alive across many thumbnails instead of
/// rebuilding the auth handshake per request.
/// </summary>
public sealed class RemoteImageSource : IImageSource
{
    private readonly INasConnector _connector;
    private readonly string _remotePath;

    /// <param name="connector">Live <see cref="INasConnector"/> (caller-owned).</param>
    /// <param name="remotePath">
    /// Remote path rooted at <see cref="NasConnectionSpec.RootPath"/>
    /// (e.g. <c>/textures/wood/oak.png</c>). NOT the canonical
    /// <c>nas://{id}{remotePath}</c> form — the caller strips the scheme.
    /// </param>
    /// <param name="displayName">Value for <see cref="DisplayName"/> (usually the canonical path).</param>
    public RemoteImageSource(INasConnector connector, string remotePath, string displayName)
    {
        _connector = connector ?? throw new ArgumentNullException(nameof(connector));
        ArgumentException.ThrowIfNullOrWhiteSpace(remotePath);
        _remotePath = remotePath;
        DisplayName = displayName;
    }

    /// <inheritdoc />
    public bool IsLocal => false;

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public string? LocalPath => null;

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(CancellationToken cancellationToken) =>
        _connector.OpenReadAsync(_remotePath, cancellationToken);
}
