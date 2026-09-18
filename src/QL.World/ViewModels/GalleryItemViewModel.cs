using System.Windows.Media.Imaging;
using QL.World.Helpers;
using QL.World.Models;

namespace QL.World.ViewModels;

/// <summary>One row in the virtualized B2 gallery.</summary>
public sealed class GalleryItemViewModel : ObservableObject
{
    private BitmapSource? _thumbnailImage;
    private int _rating;

    public GalleryItemViewModel(GalleryListItem item)
    {
        Id = item.Id;
        FileName = item.FileName;
        FilePath = item.FilePath;
        FileHash = item.FileHash;
        FileSize = item.FileSize;
        Width = item.Width;
        Height = item.Height;
        Format = item.Format;
        _rating = item.Rating;
        Category = item.PrimaryCategory;
        IsMissing = item.IsMissing;
        CreatedAt = item.CreatedAt;
        TagsCsv = item.TagsCsv;
    }

    public long Id { get; }
    public string FileName { get; }
    public string FilePath { get; }
    public string FileHash { get; }
    public long FileSize { get; }
    public int? Width { get; }
    public int? Height { get; }
    public string? Format { get; }
    public string? Category { get; private set; }
    public bool IsMissing { get; }
    public DateTime CreatedAt { get; }
    public string TagsCsv { get; private set; }

    public int Rating
    {
        get => _rating;
        private set => SetProperty(ref _rating, value);
    }

    public BitmapSource? ThumbnailImage
    {
        get => _thumbnailImage;
        set => SetProperty(ref _thumbnailImage, value);
    }

    public string DimensionsText =>
        Width is int w && Height is int h ? $"{w} × {h}" : "—";

    public string FileSizeText => FileSize switch
    {
        >= 1024L * 1024 * 1024 => $"{FileSize / (1024.0 * 1024 * 1024):0.##} GB",
        >= 1024L * 1024 => $"{FileSize / (1024.0 * 1024):0.#} MB",
        >= 1024L => $"{FileSize / 1024.0:0.#} KB",
        _ => $"{FileSize} B",
    };

    public string CategoryText => string.IsNullOrEmpty(Category) ? "未分类" : Category;
}
