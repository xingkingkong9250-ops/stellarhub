using QL.World.Models;
using QL.World.Services;
using QL.World.Tests.Support;

namespace QL.World.Tests;

public sealed class TagServiceTests
{
    [Fact]
    public void Manual_tag_is_never_overwritten_by_ai()
    {
        using var library = new TestLibrary();
        var tags = new TagService(library.Db);
        long imageId = library.InsertImage();

        tags.AddTags([imageId], ["metal"], TagSource.Manual);
        tags.AddTags([imageId], ["metal"], TagSource.Ai, confidence: 0.42);

        var result = tags.GetTagsForImage(imageId);
        Assert.Single(result);
        Assert.Equal(TagSource.Manual, result[0].Source);
        Assert.Null(result[0].Confidence);
    }

    [Fact]
    public void Ai_tag_can_be_upgraded_to_manual()
    {
        using var library = new TestLibrary();
        var tags = new TagService(library.Db);
        long imageId = library.InsertImage();

        tags.AddTags([imageId], ["rust"], TagSource.Ai, confidence: 0.9);
        tags.AddTags([imageId], ["rust"], TagSource.Manual);

        var result = tags.GetTagsForImage(imageId);
        Assert.Single(result);
        Assert.Equal(TagSource.Manual, result[0].Source);
        Assert.Null(result[0].Confidence);
    }

    [Fact]
    public void Pasted_multi_tag_string_is_split_and_trimmed()
    {
        using var library = new TestLibrary();
        var tags = new TagService(library.Db);
        long imageId = library.InsertImage();

        int created = tags.AddTags(
            [imageId],
            ["metal, rust，old、dark; worn"],
            TagSource.Manual);

        Assert.Equal(5, created);
        Assert.Equal(5, tags.GetTagsForImage(imageId).Count);
    }

    [Fact]
    public void Add_to_multiple_images_and_remove_one()
    {
        using var library = new TestLibrary();
        var tags = new TagService(library.Db);
        long a = library.InsertImage();
        long b = library.InsertImage();

        tags.AddTags([a, b], ["sharp"], TagSource.Ai, confidence: 0.8);
        tags.RemoveTag(a, "sharp");

        Assert.Empty(tags.GetTagsForImage(a));
        Assert.Single(tags.GetTagsForImage(b));
    }

    [Fact]
    public void Top_tags_orders_by_frequency_and_global_delete_counts()
    {
        using var library = new TestLibrary();
        var tags = new TagService(library.Db);
        long a = library.InsertImage();
        long b = library.InsertImage();
        long c = library.InsertImage();

        tags.AddTags([a, b, c], ["metal"], TagSource.Manual);
        tags.AddTags([a, b], ["rust"], TagSource.Manual);
        tags.AddTags([c], ["wood"], TagSource.Manual);

        var top = tags.GetTopTags(50);
        Assert.Equal("metal", top[0].Tag);
        Assert.Equal(3, top[0].Count);

        int affected = tags.DeleteTagEverywhere("metal");
        Assert.Equal(3, affected);
        Assert.DoesNotContain(tags.GetTopTags(50), t => t.Tag == "metal");
    }

    [Fact]
    public void Blank_tag_input_is_ignored_not_stored()
    {
        using var library = new TestLibrary();
        var tags = new TagService(library.Db);
        long imageId = library.InsertImage();

        int created = tags.AddTags([imageId], ["   "], TagSource.Manual);

        Assert.Equal(0, created);
        Assert.Empty(tags.GetTagsForImage(imageId));
    }
}
