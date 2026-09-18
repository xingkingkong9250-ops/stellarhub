using QL.World.Services.Colors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QL.World.Tests;

/// <summary>ColorExtractor deterministic K-means extraction checks.</summary>
public sealed class ColorExtractorTests
{
    private const string HexPattern = "^#[0-9A-F]{6}$";

    [Fact]
    public void Solid_color_image_yields_single_matching_hex()
    {
        using var image = new Image<Rgba32>(40, 30, new Rgba32(200, 100, 50));
        var colors = ColorExtractor.Extract(image);

        Assert.Equal(["#C86432"], colors);
    }

    [Fact]
    public void Solid_gray_image_yields_gray_hex()
    {
        using var image = new Image<Rgba32>(16, 16, new Rgba32(128, 128, 128));
        var colors = ColorExtractor.Extract(image);

        Assert.Equal(["#808080"], colors);
    }

    [Fact]
    public void Two_tone_image_yields_both_extreme_colors()
    {
        // Top half pure red, bottom half pure blue. Row-major seed indices
        // (0, N/4, N/2, 3N/4) then land on both regions.
        using var image = new Image<Rgba32>(64, 64);
        Fill(image, (x, y) => y < 32 ? new Rgba32(255, 0, 0) : new Rgba32(0, 0, 255));

        var colors = ColorExtractor.Extract(image);

        Assert.True(colors.Count >= 2, $"expected multiple clusters, got {string.Join(",", colors)}");
        Assert.Contains("#FF0000", colors);
        Assert.Contains("#0000FF", colors);
    }

    [Fact]
    public void Vertical_gradient_image_yields_multiple_clusters()
    {
        // Brightness increases top → bottom, so the four seed rows sample
        // four different tones and K-means keeps several clusters.
        using var image = new Image<Rgba32>(64, 64);
        Fill(image, (x, y) =>
        {
            byte v = (byte)(y * 255 / 63);
            return new Rgba32(v, v, v);
        });

        var colors = ColorExtractor.Extract(image);

        Assert.True(colors.Count >= 2, $"expected multiple clusters, got {string.Join(",", colors)}");
    }

    [Fact]
    public void All_colors_are_canonical_hex()
    {
        using var image = new Image<Rgba32>(64, 64);
        Fill(image, (x, y) => y < 32 ? new Rgba32(255, 0, 0) : new Rgba32(0, 0, 255));

        var colors = ColorExtractor.Extract(image);

        Assert.All(colors, c => Assert.Matches(HexPattern, c));
        Assert.True(colors.Count <= 5);
    }

    [Fact]
    public void Extraction_is_deterministic()
    {
        using var image = new Image<Rgba32>(48, 48);
        Fill(image, (x, y) =>
        {
            byte v = (byte)((x + y) * 255 / 94);
            return new Rgba32(v, (byte)(255 - v), v);
        });

        var first = ColorExtractor.Extract(image);
        var second = ColorExtractor.Extract(image);

        Assert.Equal(first, second);
    }

    [Fact]
    public void Fully_transparent_image_yields_no_colors()
    {
        using var image = new Image<Rgba32>(16, 16, new Rgba32(0, 0, 0, 0));

        Assert.Empty(ColorExtractor.Extract(image));
    }

    private static void Fill(Image<Rgba32> image, Func<int, int, Rgba32> pixel)
    {
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                    row[x] = pixel(x, y);
            }
        });
    }
}
