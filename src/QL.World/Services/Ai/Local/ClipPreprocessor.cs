using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace QL.World.Services.Ai.Local;

/// <summary>
/// CLIP ViT-B/32 image preprocessing: resize so the image covers 224×224 with
/// a center crop (ImageSharp ResizeMode.Crop), normalize with the CLIP mean/std
/// and lay out as NCHW float32 [1,3,224,224]. Mirrors the spike-validated
/// implementation (tools/spike/ClipSpike/ClipPreprocess.cs).
/// </summary>
public static class ClipPreprocessor
{
    public const int Side = 224;
    public const int PlaneSize = Side * Side;
    public const int TensorLength = 3 * PlaneSize;

    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    public static float[] FromFile(string path)
    {
        using Image<Rgb24> image = Image.Load<Rgb24>(path);
        return FromImage(image);
    }

    public static float[] FromImage(Image<Rgb24> image)
    {
        ArgumentNullException.ThrowIfNull(image);
        using Image<Rgb24> clone = image.Clone();
        clone.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(Side, Side),
            Mode = ResizeMode.Crop,
            Sampler = KnownResamplers.Bicubic,
        }));

        float[] tensor = new float[TensorLength];
        clone.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < Side; y++)
            {
                Span<Rgb24> row = accessor.GetRowSpan(y);
                for (int x = 0; x < Side; x++)
                {
                    tensor[0 * PlaneSize + y * Side + x] = (row[x].R / 255f - Mean[0]) / Std[0];
                    tensor[1 * PlaneSize + y * Side + x] = (row[x].G / 255f - Mean[1]) / Std[1];
                    tensor[2 * PlaneSize + y * Side + x] = (row[x].B / 255f - Mean[2]) / Std[2];
                }
            }
        });
        return tensor;
    }
}
