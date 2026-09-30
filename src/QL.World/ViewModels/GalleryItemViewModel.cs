using System.Windows.Media.Imaging;
using QL.World.Helpers;
using QL.World.Models;

namespace QL.World.ViewModels;

/// <summary>One row in the virtualized B2 gallery.</summary>
public sealed class GalleryItemViewModel : ObservableObject
{
    private BitmapSource? _thumbnailImage;
    private BitmapSource? _previewImage;
    private int _rating;
    private bool _canSubmitToShared;

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
        SourceBadge = item.SourceBadge;
        Source = item.Source;
        NasConnectionId = item.NasConnectionId;
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

    /// <summary>U2-D (§4.C.2): "本地"/"云端"/"AI" or null — drives B2-3-OL1 source badge.</summary>
    public string? SourceBadge { get; }

    /// <summary>
    /// M5-E: <see cref="ImageSource.Local"/> or <see cref="ImageSource.Remote"/>.
    /// <see cref="GalleryThumbnailProvider"/> builds the matching
    /// <c>IImageSource</c> from this so <c>ThumbnailService</c> can fetch
    /// the original from a NAS when the local gallery row points at a
    /// remote <c>file_path</c>.
    /// </summary>
    public ImageSource Source { get; }

    /// <summary>
    /// M5-E: NAS connection id when <see cref="Source"/> is
    /// <see cref="ImageSource.Remote"/>; <c>null</c> otherwise. Used by
    /// <see cref="GalleryThumbnailProvider"/> to look up the cached
    /// <see cref="INasConnector"/>.
    /// </summary>
    public int? NasConnectionId { get; }

    /// <summary>
    /// U6-B: true when the shared library is online and this row is a local
    /// file eligible for 「提交到共享库」. Drives the context-menu item.
    /// </summary>
    public bool CanSubmitToShared
    {
        get => _canSubmitToShared;
        set => SetProperty(ref _canSubmitToShared, value);
    }

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

    /// <summary>
    /// M5-F: 1024px preview-tier WebP decoded to a frozen BitmapSource.
    /// Set by <see cref="Support.GalleryThumbnailProvider"/> when the Preview
    /// level WebP arrives. Bound by <see cref="Views.Controls.ProgressiveImage"/>
    /// as the middle layer (fades in over the 256px thumb).
    /// </summary>
    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        set => SetProperty(ref _previewImage, value);
    }

    public string DimensionsText =>
        Width is int w && Height is int h ? $"{w} × {h}" : "—";

    /// <summary>
    /// U2-E (§4.D.1): first 2 tags joined by 「, 」 plus 「+N」 when there are more.
    /// Empty string when there are no tags (XAML hides the row via ZeroVis).
    /// </summary>
    public string TagsSummary
    {
        get
        {
            if (string.IsNullOrEmpty(TagsCsv)) return string.Empty;
            string[] tags = TagsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (tags.Length == 0) return string.Empty;
            int take = Math.Min(2, tags.Length);
            string first = string.Join(", ", tags.Take(take));
            return tags.Length > 2 ? $"{first} +{tags.Length - 2}" : first;
        }
    }

    public string FileSizeText => FileSize switch
    {
        >= 1024L * 1024 * 1024 => $"{FileSize / (1024.0 * 1024 * 1024):0.##} GB",
        >= 1024L * 1024 => $"{FileSize / (1024.0 * 1024):0.#} MB",
        >= 1024L => $"{FileSize / 1024.0:0.#} KB",
        _ => $"{FileSize} B",
    };

    public string CategoryText => string.IsNullOrEmpty(Category) ? "未分类" : Category;
}
