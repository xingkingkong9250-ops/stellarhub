using System.Windows.Media.Imaging;
using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;

namespace QL.World.ViewModels;

/// <summary>
/// 0.16.0 DUP-D: W8 组详情里的一张图片卡片。承载展示格式化、选中态、
/// 「推荐保留」标记与异步加载的缩略图。
/// </summary>
public sealed class DuplicateMemberViewModel : ObservableObject
{
    private BitmapSource? _thumbnailImage;
    private bool _isSelected;

    public DuplicateMemberViewModel(DuplicateMemberRecord record, bool isRecommended)
    {
        ArgumentNullException.ThrowIfNull(record);
        Record = record;
        IsRecommended = isRecommended;
    }

    public DuplicateMemberRecord Record { get; }

    public long ImageId => Record.ImageId;
    public string FileName => Record.FileName;
    public string FilePath => Record.FilePath;
    public string FileHash => Record.FileHash;
    public long FileSize => Record.FileSize;
    public int? Width => Record.Width;
    public int? Height => Record.Height;
    public DateTime CreatedAt => Record.CreatedAt;
    public ImageSource Source => Record.Source;
    public int? NasConnectionId => Record.NasConnectionId;
    public bool IsRecommended { get; }

    public string DimensionsText =>
        Width is int w && Height is int h ? $"{w} × {h}" : "—";

    public string FileSizeText => FileSize switch
    {
        >= 1024L * 1024 * 1024 => $"{FileSize / (1024.0 * 1024 * 1024):0.##} GB",
        >= 1024L * 1024 => $"{FileSize / (1024.0 * 1024):0.#} MB",
        >= 1024L => $"{FileSize / 1024.0:0.#} KB",
        _ => $"{FileSize} B",
    };

    public string CreatedAtText => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd");

    public string LevelText => Record.Level;

    public string KindText => Record.Kind == "exact" ? "严格同图" : "近似变体";

    /// <summary>L1 分数为汉明距离倒数，保留两位小数即可。</summary>
    public string ScoreText => Record.Score.ToString("0.00");

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public BitmapSource? ThumbnailImage
    {
        get => _thumbnailImage;
        set => SetProperty(ref _thumbnailImage, value);
    }
}
