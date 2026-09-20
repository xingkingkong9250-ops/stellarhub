using System.IO;
using QL.World.Services.Ai.Local;
using QL.World.Tests.Support;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace QL.World.Tests.Ai.Local;

/// <summary>
/// Golden values for the CLIP normalization constants are duplicated here on
/// purpose — they pin the preprocessing math against accidental edits.
/// </summary>
public sealed class ClipPreprocessorTests : IDisposable
{
    private const float MeanR = 0.48145466f;
    private const float MeanG = 0.4578275f;
    private const float MeanB = 0.40821073f;
    private const float StdR = 0.26862954f;
    private const float StdG = 0.26130258f;
    private const float StdB = 0.27577711f;

    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));

    public ClipPreprocessorTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public void Solid_color_non_square_produces_exact_constant_tensor()
    {
        using var image = new Image<Rgb24>(400, 200, new Rgb24(200, 100, 50));

        float[] tensor = ClipPreprocessor.FromImage(image);

        Assert.Equal(ClipPreprocessor.TensorLength, tensor.Length);
        float expectedR = (200 / 255f - MeanR) / StdR;
        float expectedG = (100 / 255f - MeanG) / StdG;
        float expectedB = (50 / 255f - MeanB) / StdB;

        Assert.All(tensor.Take(ClipPreprocessor.PlaneSize), v => Assert.Equal(expectedR, v, 4));
        Assert.All(
            tensor.Skip(ClipPreprocessor.PlaneSize).Take(ClipPreprocessor.PlaneSize),
            v => Assert.Equal(expectedG, v, 4));
        Assert.All(
            tensor.Skip(2 * ClipPreprocessor.PlaneSize),
            v => Assert.Equal(expectedB, v, 4));
    }

    [Fact]
    public void Nchw_layout_maps_channels_then_rows()
    {
        // 224×224 (no resize) with R=x, G=y so each axis is independently checkable.
        using var image = new Image<Rgb24>(224, 224);
        image.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < 224; y++)
            {
                Span<Rgb24> row = accessor.GetRowSpan(y);
                for (int x = 0; x < 224; x++)
                    row[x] = new Rgb24((byte)x, (byte)y, 128);
            }
        });

        float[] tensor = ClipPreprocessor.FromImage(image);
        int plane = ClipPreprocessor.PlaneSize;

        for (int i = 0; i < plane; i += 997)
        {
            int y = i / 224;
            int x = i % 224;
            Assert.Equal(((byte)x / 255f - MeanR) / StdR, tensor[i], 4);
            Assert.Equal(((byte)y / 255f - MeanG) / StdG, tensor[plane + i], 4);
            Assert.Equal((128 / 255f - MeanB) / StdB, tensor[2 * plane + i], 4);
        }
    }

    [Fact]
    public void FromFile_matches_FromImage()
    {
        string path = TestImageFactory.CreatePng(_directory, "solid.png", 12, 8);
        using var image = Image.Load<Rgb24>(path);

        float[] fromFile = ClipPreprocessor.FromFile(path);
        float[] fromImage = ClipPreprocessor.FromImage(image);

        Assert.Equal(fromImage, fromFile);
    }
}
