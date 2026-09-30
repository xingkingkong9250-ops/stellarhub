using QL.World.Models;

namespace QL.World.Services.Nas;

/// <summary>
/// M6-E: submission review workflow. Users submit images into the NAS
/// pending/ directory; admins approve (promote to library.json) or reject
/// (delete the pending file). All state lives on NAS — no local DB.
/// </summary>
public sealed class SubmissionService
{
    private readonly SharedStorageService _storage;
    private readonly SharedLibraryService _library;

    public SubmissionService(SharedStorageService storage, SharedLibraryService library)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _library = library ?? throw new ArgumentNullException(nameof(library));
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
