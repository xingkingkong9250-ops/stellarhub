using System.IO;
using SixLabors.ImageSharp;

namespace QL.World.Services;

/// <summary>
/// Decoder probe backed by SixLabors.ImageSharp. Kept behind a service so
/// M4 can add Magick.NET for EXR/HDR/PSD without touching importer logic.
/// </summary>
public sealed class ImageInspector
{
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or NotSupportedException or InvalidImageContentException or UnknownImageFormatException)
        {
            return false;
        }
    }
}
