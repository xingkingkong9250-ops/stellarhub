namespace QL.World.ViewModels.Support;

/// <summary>
/// v0.15.0: 画廊卡片实际显示宽度（物理像素）的全局通道。
/// 虚拟化面板（网格/瀑布流）在 MeasureOverride 计算出自适应列宽后上报；
/// <see cref="GalleryThumbnailProvider"/> 据此决定卡片应使用 256px Thumb 档
/// 还是 1024px Preview 档，避免窗口放大后小图被拉伸模糊。
/// 静态类是因为面板（Views 层）与 Provider（ViewModels 层）之间没有直接引用；
/// 上报值取最近一次（同一时刻只有一种画廊布局可见）。
/// </summary>
public static class GalleryDisplayMetrics
{
    /// <summary>Thumb 档（256px）允许的最大上采样倍数，超过则升级到 Preview 档。</summary>
    public const double ThumbMaxUpscale = 1.25;

    /// <summary>Thumb 档边长（与 ThumbnailService 的 PLACEHOLDER/Thumb 尺寸对应）。</summary>
    public const int ThumbPixels = 256;

    /// <summary>低于该物理宽度的上报视为未知/忽略（布局早期 ActualWidth 可能为 0）。</summary>
    private const double MinReportWidth = 1;

    /// <summary>宽度变化超过该物理像素数才刷新并触发 <see cref="Changed"/>（防抖）。</summary>
    private const double ReportDelta = 8;

    private static readonly object Gate = new();
    private static double _itemWidthPx;

    /// <summary>当前卡片的显示宽度（物理像素）；0 表示尚未上报。</summary>
    public static double ItemWidthPx
    {
        get { lock (Gate) return _itemWidthPx; }
    }

    /// <summary>宽度发生有意义变化时触发（UI 线程，面板 Measure 中调用）。</summary>
    public static event Action? Changed;

    /// <summary>判断给定显示宽度是否需要 Preview（1024px）档。</summary>
    public static bool ShouldUseHighRes(double itemWidthPx) =>
        itemWidthPx > ThumbPixels * ThumbMaxUpscale;

    /// <summary>当前是否需要高分辨率档。</summary>
    public static bool NeedsHighRes => ShouldUseHighRes(ItemWidthPx);

    /// <summary>
    /// 由面板上报当前卡片逻辑宽度与 DPI 缩放。仅在变化超过阈值时更新并通知。
    /// </summary>
    public static void ReportItemWidth(double widthLogicalPx, double dpiScaleX)
    {
        if (widthLogicalPx < MinReportWidth || dpiScaleX <= 0)
            return;

        double px = widthLogicalPx * dpiScaleX;
        bool changed;
        lock (Gate)
        {
            changed = Math.Abs(px - _itemWidthPx) >= ReportDelta;
            if (changed)
                _itemWidthPx = px;
        }
        if (changed)
            Changed?.Invoke();
    }

    /// <summary>测试用：重置为未上报状态。</summary>
    public static void Reset()
    {
        lock (Gate)
            _itemWidthPx = 0;
    }
}
