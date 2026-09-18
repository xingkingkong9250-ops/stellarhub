using QL.World.Services;
using QL.World.Tests.Support;

namespace QL.World.Tests;

public sealed class CollectionServiceTests : IDisposable
{
    private readonly TestLibrary _lib = new();
    private readonly CollectionService _collections;

    public CollectionServiceTests() => _collections = new CollectionService(_lib.Db);

    [Fact]
    public void Create_returns_id_and_getall_lists_with_zero_count()
    {
        long id = _collections.Create("角色三视图");
        Assert.True(id > 0);

        var all = _collections.GetAll();
        Assert.Single(all);
        Assert.Equal("角色三视图", all[0].Name);
        Assert.Equal(0, all[0].ImageCount);
    }

    [Fact]
    public void Rename_updates_name()
    {
        long id = _collections.Create("Old");
        _collections.Rename(id, "New Name");
        var all = _collections.GetAll();
        Assert.Equal("New Name", all[0].Name);
    }

    [Fact]
    public void Rename_throws_on_duplicate_name()
    {
        _collections.Create("Duplicate");
        long id2 = _collections.Create("Other");
        Assert.ThrowsAny<Exception>(() => _collections.Rename(id2, "Duplicate"));
    }

    [Fact]
    public void Delete_removes_collection_and_cascades_collection_images()
    {
        long imgId = _lib.InsertImage();
        long colId = _collections.Create("To Delete");
        _collections.AddImages(colId, [imgId]);
        Assert.Equal(1, _collections.GetImageCount(colId));

        _collections.Delete(colId);
        Assert.Empty(_collections.GetAll());
    }

    [Fact]
    public void AddImages_is_idempotent_for_existing_pair_and_advances_sort_order()
    {
        long img1 = _lib.InsertImage();
        long img2 = _lib.InsertImage();
        long colId = _collections.Create("Test");

        _collections.AddImages(colId, [img1, img2]);
        Assert.Equal(2, _collections.GetImageCount(colId));

        // Re-adding should not duplicate.
        _collections.AddImages(colId, [img1]);
        Assert.Equal(2, _collections.GetImageCount(colId));

        var ids = _collections.GetImageIds(colId);
        Assert.Equal(img1, ids[0]);
        Assert.Equal(img2, ids[1]);
    }

    [Fact]
    public void RemoveImage_drops_single_pair_only()
    {
        long img1 = _lib.InsertImage();
        long img2 = _lib.InsertImage();
        long colId = _collections.Create("Test");
        _collections.AddImages(colId, [img1, img2]);

        _collections.RemoveImage(colId, img1);
        Assert.Equal(1, _collections.GetImageCount(colId));
        var ids = _collections.GetImageIds(colId);
        Assert.Equal(img2, ids[0]);
    }

    [Fact]
    public void Reorder_persists_new_sort_order()
    {
        long img1 = _lib.InsertImage();
        long img2 = _lib.InsertImage();
        long img3 = _lib.InsertImage();
        long colId = _collections.Create("Test");
        _collections.AddImages(colId, [img1, img2, img3]);

        // Reverse the order.
        _collections.Reorder(colId, [img3, img2, img1]);

        var ids = _collections.GetImageIds(colId);
        Assert.Equal(img3, ids[0]);
        Assert.Equal(img2, ids[1]);
        Assert.Equal(img1, ids[2]);
    }

    [Fact]
    public void SetCover_updates_cover_image_id_and_accepts_null_to_clear()
    {
        long imgId = _lib.InsertImage();
        long colId = _collections.Create("Cover Test");

        _collections.SetCover(colId, imgId);
        Assert.Equal(imgId, _collections.GetAll()[0].CoverImageId);

        _collections.SetCover(colId, null);
        Assert.Null(_collections.GetAll()[0].CoverImageId);
    }

    [Fact]
    public void GetCoverHash_returns_hash_for_cover_image()
    {
        long imgId = _lib.InsertImage(hash: "abc123");
        long colId = _collections.Create("Cover Hash Test");
        _collections.SetCover(colId, imgId);

        Assert.Equal("abc123", _collections.GetCoverHash(colId));
    }

    [Fact]
    public void GetCoverHash_returns_null_when_unset()
    {
        long colId = _collections.Create("No Cover");
        Assert.Null(_collections.GetCoverHash(colId));
    }

    public void Dispose() => _lib.Dispose();
}
