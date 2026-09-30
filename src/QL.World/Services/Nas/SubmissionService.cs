using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;
using QL.World.Models;
using QL.World.ViewModels;

namespace QL.World.Services.Nas;

/// <summary>Outcome of <see cref="SubmissionService.SubmitFromLocalAsync"/>.</summary>
public enum SubmissionOutcome
{
    Submitted,
    DuplicateInLibrary,
    DuplicatePending,
    MissingLocalFile,
}

/// <summary>Result of a submit attempt.</summary>
public sealed record SubmissionResult(SubmissionOutcome Outcome, string SubmitId)
{
    public bool IsSuccess => Outcome == SubmissionOutcome.Submitted;
}

/// <summary>
/// M6-E / U6-B: submission review workflow. Users submit images into the NAS
/// pending/ directory; admins approve (promote to library.json) or reject
/// (delete the pending file). All state lives on NAS — no local DB.
/// </summary>
public sealed class SubmissionService
{
    private readonly SharedStorageService _storage;
    private readonly SharedLibraryService _library;
    private readonly ThumbnailService _thumbnails;
    private readonly AppSettingsService _settings;

    public SubmissionService(
        SharedStorageService storage,
        SharedLibraryService library,
        ThumbnailService thumbnails,
        AppSettingsService settings)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>List all pending submissions.</summary>
    public Task<IReadOnlyList<PendingSubmission>> ListPendingAsync(CancellationToken ct)
        => _storage.LoadPendingAsync(ct);

    /// <summary>
    /// Submit a new image for review. The caller is responsible for uploading
    /// the original file to NAS first; this just records the metadata.
    /// </summary>
    public Task SubmitAsync(PendingSubmission submission, CancellationToken ct)
        => _storage.SavePendingAsync(submission, ct);

    /// <summary>
    /// U6-B: submits a local gallery item end to end — dedup against the
    /// shared library and pending queue, uploads the original plus a 256px
    /// WebP thumbnail into <c>pending/files/&lt;submitId&gt;/</c>, and writes
    /// the pending JSON. Failures are reported via <see cref="SubmissionResult"/>
    /// rather than exceptions for expected cases.
    /// </summary>
    public async Task<SubmissionResult> SubmitFromLocalAsync(
        GalleryItemViewModel item,
        string submittedBy,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (!File.Exists(item.FilePath))
            return new SubmissionResult(SubmissionOutcome.MissingLocalFile, string.Empty);

        var library = await _library.LoadAsync(ct).ConfigureAwait(false);
        if (library.Any(r => r.Id == item.FileHash))
            return new SubmissionResult(SubmissionOutcome.DuplicateInLibrary, string.Empty);

        var pending = await _storage.LoadPendingAsync(ct).ConfigureAwait(false);
        if (pending.Any(p => p.FileHash == item.FileHash))
            return new SubmissionResult(SubmissionOutcome.DuplicatePending, string.Empty);

        string submitId = Guid.NewGuid().ToString("N");
        INasConnector connector = _storage.Connector;

        await connector.MakeDirectoryAsync(SharedPaths.PendingFiles(submitId), ct)
            .ConfigureAwait(false);

        string originalPath = SharedPaths.PendingOriginal(submitId, item.FileName);
        await connector.UploadFileAsync(item.FilePath, originalPath, ct)
            .ConfigureAwait(false);

        string? thumbNasPath = null;
        string? tempThumb = null;
        try
        {
            string? localThumb = await PrepareThumbnailAsync(item, ct).ConfigureAwait(false);
            if (localThumb is not null)
            {
                // Temp-encoded files (no queued thumbnail existed) are deleted
                // after upload; cache thumbnails are left in place.
                tempThumb = IsCacheThumbnail(localThumb, item.FileHash) ? null : localThumb;
                thumbNasPath = SharedPaths.PendingThumbnail(submitId);
                await connector.UploadFileAsync(localThumb, thumbNasPath, ct)
                    .ConfigureAwait(false);
            }

            var submission = new PendingSubmission
            {
                SubmitId = submitId,
                FileName = item.FileName,
                FileHash = item.FileHash,
                FileSize = item.FileSize,
                Width = item.Width,
                Height = item.Height,
                Format = item.Format,
                PrimaryCategory = item.Category,
                Tags = item.TagsCsv
                    .Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .Select(t => t.Trim()).ToList(),
                SubmittedBy = submittedBy,
                OriginalNasPath = originalPath,
                ThumbnailNasPath = thumbNasPath,
            };
            await _storage.SavePendingAsync(submission, ct).ConfigureAwait(false);
        }
        finally
        {
            if (tempThumb is not null)
            {
                try { File.Delete(tempThumb); }
                catch (IOException) { /* best effort */ }
            }
        }

        return new SubmissionResult(SubmissionOutcome.Submitted, submitId);
    }

    /// <summary>
    /// Returns the local thumbnail path when it already exists; otherwise
    /// synchronously encodes one (resize-max to the configured thumbnail
    /// size) into a temp file. Null when encoding fails — submission then
    /// proceeds without a thumbnail.
    /// </summary>
    private async Task<string?> PrepareThumbnailAsync(GalleryItemViewModel item, CancellationToken ct)
    {
        string existing = _thumbnails.GetThumbnailPath(item.FileHash);
        if (File.Exists(existing))
            return existing;

        string temp = Path.Combine(_settings.DataRoot, "temp", $"sub-{Guid.NewGuid():N}.webp");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            await Task.Run(() =>
            {
                using Image image = Image.Load(item.FilePath);
                image.Mutate(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = new Size(_settings.ThumbnailSize, _settings.ThumbnailSize),
                    Mode = ResizeMode.Max,
                }));
                image.Save(temp, new WebpEncoder { Quality = _settings.ThumbnailQuality });
            }, ct).ConfigureAwait(false);
            return temp;
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { /* best effort */ }
            return null;
        }
    }

    private bool IsCacheThumbnail(string path, string hash) =>
        string.Equals(
            Path.GetFullPath(path),
            Path.GetFullPath(_thumbnails.GetThumbnailPath(hash)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Admin approves: promotes the submission into the shared library and
    /// deletes the pending file. Returns the created SharedImageRecord.
    /// </summary>
    public async Task<SharedImageRecord> ApproveAsync(PendingSubmission sub, string approvedBy, CancellationToken ct)
    {
        var record = new SharedImageRecord
        {
            Id = sub.FileHash,
            FileName = sub.FileName,
            FileHash = sub.FileHash,
            FileSize = sub.FileSize,
            Width = sub.Width,
            Height = sub.Height,
            Format = sub.Format,
            PrimaryCategory = sub.PrimaryCategory,
            Tags = sub.Tags,
            SubmittedBy = sub.SubmittedBy,
            ApprovedBy = approvedBy,
            ApprovedAt = DateTimeOffset.UtcNow,
        };
        await _library.AddOrUpdateAsync(record, ct);
        await _storage.DeletePendingAsync(sub.SubmitId, ct);
        return record;
    }

    /// <summary>Admin rejects: deletes the pending file without promotion.</summary>
    public Task RejectAsync(string submitId, CancellationToken ct)
        => _storage.DeletePendingAsync(submitId, ct);
}
