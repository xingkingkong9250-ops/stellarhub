using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using QL.World.Services.Nas;

namespace QL.World.Tests.Nas;

/// <summary>
/// M5-B: <see cref="WebDavNasConnector"/> against a stub
/// <see cref="HttpMessageHandler"/>. Each test enqueues canned responses;
/// the stub records every request so we can assert on Range header, method,
/// and that 416 retries build a fresh <see cref="HttpRequestMessage"/>
/// (M3-D lesson — HttpRequestMessage is single-use).
/// </summary>
public sealed class WebDavNasConnectorTests
{
    private const string MULTISTATUS_TWO_CHILDREN = """
        <?xml version="1.0" encoding="utf-8"?>
        <D:multistatus xmlns:D="DAV:">
          <D:response>
            <D:href>/photos/</D:href>
            <D:propstat>
              <D:prop>
                <D:resourcetype><D:collection/></D:resourcetype>
              </D:prop>
              <D:status>HTTP/1.1 200 OK</D:status>
            </D:propstat>
          </D:response>
          <D:response>
            <D:href>/photos/a.jpg</D:href>
            <D:propstat>
              <D:prop>
                <D:getcontentlength>1024</D:getcontentlength>
                <D:getlastmodified>Tue, 01 Sep 2026 12:00:00 GMT</D:getlastmodified>
                <D:resourcetype/>
                <D:getcontenttype>image/jpeg</D:getcontenttype>
              </D:prop>
              <D:status>HTTP/1.1 200 OK</D:status>
            </D:propstat>
          </D:response>
          <D:response>
            <D:href>/photos/sub</D:href>
            <D:propstat>
              <D:prop>
                <D:resourcetype><D:collection/></D:resourcetype>
              </D:prop>
              <D:status>HTTP/1.1 200 OK</D:status>
            </D:propstat>
          </D:response>
        </D:multistatus>
        """;

    private static WebDavNasConnector NewConnector(StubHttpMessageHandler stub)
    {
        HttpClient http = new(stub) { BaseAddress = new Uri("https://nas.local/") };
        return new WebDavNasConnector(new NasConnectionSpec
        {
            Id = 1, Name = "n", Host = "nas.local", RootPath = "/photos/", UseTls = true,
        }, http);
    }

