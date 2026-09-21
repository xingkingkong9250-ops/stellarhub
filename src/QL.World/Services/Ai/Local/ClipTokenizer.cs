using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace QL.World.Services.Ai.Local;

/// <summary>
/// Minimal OpenAI CLIP BPE tokenizer (byte-level, 77 context). Loads
/// vocab.json + merges.txt (distributed as embedded resources and unpacked
/// into {dataRoot}\models\). Algorithm: lowercase → pre-tokenize regex →
/// byte-unicode map → BPE merges → ids.
/// Ported from tools/spike/ClipSpike/ClipTokenizer.cs (M3-D spike). The
/// BytesToUnicode range is the fixed version: `'~' - '!' + 1` (= 94), not
/// `94 - '!' + 1` (= 62); see project_memory for the original trap.
/// </summary>
public sealed class ClipTokenizer
{
    private const int ContextLength = 77;
    private const int SotToken = 49406;
    private const int EotToken = 49407;

    // GPT-2 / CLIP pre-tokenization pattern. Pre-compiled (no source generator)
    // to keep the regex behavior identical to the spike reference.
    private static readonly Regex SplitPatternRegex = new(
        @"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Dictionary<string, int> _encoder;
    private readonly Dictionary<(string, string), int> _bpeRanks;
    private readonly Dictionary<string, string> _byteEncoder;
    private readonly Dictionary<string, string[]> _cache = new();

    /// <summary>Embedded resource name of the CLIP vocab.json (49408 entries).</summary>
    public const string VocabResourceName = "QL.World.Assets.Ml.vocab.json";

    /// <summary>Embedded resource name of the CLIP merges.txt (BPE merge ranks).</summary>
    public const string MergesResourceName = "QL.World.Assets.Ml.merges.txt";

    public const string VocabFileName = "vocab.json";
    public const string MergesFileName = "merges.txt";

    /// <summary>
    /// Copies the embedded vocab.json + merges.txt into the models directory
    /// the first time they are needed and returns both target paths. Skipped
    /// when files already exist (so a newer user-provided file wins). Mirrors
    /// the ClipTextIndex.EnsureDistributed pattern.
    /// </summary>
    public static (string VocabPath, string MergesPath) EnsureDistributed(string modelsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelsDirectory);
        Directory.CreateDirectory(modelsDirectory);

        string vocabTarget = Path.Combine(modelsDirectory, VocabFileName);
        string mergesTarget = Path.Combine(modelsDirectory, MergesFileName);

        Assembly assembly = typeof(ClipTokenizer).Assembly;
        if (!File.Exists(vocabTarget))
            ExtractResource(assembly, VocabResourceName, vocabTarget);
        if (!File.Exists(mergesTarget))
            ExtractResource(assembly, MergesResourceName, mergesTarget);

        return (vocabTarget, mergesTarget);
    }

    private static void ExtractResource(Assembly assembly, string resourceName, string targetPath)
    {
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {resourceName}");
        using FileStream output = File.Create(targetPath);
        stream.CopyTo(output);
    }

    public ClipTokenizer(string vocabJsonPath, string mergesPath)
    {
        _encoder = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(
            File.ReadAllText(vocabJsonPath))
            ?? throw new InvalidDataException("vocab.json parse failed");

        _bpeRanks = new Dictionary<(string, string), int>();
        int rank = 0;
        foreach (string line in File.ReadLines(mergesPath))
        {
            if (line.StartsWith("#") || line.Length == 0) continue;
            string[] parts = line.Split(' ');
            if (parts.Length == 2) _bpeRanks[(parts[0], parts[1])] = rank++;
        }

        _byteEncoder = BytesToUnicode();
    }

    /// <summary>Returns [SOT, ...ids..., EOT, 0, 0, ...] of length 77.</summary>
    public int[] Encode(string text)
    {
        List<int> ids = [SotToken];
        foreach (Match m in SplitPatternRegex.Matches(text.Trim().ToLowerInvariant()))
            ids.AddRange(EncodeToken(m.Value));

        ids.Add(EotToken);
        if (ids.Count > ContextLength)
            throw new ArgumentException($"Text too long: {ids.Count} tokens > {ContextLength}");

        while (ids.Count < ContextLength) ids.Add(0);
        return [.. ids];
    }

    /// <summary>Diagnostic: BPE subword strings per pre-token (no ids).</summary>
    public IReadOnlyList<string> TokenStrings(string text)
    {
        var result = new List<string>();
        foreach (Match m in SplitPatternRegex.Matches(text.Trim().ToLowerInvariant()))
        {
            string mapped = string.Concat(
                Encoding.UTF8.GetBytes(m.Value).Select(b => _byteEncoder[((char)b).ToString()]));
            result.AddRange(Bpe(mapped));
        }
        return result;
    }

    /// <summary>Exposes the byte-to-unicode map for diagnostics/tests.</summary>
    public IReadOnlyDictionary<string, string> ByteEncoder => _byteEncoder;

    private IEnumerable<int> EncodeToken(string token)
    {
        string mapped = string.Concat(
            Encoding.UTF8.GetBytes(token).Select(b => _byteEncoder[((char)b).ToString()]));

        string[] parts = Bpe(mapped);
        return parts.Select(p => _encoder.TryGetValue(p, out int id)
            ? id
            : throw new KeyNotFoundException($"Unknown token part '{p}'"));
    }

    private string[] Bpe(string token)
    {
        if (_cache.TryGetValue(token, out string[]? cached)) return cached;
        if (token.Length < 2) { _cache[token] = [token]; return [token]; }

        List<string> word = [.. token.Select(c => c.ToString())];
        while (true)
        {
            // Find lowest-rank adjacent pair.
            int bestRank = int.MaxValue;
            int bestIndex = -1;
            for (int i = 0; i < word.Count - 1; i++)
            {
                if (_bpeRanks.TryGetValue((word[i], word[i + 1]), out int r) && r < bestRank)
                {
                    bestRank = r;
                    bestIndex = i;
                }
            }
            if (bestIndex < 0) break;
            word[bestIndex] = word[bestIndex] + word[bestIndex + 1];
            word.RemoveAt(bestIndex + 1);
        }

        string[] result = [.. word];
        _cache[token] = result;
        return result;
    }

    /// <summary>OpenAI's GPT-2 byte-to-unicode reversible mapping.</summary>
    internal static Dictionary<string, string> BytesToUnicode()
    {
        var bs = Enumerable.Range('!', '~' - '!' + 1)
            .Concat(Enumerable.Range('¡', '¬' - '¡' + 1))
            .Concat(Enumerable.Range('®', 'ÿ' - '®' + 1))
            .ToList();
        var cs = new List<int>(bs);
        int n = 0;
        for (int b = 0; b < 256; b++)
        {
            if (!bs.Contains(b))
            {
                bs.Add(b);
                cs.Add(256 + n);
                n++;
            }
        }
        return bs.Zip(cs, (b, c) => (b, c))
            .ToDictionary(x => ((char)x.b).ToString(), x => ((char)x.c).ToString());
    }
}
