using QL.World.Models;
using QL.World.Services;
using QL.World.Tests.Support;

namespace QL.World.Tests;

/// <summary>
/// M11-E: W7「跳过」软跳过过滤 — ImageQueryService 谓词为 true 时，
/// 画廊查询只返回 library_path 非空（已托管）的行；CountFiltered 同步。
/// 行由 TestLibrary.InsertImage 创建（v9 新列默认 NULL，模拟未迁移老行）。
/// </summary>
public sealed class ImageQueryServiceLegacyFilterTests
{
    private static LibraryFilter EmptyFilter => new();

    [Fact]
    public void Hidden_predicate_excludes_unmanaged_rows_from_gallery_and_count()
    {
        using var library = new TestLibrary();
        long legacyId = library.InsertImage();
        long managedId = library.InsertImage();
        library.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE images
                SET library_path = file_path, entry_code = '20261009-abcdef-001'
                WHERE id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", managedId);
            cmd.ExecuteNonQuery();
        });

        var hidden = new ImageQueryService(library.Db, () => true);

        IReadOnlyList<GalleryListItem> rows = hidden.QueryGallery(EmptyFilter);
        Assert.Single(rows);
        Assert.Equal(managedId, rows[0].Id);
        Assert.Equal(1, hidden.CountFiltered(EmptyFilter));
    }

    [Fact]
    public void Predicate_false_shows_everything_like_default()
    {
        using var library = new TestLibrary();
        library.InsertImage();
        long managedId = library.InsertImage();
        library.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE images SET library_path = file_path WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", managedId);
            cmd.ExecuteNonQuery();
        });

        var visible = new ImageQueryService(library.Db, () => false);
        Assert.Equal(2, visible.QueryGallery(EmptyFilter).Count);
        Assert.Equal(2, visible.CountFiltered(EmptyFilter));

        // 无谓语构造（既有调用点）行为不变。
        var unguarded = new ImageQueryService(library.Db);
        Assert.Equal(2, unguarded.QueryGallery(EmptyFilter).Count);
    }

    [Fact]
    public void Dynamic_predicate_reacts_to_setting_flip()
    {
        using var library = new TestLibrary();
        library.InsertImage();
        long managedId = library.InsertImage();
        library.Db.Run(conn =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE images SET library_path = file_path WHERE id = $id;";
            cmd.Parameters.AddWithValue("$id", managedId);
            cmd.ExecuteNonQuery();
        });

        bool hideLegacy = false;
        var query = new ImageQueryService(library.Db, () => hideLegacy);

        Assert.Equal(2, query.QueryGallery(EmptyFilter).Count);
        hideLegacy = true;
        Assert.Single(query.QueryGallery(EmptyFilter));
        hideLegacy = false;
        Assert.Equal(2, query.QueryGallery(EmptyFilter).Count);
    }
}
