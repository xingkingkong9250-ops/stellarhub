using System.IO;
using SixLabors.ImageSharp;

namespace QL.World.Services;

/// <summary>
/// Decoder probe backed by SixLabors.ImageSharp, falling back to
/// Magick.NET (TGA/EXR/HDR/PSD) when ImageSharp does not recognize the file.
/// </summary>
public sealed class ImageInspector
{
    private readonly IMagickDecoder _magick;

    public ImageInspector(IMagickDecoder? magick = null)
        => _magick = magick ?? new MagickDecoder();

    /// <summary>
    /// Reads only the header (full pixels are never decoded).
    /// False means unsupported/corrupt — the importer counts it as skipped.
    /// </summary>
    public bool TryReadMetadata(
        string path,
        out int width,
        out int height,
        out string? format)
    {
        width = 0;
        height = 0;
        format = null;

        try
        {
            ImageInfo info = Image.Identify(path);
            width = info.Width;
            height = info.Height;
            format = info.Metadata.DecodedImageFormat?.Name?.ToLowerInvariant();
            return true;
        }
        catch (UnknownImageFormatException)
        {
            // ImageSharp does not know this container (e.g. tga/exr/hdr/psd) —
            // let Magick decide; corrupt files fail there too.
            return _magick.TryReadMetadata(path, out width, out height, out format);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or InvalidImageContentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Header-only probe over an already-open <see cref="Stream"/>. M5-D
    /// remote import path: the network stream from
    /// <see cref="Nas.RemoteFileSystemAccessor.OpenRead"/> is probed in place
    /// so a temp file is never written just to read width/height. Caller
    /// owns the stream.
    /// </summary>
    public bool TryReadMetadata(
        Stream stream,
        out int width,
        out int height,
        out string? format)
    {
        width = 0;
        height = 0;
        format = null;

        try
        {
            ImageInfo info = Image.Identify(stream);
            width = info.Width;
            height = info.Height;
            format = info.Metadata.DecodedImageFormat?.Name?.ToLowerInvariant();
            return true;
        }
        catch (UnknownImageFormatException)
        {
            return _magick.TryReadMetadata(stream, out width, out height, out format);
        }
        catch (Exception ex) when (ex is IOException
            or NotSupportedException or InvalidImageContentException)
        {
            return false;
        }
    }
}
