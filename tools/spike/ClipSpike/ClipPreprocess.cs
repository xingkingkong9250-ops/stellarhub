using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace ClipSpike;

/// <summary>CLIP ViT-B/32 image preprocessing: resize → center crop → normalize → NCHW.</summary>
public static class ClipPreprocess
{
    public const int Side = 224;
    private static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    private static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    public static float[] FromFile(string path)
    {
        using Image<Rgb24> image = Image.Load<Rgb24>(path);
        return FromImage(image);
    }

    public static float[] FromImage(Image<Rgb24> image)
    {
        using Image<Rgb24> clone = image.Clone();
        clone.Mutate(ctx => ctx
            .Resize(new ResizeOptions
            {
                Size = new Size(Side, Side),
                Mode = ResizeMode.Crop,
                Sampler = KnownResamplers.Bicubic,
            }));

        float[] tensor = new float[1 * 3 * Side * Side];
        clone.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < Side; y++)
            {
                Span<Rgb24> row = accessor.GetRowSpan(y);
                for (int x = 0; x < Side; x++)
                {
                    tensor[0 * Side * Side + y * Side + x] = (row[x].R / 255f - Mean[0]) / Std[0];
                    tensor[1 * Side * Side + y * Side + x] = (row[x].G / 255f - Mean[1]) / Std[1];
                    tensor[2 * Side * Side + y * Side + x] = (row[x].B / 255f - Mean[2]) / Std[2];
                }
            }
        });
        return tensor;
    }
}
