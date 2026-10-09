using QL.World.Helpers;
using QL.World.Models;
using QL.World.Services;

namespace QL.World.ViewModels;

/// <summary>
/// B1-5 attribute filter group — 分辨率 / 大小 / 评分（v0.13.4 起三维；
/// 原「颜色」维度与 B1-6-3 色彩倾向色板重叠，已合并，B1-5-1 编号作废）。
/// Each dimension is a single-select chip row with 「全部」; selection writes
/// Gallery.Filter and refreshes. The section's expanded state persists in
/// settings (default collapsed).
/// </summary>
public sealed class AttributeFilterViewModel : ObservableObject
{
    private readonly GalleryViewModel _gallery;
    private readonly AppSettingsService _settings;

    public AttributeFilterViewModel(GalleryViewModel gallery, AppSettingsService settings)
    {
        _gallery = gallery;
        _settings = settings;

        ResolutionOptions = BuildOptions(
        [
            ("全部", null),
            ("<1024", ResolutionBuckets.Lt1024),
            ("1024–2047", ResolutionBuckets.From1024To2047),
            ("2048–4095", ResolutionBuckets.From2048To4095),
            ("≥4096", ResolutionBuckets.Ge4096),
        ], v => ApplyResolution(v));
        FileSizeOptions = BuildOptions(
        [
            ("全部", null),
            ("<1 MB", FileSizeBuckets.Lt1Mb),
            ("1–5 MB", FileSizeBuckets.From1To5Mb),
            ("5–20 MB", FileSizeBuckets.From5To20Mb),
            ("≥20 MB", FileSizeBuckets.Ge20Mb),
        ], v => ApplyFileSize(v));
        RatingOptions = BuildOptions(
        [
            ("全部", null),
            ("未评分", RatingBuckets.Unrated),
            ("≥3 ★", RatingBuckets.Ge3),
            ("≥4 ★", RatingBuckets.Ge4),
            ("5 ★", RatingBuckets.Eq5),
        ], v => ApplyRating(v));

        SyncActiveStates();
    }

    public IReadOnlyList<AttributeFilterOption> ResolutionOptions { get; }
    public IReadOnlyList<AttributeFilterOption> FileSizeOptions { get; }
    public IReadOnlyList<AttributeFilterOption> RatingOptions { get; }

    /// <summary>B1-5 section expanded state; persisted, default collapsed.</summary>
    public bool IsExpanded
    {
        get => _settings.SidebarAttributeFilterExpanded;
        set
        {
            if (_settings.SidebarAttributeFilterExpanded != value)
            {
                _settings.SidebarAttributeFilterExpanded = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>Re-syncs chip active states from the filter (清除全部筛选).</summary>
    public void ResetSelection() => SyncActiveStates();

    private static IReadOnlyList<AttributeFilterOption> BuildOptions(
        IReadOnlyList<(string Display, string? Value)> defs,
        Action<string?> apply) =>
        [.. defs.Select(d => new AttributeFilterOption(d.Display, d.Value, apply))];

    // Clicking the active non-全部 chip again clears that dimension.
    private void ApplyResolution(string? value)
    {
        _gallery.Filter.ResolutionBucket =
            value is not null && _gallery.Filter.ResolutionBucket == value ? null : value;
        SyncActiveStates();
        _ = _gallery.RefreshAsync();
    }

    private void ApplyFileSize(string? value)
    {
        _gallery.Filter.FileSizeBucket =
            value is not null && _gallery.Filter.FileSizeBucket == value ? null : value;
        SyncActiveStates();
        _ = _gallery.RefreshAsync();
    }

    private void ApplyRating(string? value)
    {
        _gallery.Filter.RatingBucket =
            value is not null && _gallery.Filter.RatingBucket == value ? null : value;
        SyncActiveStates();
        _ = _gallery.RefreshAsync();
    }

    private void SyncActiveStates()
    {
        foreach (AttributeFilterOption o in ResolutionOptions)
            o.IsActive = o.Value == _gallery.Filter.ResolutionBucket;
        foreach (AttributeFilterOption o in FileSizeOptions)
            o.IsActive = o.Value == _gallery.Filter.FileSizeBucket;
        foreach (AttributeFilterOption o in RatingOptions)
            o.IsActive = o.Value == _gallery.Filter.RatingBucket;
    }
}

/// <summary>One chip in an attribute filter dimension.</summary>
public sealed class AttributeFilterOption : ObservableObject
{
    private bool _isActive;

    public AttributeFilterOption(string display, string? value, Action<string?> apply)
    {
        Display = display;
        Value = value;
        SelectCommand = new RelayCommand(() => apply(value));
    }

    public string Display { get; }

    /// <summary>Filter value; null = 「全部」 (dimension inactive).</summary>
    public string? Value { get; }

    public RelayCommand SelectCommand { get; }

    public bool IsActive
    {
        get => _isActive;
        set => SetProperty(ref _isActive, value);
    }
}

/// <summary>Filter-value → display-string mapping for the B2-1 filter summary.</summary>
internal static class AttributeFilterDisplay
{
    public static string Resolution(string bucket) => bucket switch
    {
        ResolutionBuckets.Lt1024 => "<1024",
        ResolutionBuckets.From1024To2047 => "1024–2047",
        ResolutionBuckets.From2048To4095 => "2048–4095",
        ResolutionBuckets.Ge4096 => "≥4096",
        _ => bucket,
    };

    public static string FileSize(string bucket) => bucket switch
    {
        FileSizeBuckets.Lt1Mb => "<1 MB",
        FileSizeBuckets.From1To5Mb => "1–5 MB",
        FileSizeBuckets.From5To20Mb => "5–20 MB",
        FileSizeBuckets.Ge20Mb => "≥20 MB",
        _ => bucket,
    };

    public static string Rating(string bucket) => bucket switch
    {
        RatingBuckets.Unrated => "未评分",
        RatingBuckets.Ge3 => "≥3 ★",
        RatingBuckets.Ge4 => "≥4 ★",
        RatingBuckets.Eq5 => "5 ★",
        _ => bucket,
    };
}
