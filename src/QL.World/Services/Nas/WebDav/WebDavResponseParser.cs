using System.Globalization;
using System.Xml.Linq;

namespace QL.World.Services.Nas.WebDav;

/// <summary>
/// Parses WebDAV PROPFIND <c>multistatus</c> XML into <see cref="NasFileInfo"/>
/// entries. Namespace-agnostic: looks up elements by the canonical
/// <c>DAV:</c> <see cref="XNamespace"/> rather than by prefix, so 群晖's
/// <c>D:</c> prefix, 威联通's default namespace, and nextcloud's <c>d:</c>
/// all parse identically.
/// </summary>
internal static class WebDavResponseParser
{
    /// <summary>
    /// Parses the multistatus body returned by a PROPFIND request. The first
    /// <c>response</c> element is conventionally the requested path itself
    /// (the collection root); callers may pass <paramref name="skipSelf"/>
    /// <c>=true</c> to drop it (used by <c>ListFilesAsync</c> which only wants
    /// children). Returns an empty list when the body is missing or not
    /// well-formed XML.
    /// </summary>
    public static IReadOnlyList<NasFileInfo> ParseMultistatus(
        string? body,
        bool skipSelf,
        string rootPath)
    {
        if (string.IsNullOrWhiteSpace(body))
            return Array.Empty<NasFileInfo>();

        XDocument doc;
        try { doc = XDocument.Parse(body); }
        catch { return Array.Empty<NasFileInfo>(); }

        // Root element name may carry any prefix; only the namespace matters.
        XNamespace dav = WebDavRequestBuilder.Dav;
        var responses = doc.Root!
            .Elements(dav + "response")
            .ToList();

        var result = new List<NasFileInfo>(responses.Count);
        bool skippedSelf = !skipSelf; // when skipSelf=false, treat as already skipped
        string normalizedRoot = NormalizePath(rootPath);

        foreach (var response in responses)
        {
            string? href = (string?)response.Element(dav + "href");
            if (href is null)
                continue;
            string path = NormalizePath(Uri.UnescapeDataString(href));

            if (!skippedSelf && PathEquals(path, normalizedRoot))
            {
                skippedSelf = true;
                continue;
            }

            // Single propstat block is normal; multiple (with different status)
            // are legal but rare — we take the first with 2xx status or fall
            // back to the first one when none reports 2xx.
            XElement? propstat = response
                .Elements(dav + "propstat")
                .FirstOrDefault(ps => IsSuccessStatus((string?)ps.Element(dav + "status")))
                ?? response.Elements(dav + "propstat").FirstOrDefault();
            if (propstat is null)
                continue;

            XElement prop = propstat.Element(dav + "prop")!;
            bool isDir = prop.Element(dav + "resourcetype")?
                .Element(dav + "collection") is not null;

            long size = -1;
            if (long.TryParse(
                (string?)prop.Element(dav + "getcontentlength"),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out long parsedSize))
            {
                size = parsedSize;
            }

            DateTimeOffset? modified = null;
            string? lastMod = (string?)prop.Element(dav + "getlastmodified");
            if (lastMod is not null)
            {
                // RFC 1123 "R" format first (most servers — 严格校验 day-of-week);
                // fall back to loose TryParse for non-standard but readable
                // strings (e.g. some servers send "Thu" but date is actually
                // Friday; the loose parse accepts both).
                if (DateTimeOffset.TryParseExact(lastMod, "R", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsedR))
                {
                    modified = parsedR;
                }
                else if (DateTimeOffset.TryParse(lastMod, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
                {
                    modified = parsed;
                }
            }

            string? contentType = (string?)prop.Element(dav + "getcontenttype");

            result.Add(new NasFileInfo
            {
                Path = path,
                Name = GetFileName(path),
                IsDirectory = isDir,
                Size = isDir ? 0 : size,
                ModifiedAt = modified,
                ContentType = contentType,
            });
        }

        return result;
    }

    /// <summary>Normalize a WebDAV href to a forward-slash absolute path, no trailing slash (root kept as "/").</summary>
    public static string NormalizePath(string href)
    {
        string path = href.Trim();
        // Some servers return absolute URLs (http://host/path) — strip the scheme/host.
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(path, UriKind.Absolute, out Uri? uri))
                path = uri.AbsolutePath;
        }
        path = path.Replace('\\', '/');
        if (path.Length > 1 && path.EndsWith('/'))
            path = path[..^1];
        if (string.IsNullOrEmpty(path))
            path = "/";
        return path;
    }

    public static string GetFileName(string path)
    {
        int idx = path.LastIndexOf('/');
        return idx < 0 || idx == path.Length - 1 ? path : path[(idx + 1)..];
    }

    private static bool PathEquals(string a, string b) =>
        string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsSuccessStatus(string? status)
    {
        if (string.IsNullOrEmpty(status))
            return false;
        // e.g. "HTTP/1.1 200 OK" — the second token is the code.
        var parts = status.Split(' ');
        return parts.Length >= 2 &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int code) &&
            code is >= 200 and < 300;
    }
}
