using System.IO;
using QL.World.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace QL.World.Tests;

/// <summary>0.16.0 DUP-A: dHash 稳定性与区分度。</summary>
public sealed class PerceptualHashServiceTests
{
    private readonly PerceptualHashService _hasher = new();

    [Fact]
    public void Reencoded_jpeg_stays_within_same_image_threshold()
    {
        byte[] png = Render(64, 48, HorizontalGradient, new PngEncoder());
        byte[] jpeg = Render(64, 48, HorizontalGradient, new JpegEncoder { Quality = 80 });

        ulong? a = Compute(png);
        ulong? b = Compute(jpeg);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.True(
            PerceptualHashService.HammingDistance(a!.Value, b!.Value) <= 10,
            $"转码后距离应 ≤10，实际 {PerceptualHashService.HammingDistance(a.Value, b!.Value)}");
    }

    [Fact]
    public void Scaled_variant_stays_within_same_image_threshold()
    {
        byte[] small = Render(24, 16, HorizontalGradient, new PngEncoder());
        byte[] large = Render(96, 64, HorizontalGradient, new PngEncoder());

        ulong? a = Compute(small);
        ulong? b = Compute(large);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.True(
            PerceptualHashService.HammingDistance(a!.Value, b!.Value) <= 10,
            $"缩放后距离应 ≤10，实际 {PerceptualHashService.HammingDistance(a.Value, b!.Value)}");
    }

    [Fact]
    public void Different_content_exceeds_same_image_threshold()
    {
        byte[] gradient = Render(64, 48, HorizontalGradient, new PngEncoder());
        byte[] checker = Render(64, 48, Checkerboard, new PngEncoder());

        ulong? a = Compute(gradient);
        ulong? b = Compute(checker);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.True(
            PerceptualHashService.HammingDistance(a!.Value, b!.Value) > 10,
            $"不同内容距离应 >10，实际 {PerceptualHashService.HammingDistance(a.Value, b!.Value)}");
    }

    [Fact]
    public void Corrupt_bytes_return_null()
    {
        using var stream = new MemoryStream([1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Null(_hasher.TryCompute(stream));
    }

    [Fact]
    public void Missing_file_returns_null()
    {
        Assert.Null(_hasher.TryCompute(Path.Combine(Path.GetTempPath(), "no-such-dup-a.png")));
    }

    [Fact]
    public void File_and_stream_paths_produce_identical_hashes()
    {
        byte[] png = Render(64, 48, HorizontalGradient, new PngEncoder());
        string path = Path.Combine(Path.GetTempPath(), $"dup-a-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(path, png);

        try
        {
            ulong? fromStream = Compute(png);
            ulong? fromPath = _hasher.TryCompute(path);
            Assert.Equal(fromStream, fromPath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Hex_round_trip_and_invalid_input()
    {
        const ulong value = 0xDEADBEEF01234567UL;

        Assert.Equal("deadbeef01234567", PerceptualHashService.ToHex(value));
        Assert.Equal(value, PerceptualHashService.TryParseHex("deadbeef01234567"));
        Assert.Equal(0UL, PerceptualHashService.TryParseHex(PerceptualHashService.ToHex(0)));
        Assert.Null(PerceptualHashService.TryParseHex(null));
        Assert.Null(PerceptualHashService.TryParseHex("   "));
        Assert.Null(PerceptualHashService.TryParseHex("not-a-hash"));
    }

    [Fact]
    public void Hamming_distance_counts_differing_bits()
    {
        Assert.Equal(0, PerceptualHashService.HammingDistance(0, 0));
        Assert.Equal(0, PerceptualHashService.HammingDistance(42, 42));
        Assert.Equal(2, PerceptualHashService.HammingDistance(0b1011, 0b0001));
        Assert.Equal(64, PerceptualHashService.HammingDistance(ulong.MaxValue, 0));
    }

    private ulong? Compute(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        return _hasher.TryCompute(stream);
    }

    private static byte[] Render(
        int width,
        int height,
        Func<int, int, Rgba32> paint,
        SixLabors.ImageSharp.Formats.IImageEncoder encoder)
    {
        using var image = new Image<Rgba32>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
                image[x, y] = paint(x, y);
        }

        using var buffer = new MemoryStream();
        image.Save(buffer, encoder);
        return buffer.ToArray();
    }

    /// <summary>左暗右亮的横向渐变（相邻列差异稳定，抗转码/缩放）。</summary>
    private static Rgba32 HorizontalGradient(int x, int y)
    {
        byte level = (byte)(20 + x * 200 / 63);
        return new Rgba32(level, level, level);
    }

    /// <summary>16px 棋盘格：与渐变图的相邻列关系完全不同。</summary>
    private static Rgba32 Checkerboard(int x, int y)
        => ((x / 8) + (y / 8)) % 2 == 0
            ? new Rgba32(240, 240, 240)
            : new Rgba32(16, 16, 16);
}
