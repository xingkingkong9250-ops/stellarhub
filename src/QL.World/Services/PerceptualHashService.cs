using System.Globalization;
using System.IO;
using System.Numerics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace QL.World.Services;

/// <summary>
/// 0.16.0 DUP-A: 64-bit dHash 感知哈希。
/// 解码 → 拉伸降采样到 9×8 → 灰度 → 逐行比较相邻列（左 &gt; 右 记 1）。
/// 位序 y*8+x（bit 0 = 左上角），同一张图的转码/缩放/调色仍落在很小的
/// 汉明距离内，是 L1 严格同图判定与全库查重的基础。
/// 计算失败（损坏/不支持格式）返回 null，调用方不得据此阻断导入。
/// </summary>
public sealed class PerceptualHashService
{
    /// <summary>L1 同图判定阈值：汉明距离 ≤ 10 视为同一张图。</summary>
    public const int SameImageMaxDistance = 10;

    /// <summary>dHash 网格：9 列 × 8 行 → 8×8 = 64 个比较位。</summary>
    private const int GridWidth = 9;
    private const int GridHeight = 8;

    private readonly IMagickDecoder _magick;

    public PerceptualHashService(IMagickDecoder? magick = null)
        => _magick = magick ?? new MagickDecoder();

    /// <summary>对已打开的流计算 phash；调用方持有流。失败返回 null。</summary>
    public ulong? TryCompute(Stream stream)
    {
        if (stream is null)
            return null;

        try
        {
            using Image<Rgba32> image = Image.Load<Rgba32>(stream);
            return Compute(image);
        }
        catch (UnknownImageFormatException)
        {
            // ImageSharp 不认识的容器（TGA/PSD/EXR/HDR）交给 Magick。
            if (!_magick.TryLoadFrame(stream, out Image? frame) || frame is null)
                return null;
            try
            {
                using Image<Rgba32> convertible = frame.CloneAs<Rgba32>();
                return Compute(convertible);
            }
            finally
            {
                frame.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>对磁盘文件计算 phash（DUP-C 从缩略图补算复用）。失败返回 null。</summary>
    public ulong? TryCompute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            using Image<Rgba32> image = Image.Load<Rgba32>(path);
            return Compute(image);
        }
        catch (UnknownImageFormatException)
        {
            if (!_magick.TryLoadFrame(path, out Image? frame) || frame is null)
                return null;
            try
            {
                using Image<Rgba32> convertible = frame.CloneAs<Rgba32>();
                return Compute(convertible);
            }
            finally
            {
                frame.Dispose();
            }
        }
        catch
        {
            return null;
        }
    }

    private static ulong Compute(Image<Rgba32> source)
    {
        using var sample = source.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(GridWidth, GridHeight),
            Sampler = KnownResamplers.Lanczos3,
            Mode = ResizeMode.Stretch,
        }));

        Span<int> luma = stackalloc int[GridWidth * GridHeight];
        for (int y = 0; y < GridHeight; y++)
        {
            for (int x = 0; x < GridWidth; x++)
            {
                Rgba32 pixel = sample[x, y];
                // 整数近似亮度（ITU-R BT.601）：
                luma[y * GridWidth + x] = (77 * pixel.R + 150 * pixel.G + 29 * pixel.B) >> 8;
            }
        }

        ulong hash = 0;
        for (int y = 0; y < GridHeight; y++)
        {
            int row = y * GridWidth;
            for (int x = 0; x < GridWidth - 1; x++)
            {
                if (luma[row + x] > luma[row + x + 1])
                    hash |= 1UL << (y * 8 + x);
            }
        }
        return hash;
    }

    /// <summary>汉明距离：两个 64 位哈希不同位的个数（0 = 完全一致）。</summary>
    public static int HammingDistance(ulong a, ulong b)
        => BitOperations.PopCount(a ^ b);

    /// <summary>哈希转 16 位小写 hex，落 images.phash。</summary>
    public static string ToHex(ulong hash) => hash.ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>hex 解析；NULL/空/非法返回 null（视为未计算）。</summary>
    public static ulong? TryParseHex(string? hex)
        => string.IsNullOrWhiteSpace(hex) ? null
            : ulong.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong value)
                ? value
                : null;
}
