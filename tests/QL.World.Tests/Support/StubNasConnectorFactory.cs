using System.IO;
using QL.World.Services.Nas;

namespace QL.World.Tests.Support;

/// <summary>
/// Stub INasConnectorFactory for VM tests. Extracted to Support/ so both
/// SettingsViewModelTests and SettingsViewModelNasTests share the same
/// fake (zero duplication). Records whether the connector it built was
/// disposed and lets the test enqueue a success/failure outcome.
/// </summary>
internal sealed class StubNasConnectorFactory : INasConnectorFactory
{
    public bool NextSuccess { get; set; } = true;
    public NasConnectionException? NextException { get; set; }
    public bool LastConnectorDisposed { get; private set; }
    public NasConnectionSpec? LastSpec { get; private set; }

    public INasConnector Create(NasConnectionSpec spec, string? plaintextPassword)
    {
        LastConnectorDisposed = false;
        LastSpec = spec;
        return new StubNasConnector(this);
    }

    private sealed class StubNasConnector : INasConnector, IDisposable
    {
        private readonly StubNasConnectorFactory _owner;
        private bool _disposed;

        public StubNasConnector(StubNasConnectorFactory owner) => _owner = owner;

        public NasConnectionSpec Spec => _owner.LastSpec!;

        public Task<bool> TestConnectionAsync(CancellationToken ct)
        {
            if (_owner.NextException is { } ex)
            {
                _owner.NextException = null;
                throw ex;
            }
            bool ok = _owner.NextSuccess;
            _owner.NextSuccess = true;
            return Task.FromResult(ok);
        }

        public Task<IReadOnlyList<NasFileInfo>> ListFilesAsync(string p, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<NasFileInfo> GetMetadataAsync(string p, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string p, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DownloadFileAsync(string p, string d, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task<byte[]> DownloadRangeAsync(string p, long o, int l, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task UploadFileAsync(string l, string r, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task MakeDirectoryAsync(string p, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task DeleteAsync(string p, CancellationToken ct) =>
            throw new NotSupportedException();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.LastConnectorDisposed = true;
        }
    }
}
