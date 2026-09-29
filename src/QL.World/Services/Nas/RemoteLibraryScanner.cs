using QL.World.Models;

namespace QL.World.Services.Nas;

/// <summary>
/// Orchestrates a one-shot NAS library scan: resolve the persisted
/// <see cref="NasConnectionSpec"/> + DPAPI-decrypted password, build a
/// connector via <see cref="INasConnectorFactory"/>, wrap it in a
/// <see cref="RemoteFileSystemAccessor"/>, and hand both to
/// <see cref="ImportService"/> with <see cref="ImportOptions.Source"/> =
/// <see cref="ImageSource.Remote"/>. The connector is disposed when the
/// scan completes (or fails). Progress/cancellation flow straight through to
/// the importer's per-file loop.
/// </summary>
public sealed class RemoteLibraryScanner
{
    private readonly ImportService _import;
    private readonly INasConnectorFactory _factory;
    private readonly AppSettingsService _settings;

    public RemoteLibraryScanner(
        ImportService import,
        INasConnectorFactory factory,
        AppSettingsService settings)
    {
        _import = import ?? throw new ArgumentNullException(nameof(import));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>
    /// Scans the NAS connection identified by <paramref name="connectionId"/>.
    /// Throws <see cref="InvalidOperationException"/> when the id is no longer
    /// present in <see cref="AppSettingsService.NasConnections"/> (deleted).
    /// </summary>
    public async Task<ImportReport> ScanAsync(
        int connectionId,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        NasConnectionSpec? spec = _settings.NasConnections.FirstOrDefault(c => c.Id == connectionId)
            ?? throw new InvalidOperationException(
                $"NAS 连接 {connectionId} 不存在（可能在扫描开始前已被删除）。");

        string? password = _settings.HasNasPassword(connectionId)
            ? _settings.GetNasPassword(connectionId)
            : null;

        INasConnector connector = _factory.Create(spec, password);
        try
        {
            var accessor = new RemoteFileSystemAccessor(connector);
            var options = new ImportOptions
            {
                Source = ImageSource.Remote,
                NasConnectionId = connectionId,
                InputPaths = [string.IsNullOrEmpty(spec.RootPath) ? "/" : spec.RootPath],
                RecurseSubfolders = true,
                SkipHidden = true,
                EnabledExtensions = ImportOptions.SupportedExtensions,
            };
            return await _import.ImportAsync(options, accessor, progress, cancellationToken);
        }
        finally
        {
            (connector as IDisposable)?.Dispose();
        }
    }
}
