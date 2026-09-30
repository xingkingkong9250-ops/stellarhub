using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using QL.World.Services.Nas.WebDav;

namespace QL.World.Services.Nas;

/// <summary>
/// WebDAV implementation of <see cref="INasConnector"/>. Built on
/// <see cref="HttpClient"/> (zero new NuGet). The caller constructs and
/// owns the <see cref="HttpClient"/> instance — including
/// <see cref="HttpClient.BaseAddress"/>, timeout, Basic-auth
/// <see cref="HttpClient.DefaultRequestHeaders"/>, and
/// <see cref="HttpClientHandler.ServerCertificateValidationCallback"/> for
/// <see cref="NasConnectionSpec.TrustSelfSigned"/> — so this class stays
/// focused on request building and response parsing.
/// </summary>
public sealed class WebDavNasConnector : INasConnector
{
    private readonly HttpClient _http;
    private readonly NasConnectionSpec _spec;

    /// <summary>
    /// <paramref name="httpClient"/> must be configured with the connection's
    /// <see cref="HttpClient.BaseAddress"/>, auth header, timeout, and (when
    /// <see cref="NasConnectionSpec.TrustSelfSigned"/>) a permissive
    /// certificate-validation callback before being passed in.
    /// </summary>
    public WebDavNasConnector(NasConnectionSpec spec, HttpClient httpClient)
    {
        _spec = spec ?? throw new ArgumentNullException(nameof(spec));
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public NasConnectionSpec Spec => _spec;

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken)
    {
        // PROPFIND root with Depth: 0 — single response (the root itself).
        // Reused by GetMetadataAsync; keep the helper DRY.
        try
        {
            using HttpResponseMessage resp =
                await SendPropFindAsync(_spec.RootPath, depth: "0", cancellationToken);
            // 401/403 here surfaces as AuthFailed inside SendPropFindAsync.
            return true;
        }
        catch (NasConnectionException) { throw; }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NasConnectionException(NasConnectionKind.Timeout, "test connection timed out", ex);
        }
        catch (Exception ex)
        {
            throw MapNetworkException(ex);
        }
    }

    public async Task<IReadOnlyList<NasFileInfo>> ListFilesAsync(string remotePath, CancellationToken cancellationToken)
    {
        string path = ResolveRemotePath(remotePath);
        try
        {
            using HttpResponseMessage resp =
                await SendPropFindAsync(path, depth: "1", cancellationToken);
            string body = await resp.Content.ReadAsStringAsync(cancellationToken);
            // Depth:1 returns [root-itself, ...children]; drop the self entry.
            return WebDavResponseParser.ParseMultistatus(
                body, skipSelf: true, rootPath: path);
        }
        catch (NasConnectionException) { throw; }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NasConnectionException(NasConnectionKind.Timeout, "list files timed out", ex);
        }
        catch (Exception ex) { throw MapNetworkException(ex); }
    }

    public async Task<NasFileInfo> GetMetadataAsync(string remotePath, CancellationToken cancellationToken)
    {
        string path = ResolveRemotePath(remotePath);
        try
        {
            using HttpResponseMessage resp =
                await SendPropFindAsync(path, depth: "0", cancellationToken);
            string body = await resp.Content.ReadAsStringAsync(cancellationToken);
            var list = WebDavResponseParser.ParseMultistatus(body, skipSelf: false, rootPath: path);
            return list.Count == 0
                ? throw new NasConnectionException(NasConnectionKind.NotFound, $"not found: {path}")
                : list[0];
        }
        catch (NasConnectionException) { throw; }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NasConnectionException(NasConnectionKind.Timeout, "get metadata timed out", ex);
        }
        catch (Exception ex) { throw MapNetworkException(ex); }
    }

    public async Task<Stream> OpenReadAsync(string remotePath, CancellationToken cancellationToken)
    {
        string path = ResolveRemotePath(remotePath);
        try
        {
            // ResponseHeadersRead streams the body instead of buffering it.
            HttpResponseMessage resp = await SendGetAsync(path, cancellationToken, completionOption: HttpCompletionOption.ResponseHeadersRead);
            return new ResponseStream(resp); // disposes resp on dispose
        }
        catch (NasConnectionException) { throw; }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NasConnectionException(NasConnectionKind.Timeout, "open read timed out", ex);
        }
        catch (Exception ex) { throw MapNetworkException(ex); }
    }

    public async Task DownloadFileAsync(string remotePath, string destinationPath, CancellationToken cancellationToken)
    {
        string path = ResolveRemotePath(remotePath);
        try
        {
            HttpResponseMessage resp = await SendGetAsync(path, cancellationToken, HttpCompletionOption.ResponseHeadersRead);
            try
            {
                await using (Stream network = await resp.Content.ReadAsStreamAsync(cancellationToken))
                using (Stream file = new FileStream(
                    destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 81920, useAsync: true))
                {
                    await network.CopyToAsync(file, cancellationToken);
                }
            }
            finally { resp.Dispose(); }
        }
        catch (NasConnectionException) { throw; }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NasConnectionException(NasConnectionKind.Timeout, "download timed out", ex);
        }
        catch (Exception ex) { throw MapNetworkException(ex); }
    }

    public async Task<byte[]> DownloadRangeAsync(string remotePath, long offset, int length, CancellationToken cancellationToken)
    {
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (length <= 0) throw new ArgumentOutOfRangeException(nameof(length));
        string path = ResolveRemotePath(remotePath);

        // HttpRequestMessage is single-use: build a fresh instance per send
        // (M3-D lesson — re-sending the same instance throws InvalidOperationException).
        // Range header is inclusive on both ends: bytes=offset-(offset+length-1).
        try
        {
            using HttpRequestMessage first = BuildRangeRequest(path, offset, length);
            (byte[]? firstBytes, HttpStatusCode firstStatus) =
                await TrySendRangeAsync(first, cancellationToken);

            // 206 Partial Content → return the body bytes.
            if (firstStatus == HttpStatusCode.PartialContent && firstBytes is not null)
                return firstBytes;

            // 416 Range Not Satisfiable → retry once with a brand-new request
            // (the original instance is disposed after SendAsync and cannot
            // be reused). Some servers reject ranges that overshoot the file
            // but accept a fresh plain GET returning the whole file.
            if (firstStatus == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                using HttpRequestMessage retry = BuildRangeRequest(path, offset, length);
                (byte[]? retryBytes, HttpStatusCode retryStatus) =
                    await TrySendRangeAsync(retry, cancellationToken);
                if (retryStatus == HttpStatusCode.PartialContent && retryBytes is not null)
                    return retryBytes;
                throw new NasConnectionException(
                    NasConnectionKind.RangeNotSatisfiable,
                    $"range not satisfiable at offset={offset} length={length} (status={retryStatus})");
            }

            // 200 → server ignored the Range header and returned the whole file;
            // slice the requested window out of the body.
            if (firstStatus == HttpStatusCode.OK && firstBytes is not null)
            {
                if (firstBytes.Length < offset + length)
                    throw new NasConnectionException(
                        NasConnectionKind.RangeNotSatisfiable,
                        $"server returned {firstBytes.Length} bytes, less than offset+length={offset + length}");
                byte[] slice = new byte[length];
                Array.Copy(firstBytes, offset, slice, 0, length);
                return slice;
            }

            // Anything else is a protocol error (auth/timeout already thrown inside).
            throw new NasConnectionException(
                NasConnectionKind.ProtocolError,
                $"unexpected status {firstStatus} for range read");
        }
        catch (NasConnectionException) { throw; }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NasConnectionException(NasConnectionKind.Timeout, "range download timed out", ex);
        }
        catch (Exception ex) { throw MapNetworkException(ex); }
    }

    public async Task UploadFileAsync(string localPath, string remotePath, CancellationToken cancellationToken)
    {
        string path = ResolveRemotePath(remotePath);
        try
        {
            using FileStream stream = new(localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 81920, useAsync: true);
            using StreamContent content = new(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using HttpRequestMessage req = new(HttpMethod.Put, path) { Content = content };
            using HttpResponseMessage resp = await SendAsync(req, cancellationToken);
        }
        catch (NasConnectionException) { throw; }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NasConnectionException(NasConnectionKind.Timeout, "upload timed out", ex);
        }
        catch (Exception ex) { throw MapNetworkException(ex); }
    }

    public async Task MakeDirectoryAsync(string remotePath, CancellationToken cancellationToken)
    {
        string path = ResolveRemotePath(remotePath);
        try
        {
            using HttpRequestMessage req = new(new HttpMethod("MKCOL"), path);
            using HttpResponseMessage resp = await SendAsync(req, cancellationToken);
            // 405 Method Not Allowed / 301 → directory already exists; idempotent.
            // 409 Conflict → parent missing; surface as NotFound for caller to handle.
        }
        catch (NasConnectionException) { throw; }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NasConnectionException(NasConnectionKind.Timeout, "mkcol timed out", ex);
        }
        catch (Exception ex) { throw MapNetworkException(ex); }
    }

    public async Task DeleteAsync(string remotePath, CancellationToken cancellationToken)
    {
        string path = ResolveRemotePath(remotePath);
        try
        {
            using HttpRequestMessage req = new(HttpMethod.Delete, path);
            using HttpResponseMessage resp = await SendAsync(req, cancellationToken);
            // 404 → already gone; idempotent.
        }
        catch (NasConnectionException) { throw; }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NasConnectionException(NasConnectionKind.Timeout, "delete timed out", ex);
        }
        catch (Exception ex) { throw MapNetworkException(ex); }
    }

    public async Task MoveAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        string src = ResolveRemotePath(sourcePath);
        string dst = ResolveRemotePath(destinationPath);

        bool moved = await TryServerSideMoveAsync(src, dst, cancellationToken).ConfigureAwait(false);
        if (moved) return;

        // Fallback: MOVE unsupported/rejected — round-trip via a local temp file.
        string temp = Path.Combine(Path.GetTempPath(), $"webdav-move-{Guid.NewGuid():N}");
        try
        {
            await DownloadFileAsync(sourcePath, temp, cancellationToken).ConfigureAwait(false);
            await UploadFileAsync(temp, destinationPath, cancellationToken).ConfigureAwait(false);
            await DeleteAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>
    /// Attempts an RFC4918 MOVE. Returns false (never throws) when the server
    /// rejects the verb — the caller then uses the download/upload fallback.
    /// </summary>
    private async Task<bool> TryServerSideMoveAsync(string src, string dst, CancellationToken cancellationToken)
    {
        try
        {
            // Destination must be an absolute URI; BaseAddress ends with the
            // configured RootPath so a rooted relative URI composes correctly.
            if (_http.BaseAddress is null)
                return false;
            var destinationUri = new Uri(_http.BaseAddress, dst);

            using var req = new HttpRequestMessage(new HttpMethod("MOVE"), src);
            req.Headers.Add("Destination", destinationUri.AbsoluteUri);
            req.Headers.Add("Overwrite", "T");

            using HttpResponseMessage resp = await SendAsync(req, cancellationToken).ConfigureAwait(false);
            return resp.IsSuccessStatusCode;
        }
        catch (NasConnectionException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            // Malformed Destination URI / header value — use fallback.
            return false;
        }
    }

    // ─── helpers ────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendPropFindAsync(string path, string depth, CancellationToken cancellationToken)
    {
        using HttpRequestMessage req = new(new HttpMethod("PROPFIND"), path);
        req.Headers.Add("Depth", depth);
        req.Content = new StringContent(WebDavRequestBuilder.BuildPropFindBody(), Encoding.UTF8, "application/xml");
        HttpResponseMessage resp = await SendAsync(req, cancellationToken);
        return resp; // ownership transferred (already disposed via using above for the request only)
    }

    private async Task<HttpResponseMessage> SendGetAsync(string path, CancellationToken cancellationToken, HttpCompletionOption completionOption)
    {
        using HttpRequestMessage req = new(HttpMethod.Get, path);
        HttpResponseMessage resp = await SendAsync(req, cancellationToken, completionOption);
        return resp;
    }

    /// <summary>
    /// Sends and disposes the request, returning the response. Maps 401/403
    /// to AuthFailed, 404 to NotFound, 5xx to ProtocolError, etc. The caller
    /// owns the returned <see cref="HttpResponseMessage"/> and must dispose it.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(request, completionOption, cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NasConnectionException(NasConnectionKind.Timeout, "request timed out", ex);
        }
        catch (HttpRequestException ex) { throw MapHttpRequestException(ex); }
        return MapStatus(resp, request.Method);
    }

    private async Task<(byte[]? Bytes, HttpStatusCode Status)> TrySendRangeAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Buffer the body because the caller expects a small range chunk
        // (≤4MB). The caller's `using HttpRequestMessage` block disposes
        // the request after we return; the response is disposed here.
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NasConnectionException(NasConnectionKind.Timeout, "range request timed out", ex);
        }
        catch (HttpRequestException ex) { throw MapHttpRequestException(ex); }

        using (resp)
        {
            HttpStatusCode status = resp.StatusCode;
            // Auth/network/server-error statuses throw via MapStatus before
            // we ever try to read the body. Range-related statuses (206/200/416)
            // return the buffered bytes for the caller to interpret.
            if (status != HttpStatusCode.PartialContent &&
                status != HttpStatusCode.OK &&
                status != HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // MapStatus throws for non-2xx; if it ever returned we'd
                // hit the unreachable throw below (defensive — never executes).
                resp.Dispose();
                throw new NasConnectionException(
                    NasConnectionKind.ProtocolError,
                    $"unexpected status {status} for range request");
            }
            byte[] bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
            return (bytes, status);
        }
    }

    private static HttpRequestMessage BuildRangeRequest(string path, long offset, int length) =>
        new(HttpMethod.Get, path)
        {
            Headers = { Range = new RangeHeaderValue(offset, offset + length - 1) }
        };

    /// <summary>
    /// Throws on auth/protocol errors. Returns resp on success. For
    /// MKCOL/DELETE, 404/405 are treated as success (idempotent semantics);
    /// for PROPFIND/GET they map to NotFound.
    /// </summary>
    private static HttpResponseMessage MapStatus(HttpResponseMessage resp, HttpMethod method)
    {
        int code = (int)resp.StatusCode;
        switch (code)
        {
            case >= 200 and < 300:
                return resp;
            case 401 or 403:
                resp.Dispose();
                throw new NasConnectionException(
                    NasConnectionKind.AuthenticationFailed,
                    $"auth failed ({code} {resp.ReasonPhrase})");
            case 404 when method == HttpMethod.Delete || method.Method == "MKCOL":
                // Idempotent delete / mkdir-already-exists — caller treats as success.
                resp.Dispose();
                return resp;
            case 404:
                resp.Dispose();
                throw new NasConnectionException(
                    NasConnectionKind.NotFound,
                    $"not found ({resp.ReasonPhrase})");
            case 405 when method.Method == "MKCOL":
                // Method Not Allowed on MKCOL means "collection exists".
                resp.Dispose();
                return resp;
            case 408: // Request Timeout
                resp.Dispose();
                throw new NasConnectionException(NasConnectionKind.Timeout, "server timeout (408)");
            case >= 400 and < 500:
                resp.Dispose();
                throw new NasConnectionException(
                    NasConnectionKind.ProtocolError,
                    $"client error {code} {resp.ReasonPhrase}");
            case >= 500:
                resp.Dispose();
                throw new NasConnectionException(
                    NasConnectionKind.ProtocolError,
                    $"server error {code} {resp.ReasonPhrase}");
            default:
                resp.Dispose();
                throw new NasConnectionException(
                    NasConnectionKind.Unknown,
                    $"unexpected status {code} {resp.ReasonPhrase}");
        }
    }

    private static NasConnectionException MapNetworkException(Exception ex) =>
        ex is NasConnectionException nc ? nc
        : ex is HttpRequestException hre ? MapHttpRequestException(hre)
        : ex is OperationCanceledException oc && !oc.CancellationToken.IsCancellationRequested
            ? new NasConnectionException(NasConnectionKind.Timeout, "timed out", oc)
        : new NasConnectionException(NasConnectionKind.Unknown, ex.Message, ex);

    private static NasConnectionException MapHttpRequestException(HttpRequestException ex)
    {
        // HttpClient throws HttpRequestException for DNS/TCP/TLS failures and
        // for non-success status codes (when EnsureSuccessStatusCode is called,
        // which we avoid). Categorize by inner status when available.
        HttpStatusCode? status = ex.StatusCode;
        return status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new(
                NasConnectionKind.AuthenticationFailed, $"auth failed: {ex.Message}", ex),
            HttpStatusCode.NotFound => new(NasConnectionKind.NotFound, $"not found: {ex.Message}", ex),
            HttpStatusCode.RequestTimeout => new(NasConnectionKind.Timeout, "timed out (408)", ex),
            _ => new(NasConnectionKind.NetworkUnreachable, $"network error: {ex.Message}", ex),
        };
    }

    private string ResolveRemotePath(string remotePath)
    {
        // remotePath is relative to RootPath; both must be forward-slash absolute.
        string root = WebDavResponseParser.NormalizePath(_spec.RootPath);
        string rel = string.IsNullOrEmpty(remotePath) ? "/" : remotePath.Trim();
        if (!rel.StartsWith('/'))
            rel = "/" + rel;
        if (rel == "/")
            return root;
        return root == "/" ? rel : root + rel;
    }

    /// <summary>
    /// Wraps an <see cref="HttpResponseMessage"/> so the caller can stream
    /// the body and have the response disposed with the stream. HttpClient
    /// connections return to the pool only after the response is disposed.
    /// </summary>
    private sealed class ResponseStream : Stream
    {
        private readonly HttpResponseMessage _resp;
        private readonly Stream _body;
        private bool _disposed;

        public ResponseStream(HttpResponseMessage resp)
        {
            _resp = resp;
            _body = resp.Content.ReadAsStream();
        }

        public override bool CanRead => !_disposed && _body.CanRead;
        public override bool CanSeek => _body.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _body.Length;
        public override long Position { get => _body.Position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _body.Read(buffer, offset, count);
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            _body.ReadAsync(buffer, offset, count, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _body.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing) { _body.Dispose(); _resp.Dispose(); }
            }
            base.Dispose(disposing);
        }
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