    [Fact]
    public async Task TestConnectionAsync_PROPFIND_root_returns_true()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(207, MULTISTATUS_TWO_CHILDREN);
        WebDavNasConnector c = NewConnector(stub);
        Assert.True(await c.TestConnectionAsync(CancellationToken.None));
        Assert.Equal(1, stub.CallCount);
        Assert.Equal("PROPFIND", stub.Requests[0].Method.Method);
        Assert.Equal("0", stub.Requests[0].Headers.GetValues("Depth").First());
    }

    [Fact]
    public async Task TestConnectionAsync_401_throws_AuthenticationFailed()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(401);
        WebDavNasConnector c = NewConnector(stub);
        var ex = await Assert.ThrowsAsync<NasConnectionException>(
            () => c.TestConnectionAsync(CancellationToken.None));
        Assert.Equal(NasConnectionKind.AuthenticationFailed, ex.Kind);
    }

    [Fact]
    public async Task TestConnectionAsync_timeout_throws_Timeout()
    {
        var stub = new StubHttpMessageHandler();
        stub.EnqueueTimeout();
        WebDavNasConnector c = NewConnector(stub);
        var ex = await Assert.ThrowsAsync<NasConnectionException>(
            () => c.TestConnectionAsync(CancellationToken.None));
        Assert.Equal(NasConnectionKind.Timeout, ex.Kind);
    }

    [Fact]
    public async Task ListFilesAsync_drops_self_keeps_children()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(207, MULTISTATUS_TWO_CHILDREN);
        WebDavNasConnector c = NewConnector(stub);
        IReadOnlyList<NasFileInfo> list = await c.ListFilesAsync("/", CancellationToken.None);

        Assert.Equal(2, list.Count);
        Assert.Contains(list, f => f.Name == "a.jpg" && !f.IsDirectory && f.Size == 1024);
        Assert.Contains(list, f => f.Name == "sub" && f.IsDirectory);
        Assert.DoesNotContain(list, f => f.Path == "/photos" || f.Path == "/photos/");
        Assert.Equal("image/jpeg", list.Single(f => f.Name == "a.jpg").ContentType);
        Assert.NotNull(list.Single(f => f.Name == "a.jpg").ModifiedAt);
    }

    [Fact]
    public async Task GetMetadataAsync_404_throws_NotFound()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(404);
        WebDavNasConnector c = NewConnector(stub);
        var ex = await Assert.ThrowsAsync<NasConnectionException>(
            () => c.GetMetadataAsync("/missing.jpg", CancellationToken.None));
        Assert.Equal(NasConnectionKind.NotFound, ex.Kind);
    }

    [Fact]
    public async Task DownloadRangeAsync_sends_Range_header_in_bytes_form()
    {
        var stub = new StubHttpMessageHandler();
        byte[] bytes = new byte[16]; for (int i = 0; i < 16; i++) bytes[i] = (byte)i;
        RangeHeaderValue? captured = null;
        stub.Enqueue(206, bytes: bytes, inspect: r => captured = r.Headers.Range);
        WebDavNasConnector c = NewConnector(stub);

        byte[] got = await c.DownloadRangeAsync("/x.jpg", 0, 16, CancellationToken.None);
        Assert.Equal(16, got.Length);
        Assert.NotNull(captured);
        var range = captured!.Ranges.Single();
        Assert.Equal(0, range.From);
        Assert.Equal(15, range.To); // bytes=0-15
    }

    [Fact]
    public async Task DownloadRangeAsync_416_retries_once_with_new_request_instance()
    {
        // First request → 416; second request → 206 with the chunk.
        // The two HttpRequestMessage instances MUST be different (M3-D).
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(416); // first attempt — Range Not Satisfiable
        byte[] bytes = new byte[8]; for (int i = 0; i < 8; i++) bytes[i] = (byte)i;
        stub.Enqueue(206, bytes: bytes);
        WebDavNasConnector c = NewConnector(stub);

        byte[] got = await c.DownloadRangeAsync("/x.jpg", 0, 8, CancellationToken.None);
        Assert.Equal(8, got.Length);
        Assert.Equal(2, stub.CallCount);
        Assert.NotSame(stub.Requests[0], stub.Requests[1]); // fresh request per send
        Assert.Equal("GET", stub.Requests[0].Method.Method);
        Assert.Equal("GET", stub.Requests[1].Method.Method);
    }

    [Fact]
    public async Task DownloadRangeAsync_416_both_attempts_throws_RangeNotSatisfiable()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(416);
        stub.Enqueue(416);
        WebDavNasConnector c = NewConnector(stub);

        var ex = await Assert.ThrowsAsync<NasConnectionException>(
            () => c.DownloadRangeAsync("/x.jpg", 0, 8, CancellationToken.None));
        Assert.Equal(NasConnectionKind.RangeNotSatisfiable, ex.Kind);
        Assert.Equal(2, stub.CallCount);
    }

    [Fact]
    public async Task DownloadRangeAsync_200_server_ignores_range_slices_response()
    {
        var stub = new StubHttpMessageHandler();
        // Server returned the full 32-byte file (ignored Range); we asked for [0..16).
        byte[] full = new byte[32]; for (int i = 0; i < 32; i++) full[i] = (byte)i;
        stub.Enqueue(200, bytes: full);
        WebDavNasConnector c = NewConnector(stub);

        byte[] got = await c.DownloadRangeAsync("/x.jpg", 0, 16, CancellationToken.None);
        Assert.Equal(16, got.Length);
        Assert.Equal(0, got[0]);
        Assert.Equal(15, got[15]);
    }

    [Fact]
    public async Task DownloadFileAsync_writes_response_bytes_to_destination()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(200, bytes: new byte[] { 9, 8, 7, 6 });
        WebDavNasConnector c = NewConnector(stub);
        string temp = Path.GetTempFileName();
        try
        {
            await c.DownloadFileAsync("/x.jpg", temp, CancellationToken.None);
            Assert.True(File.Exists(temp));
            Assert.Equal(new byte[] { 9, 8, 7, 6 }, File.ReadAllBytes(temp));
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    [Fact]
    public async Task OpenReadAsync_returns_readable_stream()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(200, bytes: new byte[] { 1, 2, 3, 4 });
        WebDavNasConnector c = NewConnector(stub);
        await using (Stream s = await c.OpenReadAsync("/x.jpg", CancellationToken.None))
        using (MemoryStream copy = new())
        {
            await s.CopyToAsync(copy);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, copy.ToArray());
        }
    }

    [Fact]
    public async Task MakeDirectoryAsync_405_method_not_allowed_treated_as_success()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(405); // collection already exists
        WebDavNasConnector c = NewConnector(stub);
        await c.MakeDirectoryAsync("/photos/sub", CancellationToken.None);
        Assert.Equal(1, stub.CallCount);
        Assert.Equal("MKCOL", stub.Requests[0].Method.Method);
    }

    [Fact]
    public async Task DeleteAsync_404_not_found_treated_as_success()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(404); // already gone
        WebDavNasConnector c = NewConnector(stub);
        await c.DeleteAsync("/photos/gone.jpg", CancellationToken.None);
        Assert.Equal(1, stub.CallCount);
        Assert.Equal("DELETE", stub.Requests[0].Method.Method);
    }

    [Fact]
    public async Task UploadFileAsync_PUTs_with_octet_stream_content_type()
    {
        var stub = new StubHttpMessageHandler();
        stub.Enqueue(201);
        WebDavNasConnector c = NewConnector(stub);
        string temp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(temp, new byte[] { 1, 2, 3 });
            await c.UploadFileAsync(temp, "/photos/up.bin", CancellationToken.None);
            Assert.Equal(1, stub.CallCount);
            Assert.Equal("PUT", stub.Requests[0].Method.Method);
            Assert.Equal("application/octet-stream",
                stub.Requests[0].Content!.Headers.ContentType!.MediaType);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<(int Status, string? Body, byte[]? Bytes, Action<HttpRequestMessage>? Inspect, bool Delay)> _responses = new();
        private readonly List<HttpRequestMessage> _requests = new();
        public int CallCount { get; private set; }
        public IReadOnlyList<HttpRequestMessage> Requests => _requests;

        public void Enqueue(int status, string? body = null, byte[]? bytes = null,
            Action<HttpRequestMessage>? inspect = null)
            => _responses.Enqueue((status, body, bytes, inspect, Delay: false));

        public void EnqueueTimeout()
            => _responses.Enqueue((0, null, null, null, Delay: true));

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            _requests.Add(request);
            if (_responses.Count == 0)
                throw new InvalidOperationException("no response queued");
            var (status, body, bytes, inspect, delay) = _responses.Dequeue();
            inspect?.Invoke(request);

            if (delay)
            {
                // Force a timeout without relying on HttpClient.Timeout: throw
                // OperationCanceledException NOT caused by the caller's token.
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                throw new OperationCanceledException("simulated timeout");
            }

            HttpContent content = bytes is not null
                ? new ByteArrayContent(bytes)
                : new StringContent(body ?? string.Empty, Encoding.UTF8, "application/xml");
            return new HttpResponseMessage((HttpStatusCode)status) { Content = content };
        }
    }
}
