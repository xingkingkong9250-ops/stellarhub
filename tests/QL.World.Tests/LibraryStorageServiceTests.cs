using System.IO;
using System.Security.Cryptography;
using QL.World.Services;

namespace QL.World.Tests;

public sealed class LibraryStorageServiceTests : IDisposable
{
    private readonly string _libraryRoot;
    private readonly LibraryStorageService _storage;

    public LibraryStorageServiceTests()
    {
        _libraryRoot = Path.Combine(
            Path.GetTempPath(), "ql-world-tests", Guid.NewGuid().ToString("N"));
        _storage = new LibraryStorageService();
    }

    public void Dispose()
    {
        try { Directory.Delete(_libraryRoot, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    private static (string Hash, byte[] Bytes) Payload(string text)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
        using var md5 = MD5.Create();
        return (Convert.ToHexString(md5.ComputeHash(bytes)).ToLowerInvariant(), bytes);
    }

    private static Stream StreamOf(byte[] bytes) => new MemoryStream(bytes, writable: false);

    [Fact]
    public void EnsurePhysical_copies_stream_into_sharded_path()
    {
        var (hash, bytes) = Payload("stellar-hub-m11-c");

        string physical;
        using (Stream src = StreamOf(bytes))
        {
            physical = _storage.EnsurePhysical(
                _libraryRoot, hash, src, "参考图.png", "20261009-a1b2c3-001");
        }

        string expected = LibraryRootResolver.GetPhysicalPath(
            _libraryRoot, hash, "参考图.png", "20261009-a1b2c3-001");
        Assert.Equal(expected, physical);
        Assert.True(File.Exists(physical));
        Assert.Equal(bytes, File.ReadAllBytes(physical));
        // 中文原名 + 扩展名保留在物理层文件名中。
        Assert.Contains("参考图_20261009-a1b2c3-001.png", physical);
    }

    [Fact]
    public void EnsurePhysical_same_hash_is_a_CAS_hit_and_never_overwrites()
    {
        var (hash, original) = Payload("original-bytes");
        using (Stream src = StreamOf(original))
        {
            _storage.EnsurePhysical(_libraryRoot, hash, src, "a.png", "20261009-a1b2c3-001");
        }

        // Same hash claimed by DIFFERENT bytes (e.g. tampered re-import):
        // CAS must trust the hash and skip the copy entirely.
        byte[] tampered = System.Text.Encoding.UTF8.GetBytes("tampered-different-bytes");
        string secondPath;
        using (Stream src = new MemoryStream(tampered))
        {
            long positionBefore = src.Position;
            secondPath = _storage.EnsurePhysical(
                _libraryRoot, hash, src, "a.png", "20261009-a1b2c3-001");
            // Stream untouched on a hit (seek-free for remote streams).
            Assert.Equal(positionBefore, src.Position);
        }

        string expected = LibraryRootResolver.GetPhysicalPath(
            _libraryRoot, hash, "a.png", "20261009-a1b2c3-001");
        Assert.Equal(expected, secondPath);
        Assert.Equal(original, File.ReadAllBytes(secondPath));
    }

    [Fact]
    public void EnsurePhysicalFromFile_round_trips_source_file()
    {
        var (hash, bytes) = Payload("from-disk");
        string source = Path.Combine(_libraryRoot, "_src", "photo.JPG");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, bytes);

        string physical = _storage.EnsurePhysicalFromFile(
            _libraryRoot, hash, source, "20261009-a1b2c3-001");

        Assert.True(File.Exists(physical));
        Assert.Equal(bytes, File.ReadAllBytes(physical));
        Assert.Equal(".jpg", Path.GetExtension(physical));
    }

    [Fact]
    public void Truncated_copy_leaves_no_permanent_file_after_crash()
    {
        var (hash, bytes) = Payload("crash-mid-copy");
        string target = LibraryRootResolver.GetPhysicalPath(
            _libraryRoot, hash, "a.png", "20261009-a1b2c3-001");

        Assert.Throws<IOException>(() =>
            _storage.EnsurePhysical(
                _libraryRoot, hash, new ThrowingStream(), "a.png", "20261009-a1b2c3-001"));

        Assert.False(File.Exists(target));
        // No temp leftovers in the shard dir.
        string shard = Path.GetDirectoryName(target)!;
        Assert.Empty(Directory.Exists(shard)
            ? Directory.GetFiles(shard, ".tmp-*")
            : []);
    }

    [Fact]
    public void CreateCategoryHardlink_shares_mft_record_with_physical_file()
    {
        var (hash, bytes) = Payload("hardlink-me");
        string physical;
        using (Stream src = StreamOf(bytes))
        {
            physical = _storage.EnsurePhysical(
                _libraryRoot, hash, src, "hero.png", "20261009-a1b2c3-001");
        }

        string link = LibraryRootResolver.GetCategoryHardlinkPath(
            _libraryRoot, "角色", "hero.png", "20261009-a1b2c3-001");
        _storage.CreateCategoryHardlink(physical, link);

        Assert.True(File.Exists(link));
        Assert.Equal(bytes, File.ReadAllBytes(link));

        // Prove it is a real hardlink (not a copy): bytes appended through
        // the physical path are immediately visible through the link path.
        File.AppendAllText(physical, "+delta");
        Assert.Equal("hardlink-me+delta", File.ReadAllText(link));
    }

    [Fact]
    public void CreateCategoryHardlink_is_idempotent_when_link_exists()
    {
        var (hash, bytes) = Payload("idempotent-link");
        string physical;
        using (Stream src = StreamOf(bytes))
        {
            physical = _storage.EnsurePhysical(
                _libraryRoot, hash, src, "b.png", "20261009-a1b2c3-002");
        }
        string link = LibraryRootResolver.GetCategoryHardlinkPath(
            _libraryRoot, "场景", "b.png", "20261009-a1b2c3-002");

        _storage.CreateCategoryHardlink(physical, link);
        var ex = Record.Exception(() => _storage.CreateCategoryHardlink(physical, link));
        Assert.Null(ex);
    }

    [Fact]
    public void CreateCategoryHardlink_throws_when_physical_missing()
    {
        string ghost = Path.Combine(_libraryRoot, "files", "z", "zzzz", "ghost_code.png");
        string link = Path.Combine(_libraryRoot, "角色", "ghost_code.png");

        Assert.Throws<FileNotFoundException>(
            () => _storage.CreateCategoryHardlink(ghost, link));
    }

    [Fact]
    public void CreateCategoryHardlink_cross_volume_throws_io_exception()
    {
        // Pick a drive letter that does not exist on this machine — the
        // directory creation / CreateHardLinkW call must surface an
        // IOException either way (ERROR_PATH_NOT_FOUND or
        // ERROR_NOT_SAME_DEVICE on a real second volume).
        string? unavailableDrive = "UVWXYZ"
            .Select(ch => $"{ch}:\\")
            .FirstOrDefault(root => !Directory.Exists(root));
        if (unavailableDrive is null)
            return; // every candidate letter exists on this machine

        var (hash, bytes) = Payload("cross-volume");
        string physical;
        using (Stream src = StreamOf(bytes))
        {
            physical = _storage.EnsurePhysical(
                _libraryRoot, hash, src, "c.png", "20261009-a1b2c3-003");
        }

        string link = Path.Combine(
            unavailableDrive, $"ql-m11c-{Guid.NewGuid():N}", "c_code.png");
        // Directory creation surfaces DirectoryNotFoundException on a
        // nonexistent volume and CreateHardLinkW surfaces IOException with
        // ERROR_NOT_SAME_DEVICE on a real second volume — both derive from
        // IOException and must abort the link step.
        Assert.ThrowsAny<IOException>(() => _storage.CreateCategoryHardlink(physical, link));
    }

    private sealed class ThrowingStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count)
            => throw new IOException("simulated mid-copy failure");

        public override int ReadByte()
            => throw new IOException("simulated mid-copy failure");
    }
}
