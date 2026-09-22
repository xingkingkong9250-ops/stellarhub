using System.IO;
using ImageMagick;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QL.World.Services;

/// <summary>
/// Decodes the professional formats SixLabors.ImageSharp cannot read
/// (TGA / EXR / HDR / PSD). Implemented with Magick.NET (Q8 build, so
/// HDR/EXR are auto-converted to 8-bit without HDRI tone-mapping).
/// </summary>
public interface IMagickDecoder
{
    /// <summary>Header-only probe; false means unreadable/corrupt.</summary>
    bool TryReadMetadata(string path, out int width, out int height, out string? format);

    /// <summary>
    /// Decodes the composite frame (PSD layers flattened) into an ImageSharp
    /// image. On success <paramref name="image"/> is non-null and owned by the
    /// caller. Returns false when the file cannot be decoded.
    /// </summary>
    bool TryLoadFrame(string path, out Image? image);
}

/// <summary>
/// Pixel transfer goes through an in-memory PNG bridge: Magick decodes and
/// flattens, ImageSharp re-decodes the PNG. This sidesteps channel-order and
/// colorspace ambiguity of raw pixel buffers across TGA/EXR/HDR/PSD — the
/// extra encode pass is negligible next to decoding multi-megabyte textures.
/// </summary>
internal sealed class MagickDecoder : IMagickDecoder
{
    public bool TryReadMetadata(string path, out int width, out int height, out string? format)
    {
        width = 0;
        height = 0;
        format = null;

        try
        {
            var info = new MagickImageInfo(path);
            width = checked((int)info.Width);
            height = checked((int)info.Height);
            format = info.Format.ToString().ToLowerInvariant();
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException or MagickException)
        {
            return false;
        }
    }

    public bool TryLoadFrame(string path, out Image? image)
    {
        image = null;

        try
        {
            using var frames = new MagickImageCollection(path);
            // PSD: flatten layers onto the composite frame; no-op for single
            // image formats (TGA/EXR/HDR).
            frames.Flatten();
            if (frames.Count == 0)
                return false;

            byte[] png = frames.ToByteArray(MagickFormat.Png);
            image = Image.Load<Rgba32>(png);
            return true;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException or MagickException)
        {
            image?.Dispose();
            image = null;
            return false;
        }
    }
}
