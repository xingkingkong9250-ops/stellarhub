using System.Xml.Linq;

namespace QL.World.Services.Nas.WebDav;

/// <summary>
/// Builds WebDAV request bodies (PROPFIND/PROPPATCH XML) using
/// <see cref="XDocument"/> instead of string concatenation. The XML is
/// emitted with the canonical <c>DAV:</c> namespace so it parses on every
/// standards-compliant WebDAV server (群晖 <c>D:</c> prefix, 威联通 default
/// namespace, nextcloud <c>d:</c>, etc.). Response parsing handles prefix
/// variance in <see cref="WebDavResponseParser"/>.
/// </summary>
internal static class WebDavRequestBuilder
{
    /// <summary>
    /// Standard DAV: namespace; never hardcode the <c>D:</c> prefix.
    /// </summary>
    public static readonly XNamespace Dav = "DAV:";

    /// <summary>
    /// Builds a PROPFIND request body asking for getcontentlength /
    /// getlastmodified / resourcetype / getcontenttype. Empty <c>prop</c>
    /// elements mean "named properties only" — servers fill in values or
    /// omit the element when unsupported.
    /// </summary>
    public static string BuildPropFindBody()
    {
        XDocument doc = new(new XElement(Dav + "propfind",
            new XElement(Dav + "prop",
                new XElement(Dav + "getcontentlength"),
                new XElement(Dav + "getlastmodified"),
                new XElement(Dav + "resourcetype"),
                new XElement(Dav + "getcontenttype"))));
        return doc.ToString(SaveOptions.DisableFormatting);
    }
}
