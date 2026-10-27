using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace QL.World.Tests.Support;

/// <summary>Creates tiny real PNG files on disk for import/thumbnail tests.</summary>
internal static class TestImageFactory
{
    public static string CreatePng(string directory, string name, int width = 12, int height = 8)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        using var image = new Image<Rgba32>(width, height, new Rgba32(200, 100, 50));
        image.Save(path, new PngEncoder());
        return path;
    }

    /// <summary>DUP-B: 左暗右亮的横向渐变 PNG（与 JPEG 变体可互认为同图）。</summary>
    public static string CreateGradientPng(
        string directory, string name, int width = 64, int height = 48)
        => CreateGradient(directory, name, new PngEncoder(), width, height);

    /// <summary>DUP-B: 同一渐变内容的 JPEG 转码变体（字节不同但 dHash 接近）。</summary>
    public static string CreateGradientJpeg(
        string directory, string name, int width = 64, int height = 48)
        => CreateGradient(directory, name, new JpegEncoder { Quality = 80 }, width, height);

    /// <summary>DUP-B: 16px 棋盘格 PNG，与渐变的 dHash 距离远超同图阈值。</summary>
    public static string CreateCheckerboardPng(
        string directory, string name, int width = 64, int height = 48)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        using var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte level = ((x / 8) + (y / 8)) % 2 == 0 ? (byte)240 : (byte)16;
                image[x, y] = new Rgba32(level, level, level);
            }
        }
        image.Save(path, new PngEncoder());
        return path;
    }

    private static string CreateGradient(
        string directory, string name, IImageEncoder encoder, int width, int height)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        using var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte level = (byte)(20 + x * 200 / Math.Max(1, width - 1));
                image[x, y] = new Rgba32(level, level, level);
            }
        }
        image.Save(path, encoder);
        return path;
    }

    /// <summary>Writes bytes that are not a decodable image.</summary>
    public static string CreateGarbage(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        return path;
    }
}
