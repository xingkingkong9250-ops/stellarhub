using System.IO;
using System.Linq;
using QL.World.Services.Ai.Local;

namespace QL.World.Tests.Ai.Local;

/// <summary>
/// BPE tokenizer behavior + embedded-resource distribution. The vocab/merges
/// resources live in QL.World.dll; EnsureDistributed unpacks them into a temp
/// models directory for the test.
/// </summary>
public sealed class ClipTokenizerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));

    public ClipTokenizerTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }
    }

    private string ModelsDir
    {
        get
        {
            Directory.CreateDirectory(Path.Combine(_directory, "models"));
            return Path.Combine(_directory, "models");
        }
    }

    private ClipTokenizer Create()
    {
        (string vocab, string merges) = ClipTokenizer.EnsureDistributed(ModelsDir);
        return new ClipTokenizer(vocab, merges);
    }

    [Fact]
    public void EnsureDistributed_extracts_both_files_and_is_idempotent()
    {
        (string vocabPath, string mergesPath) = ClipTokenizer.EnsureDistributed(ModelsDir);

        Assert.True(File.Exists(vocabPath));
        Assert.True(File.Exists(mergesPath));
        long vocabSize = new FileInfo(vocabPath).Length;
        long mergesSize = new FileInfo(mergesPath).Length;
        Assert.True(vocabSize > 100_000, $"vocab.json unexpectedly small: {vocabSize}");
        Assert.True(mergesSize > 100_000, $"merges.txt unexpectedly small: {mergesSize}");

        // Second call must skip overwrite (touch mtime then re-call, assert unchanged).
        DateTime vocabMtime = File.GetLastWriteTimeUtc(vocabPath);
        Thread.Sleep(20);
        ClipTokenizer.EnsureDistributed(ModelsDir);
        Assert.Equal(vocabMtime, File.GetLastWriteTimeUtc(vocabPath));
    }

    [Fact]
    public void Encode_produces_77_length_with_sot_eot_and_zero_padding()
    {
        ClipTokenizer tokenizer = Create();

        int[] ids = tokenizer.Encode("hello");

        Assert.Equal(77, ids.Length);
        Assert.Equal(49406, ids[0]); // SOT
        Assert.Contains(49407, ids); // EOT somewhere
        int eotIndex = System.Array.IndexOf(ids, 49407);
        Assert.True(eotIndex >= 1);
        Assert.All(ids[(eotIndex + 1)..], id => Assert.Equal(0, id));
    }

    [Fact]
    public void Encode_known_prompt_has_bpe_subword_count_not_character_count()
    {
        // Spike-verified sanity: "a seamless texture map pattern" ≈ 8 content
        // tokens. The broken BytesToUnicode port produced 26 (per-character).
        ClipTokenizer tokenizer = Create();

        int[] ids = tokenizer.Encode("a seamless texture map pattern");
        int contentTokens = ids.Count(id => id is not (0 or 49406 or 49407));

        Assert.InRange(contentTokens, 6, 10);
    }

    [Fact]
    public void Encode_too_long_text_throws()
    {
        ClipTokenizer tokenizer = Create();
        // Each English word adds several tokens; 200 words comfortably exceeds 77.
        string longText = string.Concat(Enumerable.Repeat("texture ", 200));

        Assert.Throws<ArgumentException>(() => tokenizer.Encode(longText));
    }

    [Fact]
    public void ByteEncoder_maps_printable_ascii_letters_to_themselves()
    {
        // Regression guard for the M3-D spike BytesToUnicode trap:
        // 'a' (0x61) must map to "a", not to "ģ" (U+0123, id 223).
        var map = ClipTokenizer.BytesToUnicode();

        Assert.Equal("a", map["a"]);
        Assert.Equal("z", map["z"]);
        Assert.Equal("A", map["A"]);
        Assert.Equal("~", map["~"]);
        Assert.Equal("!", map["!"]);
    }

    [Fact]
    public void TokenStrings_hello_is_not_split_per_character()
    {
        // "hello" is a single BPE merge in CLIP vocab; a working merge table
        // yields 1-2 parts, a broken one yields 5 (one per character).
        ClipTokenizer tokenizer = Create();

        var parts = tokenizer.TokenStrings("hello");

        Assert.InRange(parts.Count, 1, 2);
    }
}
