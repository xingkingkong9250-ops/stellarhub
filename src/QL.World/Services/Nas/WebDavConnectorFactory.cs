using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using QL.World.Interop;

namespace QL.World.Services.Nas;

/// <summary>
/// WebDAV implementation of <see cref="INasConnectorFactory"/>. Builds an
/// <see cref="HttpClient"/> (BaseAddress, Basic auth, 30 s timeout,
/// permissive cert callback when <see cref="NasConnectionSpec.TrustSelfSigned"/>
/// is set) and wraps the M5-B <see cref="WebDavNasConnector"/> plus the
/// HttpClient/Handler in a disposable wrapper so callers don't have to know
/// about HttpClient lifetime. Zero new NuGet.
/// </summary>
public sealed class WebDavConnectorFactory : INasConnectorFactory
{
    private static readonly TimeSpan TIMEOUT = TimeSpan.FromSeconds(30);

    public INasConnector Create(NasConnectionSpec spec, string? plaintextPassword)
    {
        ArgumentNullException.ThrowIfNull(spec);

        var handler = new HttpClientHandler();
        if (spec.TrustSelfSigned)
        {
            // .NET Core/5+ exposes this as ServerCertificateCustomValidationCallback
            // (the older .NET Framework ServerCertificateValidationCallback was removed).
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        }

        string scheme = spec.UseTls ? "https" : "http";
        int port = spec.Port ?? (spec.UseTls ? 443 : 80);
        var baseAddress = new Uri($"{scheme}://{spec.Host}:{port}/");

        var http = new HttpClient(handler) { BaseAddress = baseAddress, Timeout = TIMEOUT };

        // M8-D: ShareLink 副库走匿名访问——即使 spec 误填了凭据也不加 Basic-auth。
        // AppSettingsService.ValidateNasRoles 已在 Load 时清空 ShareLink 的凭据，
        // 此处 defensive 再判一次确保 connector 不会带 Authorization header。
        if (spec.Role != NasRole.ShareLink && !string.IsNullOrEmpty(spec.Username))
        {
            string password = !string.IsNullOrEmpty(plaintextPassword)
                ? plaintextPassword
                : (DpApiProtector.Unprotect(spec.EncryptedPassword) ?? string.Empty);
            byte[] credential = Encoding.UTF8.GetBytes($"{spec.Username}:{password}");
            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(credential));
        }

        var inner = new WebDavNasConnector(spec, http);
        return new DisposableWebDavConnector(inner, http, handler);
    }

    /// <summary>
    /// Wraps the WebDavNasConnector + its HttpClient + HttpClientHandler so
    /// disposing the connector releases all three. Implements INasConnector
    /// by delegating every method to the inner connector (M5-B's connector
    /// doesn't own the HttpClient so we must NOT add IDisposable to it).
    /// </summary>
    private sealed class DisposableWebDavConnector : INasConnector, IDisposable
    {
        private readonly WebDavNasConnector _inner;
        private readonly HttpClient _http;
        private readonly HttpClientHandler _handler;
        private bool _disposed;

        public DisposableWebDavConnector(WebDavNasConnector inner, HttpClient http, HttpClientHandler handler)
        {
            _inner = inner;
            _http = http;
            _handler = handler;
        }

        public NasConnectionSpec Spec => _inner.Spec;

        public Task<bool> TestConnectionAsync(CancellationToken cancellationToken) =>
            _inner.TestConnectionAsync(cancellationToken);
        public Task<IReadOnlyList<NasFileInfo>> ListFilesAsync(string remotePath, CancellationToken cancellationToken) =>
            _inner.ListFilesAsync(remotePath, cancellationToken);
        public Task<NasFileInfo> GetMetadataAsync(string remotePath, CancellationToken cancellationToken) =>
            _inner.GetMetadataAsync(remotePath, cancellationToken);
        public Task<Stream> OpenReadAsync(string remotePath, CancellationToken cancellationToken) =>
            _inner.OpenReadAsync(remotePath, cancellationToken);
        public Task DownloadFileAsync(string remotePath, string destinationPath, CancellationToken cancellationToken) =>
            _inner.DownloadFileAsync(remotePath, destinationPath, cancellationToken);
        public Task<byte[]> DownloadRangeAsync(string remotePath, long offset, int length, CancellationToken cancellationToken) =>
            _inner.DownloadRangeAsync(remotePath, offset, length, cancellationToken);
        public Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken) =>
            _inner.UploadFileAsync(localPath, remotePath, cancellationToken);
        public Task MakeDirectoryAsync(string remotePath, CancellationToken cancellationToken) =>
            _inner.MakeDirectoryAsync(remotePath, cancellationToken);
        public Task DeleteAsync(string remotePath, CancellationToken cancellationToken) =>
            _inner.DeleteAsync(remotePath, cancellationToken);
        public Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken) =>
            _inner.MoveAsync(sourcePath, destinationPath, cancellationToken);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _http.Dispose();
            _handler.Dispose();
        }
    }
}
