using QL.World.Models;
using QL.World.ViewModels;

namespace QL.World.Tests;

public sealed class GalleryItemViewModelTests
{
    [Fact]
    public void TagsSummary_returns_empty_when_no_tags()
    {
        var vm = new GalleryItemViewModel(new GalleryListItem { TagsCsv = "" });
        Assert.Equal(string.Empty, vm.TagsSummary);
    }

    [Fact]
    public void TagsSummary_shows_single_tag()
    {
        var vm = new GalleryItemViewModel(new GalleryListItem { TagsCsv = "metal" });
        Assert.Equal("metal", vm.TagsSummary);
    }

    [Fact]
    public void TagsSummary_shows_two_tags_joined()
    {
        var vm = new GalleryItemViewModel(new GalleryListItem { TagsCsv = "metal,rust" });
        Assert.Equal("metal, rust", vm.TagsSummary);
    }

    [Fact]
    public void TagsSummary_truncates_beyond_two_with_plus_count()
    {
        var vm = new GalleryItemViewModel(new GalleryListItem { TagsCsv = "metal,rust,iron,steel" });
        Assert.Equal("metal, rust +2", vm.TagsSummary);
    }
}
