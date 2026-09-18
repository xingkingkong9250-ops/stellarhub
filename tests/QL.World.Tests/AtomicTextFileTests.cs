using System.IO;
using QL.World.Services;

namespace QL.World.Tests;

public sealed class AtomicTextFileTests : IDisposable
{
    private readonly string _directory;

    public AtomicTextFileTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "ql-world-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* Temp cleanup is best effort. */ }
    }

    [Fact]
    public void First_write_creates_file_and_initial_backup()
    {
        string path = Path.Combine(_directory, "config.json");

        AtomicTextFile.WriteAllText(path, "{\"a\":1}");

        Assert.True(File.Exists(path));
        Assert.True(File.Exists(path + ".backup"));
        Assert.Equal("{\"a\":1}", File.ReadAllText(path));
    }

    [Fact]
    public void Second_write_keeps_previous_content_as_backup()
    {
        string path = Path.Combine(_directory, "config.json");

        AtomicTextFile.WriteAllText(path, "first");
        AtomicTextFile.WriteAllText(path, "second");

        Assert.Equal("second", File.ReadAllText(path));
        Assert.Equal("first", File.ReadAllText(path + ".backup"));
        Assert.Empty(Directory.GetFiles(_directory, "*.partial"));
    }

    [Fact]
    public void Restore_backup_reinstates_last_known_good_snapshot()
    {
        string path = Path.Combine(_directory, "config.json");

        AtomicTextFile.WriteAllText(path, "first");
        AtomicTextFile.WriteAllText(path, "second");
        AtomicTextFile.RestoreBackup(path);

        Assert.Equal("first", File.ReadAllText(path));
    }

    [Fact]
    public void Restore_throws_when_no_backup_exists()
    {
        string path = Path.Combine(_directory, "missing.json");

        Assert.Throws<FileNotFoundException>(() => AtomicTextFile.RestoreBackup(path));
    }
}
