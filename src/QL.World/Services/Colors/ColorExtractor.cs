using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace QL.World.Services.Colors;

/// <summary>
/// Deterministic dominant-color extraction: downsample to ≤64×64, K-means
/// (k=4, fixed seeding, ≤10 iterations), clusters sorted by weight produce
/// 3–5 "#RRGGBB" values. Fully deterministic — the same image always yields
/// the same colors, so NULL-only backfill is idempotent.
/// </summary>
public static class ColorExtractor
{
    private const int MaxSampleSide = 64;
    private const int ClusterCount = 4;
    private const int MaxIterations = 10;
    private const int MaxReturned = 5;

    /// <summary>Extracts dominant colors from an already-loaded image.</summary>
    public static IReadOnlyList<string> Extract(Image image)
    {
        using Image<Rgba32> sample = image.CloneAs<Rgba32>();
        sample.Mutate(ctx => ctx.Resize(new ResizeOptions
        {
            Size = new Size(MaxSampleSide, MaxSampleSide),
            Mode = ResizeMode.Max,
            Sampler = KnownResamplers.Bicubic,
        }));

        // Collect opaque pixels (alpha at/below the threshold is background).
        List<Rgba32> pixels = new(MaxSampleSide * MaxSampleSide);
        sample.ProcessPixelRows(accessor =>
        {
            for (int y = 0; y < accessor.Height; y++)
            {
                Span<Rgba32> row = accessor.GetRowSpan(y);
                foreach (ref readonly Rgba32 px in row)
                {
                    if (px.A > 16) pixels.Add(px);
                }
            }
        });
        if (pixels.Count == 0) return [];

        // Deterministic seeding: spread initial centers evenly over the sample.
        var centers = new float[ClusterCount][];
        for (int i = 0; i < ClusterCount; i++)
        {
            int index = i * pixels.Count / ClusterCount;
            Rgba32 px = pixels[index];
            centers[i] = [px.R, px.G, px.B];
        }

        var assignments = new int[pixels.Count];
        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            bool changed = AssignClusters(pixels, centers, assignments);
            UpdateCenters(pixels, assignments, centers);
            if (!changed) break;
        }

        // Cluster weights → sort → top 3–5 as hex.
        int[] weights = new int[ClusterCount];
        foreach (int a in assignments) weights[a]++;

        return Enumerable.Range(0, ClusterCount)
            .Where(i => weights[i] > 0)
            .OrderByDescending(i => weights[i])
            .Take(MaxReturned)
            .Select(i => ToHex(centers[i]))
            .ToList();
    }

    private static bool AssignClusters(List<Rgba32> pixels, float[][] centers, int[] assignments)
    {
        bool changed = false;
        for (int i = 0; i < pixels.Count; i++)
        {
            Rgba32 px = pixels[i];
            float best = float.MaxValue;
            int bestCluster = 0;
            for (int c = 0; c < centers.Length; c++)
            {
                float dr = px.R - centers[c][0];
                float dg = px.G - centers[c][1];
                float db = px.B - centers[c][2];
                float distance = dr * dr + dg * dg + db * db;
                if (distance < best)
                {
                    best = distance;
                    bestCluster = c;
                }
            }
            if (assignments[i] != bestCluster)
            {
                assignments[i] = bestCluster;
                changed = true;
            }
        }
        return changed;
    }

    private static void UpdateCenters(List<Rgba32> pixels, int[] assignments, float[][] centers)
    {
        var sums = new float[ClusterCount][];
        for (int c = 0; c < ClusterCount; c++) sums[c] = new float[4]; // r, g, b, count

        for (int i = 0; i < pixels.Count; i++)
        {
            float[] bucket = sums[assignments[i]];
            bucket[0] += pixels[i].R;
            bucket[1] += pixels[i].G;
            bucket[2] += pixels[i].B;
            bucket[3] += 1;
        }

        for (int c = 0; c < ClusterCount; c++)
        {
            if (sums[c][3] > 0)
            {
                centers[c][0] = sums[c][0] / sums[c][3];
                centers[c][1] = sums[c][1] / sums[c][3];
                centers[c][2] = sums[c][2] / sums[c][3];
            }
        }
    }

    private static string ToHex(float[] center) =>
        $"#{(int)Math.Round(center[0]):X2}{(int)Math.Round(center[1]):X2}{(int)Math.Round(center[2]):X2}";
}
