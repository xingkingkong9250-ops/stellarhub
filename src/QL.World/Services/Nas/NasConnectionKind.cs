namespace QL.World.Services.Nas;

/// <summary>
/// Categorizes <see cref="NasConnectionException"/> failures so callers can
/// branch on retryable vs. fatal causes without parsing exception messages.
/// Mapped from underlying HTTP status / socket error in <c>WebDavNasConnector</c> (M5-B).
/// </summary>
public enum NasConnectionKind
{
    /// <summary>HTTP 401/403 or wrong credentials.</summary>
    AuthenticationFailed,

    /// <summary>Request exceeded the configured timeout window.</summary>
    Timeout,

    /// <summary>DNS resolution, TCP connect, or TLS handshake failure.</summary>
    NetworkUnreachable,

    /// <summary>Server returned 416 Range Not Satisfiable after a retry.</summary>
    RangeNotSatisfiable,

    /// <summary>Server returned 404 / path does not exist.</summary>
    NotFound,

    /// <summary>Unexpected HTTP status or protocol-level error (5xx, malformed response).</summary>
    ProtocolError,

    /// <summary>Anything not covered above (XML parsing, I/O, cancellation surfaced as fault).</summary>
    Unknown,
}
