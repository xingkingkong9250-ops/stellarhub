using System.IO;

namespace QL.World.Services.Nas;

/// <summary>
/// <see cref="IImageSource"/> backed by a local Windows file path.
/// <see cref="OpenReadAsync"/> returns a fresh <see cref="FileStream"/>;
/// <see cref="LocalPath"/> is the wrapped path so
/// <see cref="ThumbnailService"/> can take the fast
/// <c>Image.Load(path)</c> + MagickDecoder-fallback branch.
/// </summary>
public sealed class LocalImageSource : IImageSource
{
    /// <param name="path">Absolute local file path.</param>
    public LocalImageSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        LocalPath = path;
    }

    /// <inheritdoc />
    public bool IsLocal => true;

    /// <inheritdoc />
    public string DisplayName => LocalPath!;

    /// <inheritdoc />
    public string? LocalPath { get; }

    /// <inheritdoc />
    public Task<Stream> OpenReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<Stream>(File.OpenRead(LocalPath!));
    }
}
