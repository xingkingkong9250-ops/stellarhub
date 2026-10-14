using QL.World.ViewModels.Support;

namespace QL.World.Tests;

public sealed class GalleryDisplayMetricsTests : IDisposable
{
    public GalleryDisplayMetricsTests() => GalleryDisplayMetrics.Reset();

    public void Dispose() => GalleryDisplayMetrics.Reset();

    [Fact]
    public void ShouldUseHighRes_ThresholdIs125PercentOfThumb()
    {
        // 256 * 1.25 = 320：恰好 320 不升级，超过才升级。
        Assert.False(GalleryDisplayMetrics.ShouldUseHighRes(320));
        Assert.True(GalleryDisplayMetrics.ShouldUseHighRes(321));
        Assert.False(GalleryDisplayMetrics.ShouldUseHighRes(0));
    }

    [Fact]
    public void ReportItemWidth_ConvertsLogicalToPhysicalPixels()
    {
        GalleryDisplayMetrics.ReportItemWidth(200, 2.0);
        Assert.Equal(400, GalleryDisplayMetrics.ItemWidthPx);
        Assert.True(GalleryDisplayMetrics.NeedsHighRes);
    }

    [Fact]
    public void ReportItemWidth_SmallWidthKeepsThumbTier()
    {
        GalleryDisplayMetrics.ReportItemWidth(240, 1.0);
        Assert.False(GalleryDisplayMetrics.NeedsHighRes);
    }

    [Fact]
    public void ReportItemWidth_IgnoresZeroWidthAndInvalidDpi()
    {
        GalleryDisplayMetrics.ReportItemWidth(0, 1.0);
        GalleryDisplayMetrics.ReportItemWidth(300, 0);
        Assert.Equal(0, GalleryDisplayMetrics.ItemWidthPx);
    }

    [Fact]
    public void ReportItemWidth_RaisesChangedOnlyBeyondDelta()
    {
        int fired = 0;
        void Handler() => fired++;
        GalleryDisplayMetrics.Changed += Handler;
        try
        {
            GalleryDisplayMetrics.ReportItemWidth(400, 1.0);   // 0 → 400：触发
            GalleryDisplayMetrics.ReportItemWidth(404, 1.0);   // 变化 4px < 8：不触发
            GalleryDisplayMetrics.ReportItemWidth(420, 1.0);   // 变化 20px：触发
            Assert.Equal(2, fired);
            Assert.Equal(420, GalleryDisplayMetrics.ItemWidthPx);
        }
        finally
        {
            GalleryDisplayMetrics.Changed -= Handler;
        }
    }
}
