namespace QL.World.ViewModels;

/// <summary>
/// U6-A: abstraction over a per-gallery thumbnail loader. The local
/// <see cref="Support.GalleryThumbnailProvider"/> (SQLite gallery) and the
/// NAS shared-library thumbnail cache both implement it so the WPF
/// container-realization behavior can attach to either ListBox without
/// knowing where the bytes come from.
/// </summary>
public interface IGalleryThumbnailProvider
{
    void Attach(GalleryItemViewModel item);
    void Detach(GalleryItemViewModel item);
    void ClearRealized();
}
