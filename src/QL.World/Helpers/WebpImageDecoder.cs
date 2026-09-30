using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QL.World.Helpers;

/// <summary>
/// U6-A: WebP → frozen BitmapSource decoder. WIC has no guaranteed WebP
/// codec on Windows, so images go through ImageSharp (RGBA) then are
/// wrapped as a BGRA32 <see cref="BitmapSource"/>. Shared by the local
/// <see cref="ViewModels.Support.GalleryThumbnailProvider"/> and the NAS
/// shared-library thumbnail cache.
/// </summary>
internal static class WebpImageDecoder
{
    /// <summary>Decodes a WebP file; returns null when the file is missing or corrupt.</summary>
    public static BitmapSource? DecodeFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            byte[] webp = File.ReadAllBytes(path);
            return DecodeBytes(webp);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Decodes raw WebP bytes; returns null when corrupt.</summary>
    public static BitmapSource? DecodeBytes(byte[] webp)
    {
        try
        {
            byte[] rgba;
            int width;
            int height;

            using (Image<Rgba32> image = Image.Load<Rgba32>(webp))
            {
                width = image.Width;
                height = image.Height;
                rgba = new byte[width * height * 4];
                image.CopyPixelDataTo(rgba);
            }

            // R,G,B,A → B,G,R,A for PixelFormats.Bgra32.
            for (int i = 0; i < rgba.Length; i += 4)
                (rgba[i], rgba[i + 2]) = (rgba[i + 2], rgba[i]);

            var bitmap = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Bgra32, null, rgba, width * 4);
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }
}
