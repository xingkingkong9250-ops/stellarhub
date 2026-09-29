namespace QL.World.Services;

/// <summary>
/// M5-F: thumbnail resolution tier. Drives the on-disk suffix
/// (<c>&lt;hash&gt;_thumb.webp</c> / <c>&lt;hash&gt;_preview.webp</c>)
/// and the target pixel size in <see cref="ThumbnailService.ProcessJob"/>.
/// <list type="bullet">
///   <item><see cref="Thumb"/> — 256 px (matches <c>AppSettings.ThumbnailSize</c>); gallery grid + list.</item>
///   <item><see cref="Preview"/> — 1024 px; detail / large preview pane.</item>
/// </list>
/// The original full-resolution image is never cached to the thumbnails
/// directory — it is loaded on demand (local: <c>Image.Load</c>; remote:
/// streamed via <c>IImageSource.OpenReadAsync</c>).
/// </summary>
public enum ThumbnailLevel
{
    /// <summary>256 px — gallery grid and list rows.</summary>
    Thumb = 0,

    /// <summary>1024 px — detail preview pane.</summary>
    Preview = 1,
}
