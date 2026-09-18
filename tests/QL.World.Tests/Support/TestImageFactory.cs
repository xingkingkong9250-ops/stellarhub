using System.IO;
using SixLabors.ImageSharp;
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

    /// <summary>Writes bytes that are not a decodable image.</summary>
    public static string CreateGarbage(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        return path;
    }
}
