using System.IO;
using QL.World.Services;

namespace QL.World.Tests;

public sealed class LibraryRootResolverTests : IDisposable
{
    private readonly string _root;

    public LibraryRootResolverTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    [Fact]
    public void EnsureLibraryLayout_creates_root_and_files_folder_idempotently()
    {
        LibraryRootResolver.EnsureLibraryLayout(_root);
        Assert.True(Directory.Exists(_root));
        Assert.True(Directory.Exists(Path.Combine(_root, "files")));

        // Second call must not throw.
        LibraryRootResolver.EnsureLibraryLayout(_root);
    }

    [Fact]
    public void Physical_path_uses_two_level_hash_sharding_and_entry_code_suffix()
    {
        string path = LibraryRootResolver.GetPhysicalPath(
            _root, "a1b2c3d4e5", "My Photo.png", "20261009-a1b2c3-001");

        string expected = Path.Combine(
            _root, "files", "a", "a1b2", "My Photo_20261009-a1b2c3-001.png");
        Assert.Equal(expected, path);
    }

    [Fact]
    public void Category_hardlink_path_lives_under_category_folder_with_entry_code()
    {
        string path = LibraryRootResolver.GetCategoryHardlinkPath(
            _root, "角色", "hero.JPG", "20261009-a1b2c3-001");

        string expected = Path.Combine(
            _root, "角色", "hero_20261009-a1b2c3-001.jpg");
        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Null_or_blank_category_resolves_to_uncategorized(string? category)
    {
        string dir = LibraryRootResolver.GetCategoryDirectory(_root, category);
        Assert.EndsWith(LibraryRootResolver.UncategorizedCategoryName, dir);

        string path = LibraryRootResolver.GetCategoryHardlinkPath(
            _root, category, "a.png", "20261009-a1b2c3-001");
        Assert.Contains(LibraryRootResolver.UncategorizedCategoryName, path);
    }

    [Theory]
    [InlineData("..", "未分类")]
    [InlineData("...", "未分类")]
    [InlineData(@"a\b/c", "a_b_c")]
    [InlineData("bad:name?", "bad_name_")]
    public void SanitizeCategoryName_neutralizes_traversal_and_invalid_chars(
        string raw, string expected)
    {
        Assert.Equal(expected, LibraryRootResolver.SanitizeCategoryName(raw));
    }

    [Fact]
    public void BuildStoredFileName_handles_extension_only_and_dotted_stems()
    {
        Assert.Equal("image_20261009-a1b2c3-001.png",
            LibraryRootResolver.BuildStoredFileName(".png", "20261009-a1b2c3-001"));
        Assert.Equal("A.B_20261009-a1b2c3-001.jpg",
            LibraryRootResolver.BuildStoredFileName("A.B.JPG", "20261009-a1b2c3-001"));
        Assert.Equal("image_20261009-a1b2c3-001",
            LibraryRootResolver.BuildStoredFileName("", "20261009-a1b2c3-001"));
    }

    [Fact]
    public void ValidateSameVolume_accepts_same_volume_case_insensitive()
    {
        var (ok, error) = LibraryRootResolver.ValidateSameVolume(
            @"c:\lib\pictures", @"C:\appdata\stellar");
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateSameVolume_rejects_cross_volume()
    {
        var (ok, error) = LibraryRootResolver.ValidateSameVolume(
            @"D:\lib\pictures", @"C:\appdata\stellar");
        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("盘符", error);
    }

    [Theory]
    [InlineData("", @"C:\data")]
    [InlineData(@"C:\lib", "")]
    public void ValidateSameVolume_rejects_blank_inputs(string libraryRoot, string dataRoot)
    {
        var (ok, _) = LibraryRootResolver.ValidateSameVolume(libraryRoot, dataRoot);
        Assert.False(ok);
    }

    [Fact]
    public void Short_hash_throws_for_sharding()
    {
        Assert.Throws<ArgumentException>(() =>
            LibraryRootResolver.GetPhysicalPath(_root, "abc", "a.png", "code-1"));
    }
}
