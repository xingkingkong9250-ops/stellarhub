using System.Windows.Media.Imaging;
using QL.World.Helpers;
using QL.World.Models;

namespace QL.World.ViewModels;

/// <summary>
/// U6-C: one row in the admin review queue (W6). Wraps the immutable
/// <see cref="PendingSubmission"/> plus display-only formatting and the
/// asynchronously loaded thumbnail.
/// </summary>
public sealed class ReviewItemViewModel : ObservableObject
{
    private BitmapSource? _thumbnail;

    public ReviewItemViewModel(PendingSubmission source)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public PendingSubmission Source { get; }

    public string SubmitId => Source.SubmitId;
    public string FileName => Source.FileName;
    public string SubmittedBy => Source.SubmittedBy;

    public string SubmittedAtText =>
        Source.SubmittedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string DimensionsText =>
        Source.Width is int w && Source.Height is int h ? $"{w} × {h}" : "—";

    public string TagsSummary =>
        Source.Tags.Count > 0 ? string.Join(", ", Source.Tags) : "无标签";

    public string FileSizeText => Source.FileSize switch
    {
        >= 1024L * 1024 * 1024 => $"{Source.FileSize / (1024.0 * 1024 * 1024):0.##} GB",
        >= 1024L * 1024 => $"{Source.FileSize / (1024.0 * 1024):0.#} MB",
        >= 1024L => $"{Source.FileSize / 1024.0:0.#} KB",
        _ => $"{Source.FileSize} B",
    };

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set => SetProperty(ref _thumbnail, value);
    }
}
