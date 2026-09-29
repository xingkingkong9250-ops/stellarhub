namespace QL.World.Services.Nas;

/// <summary>
/// Raised by <see cref="INasConnector"/> and <see cref="IFileSystemAccessor"/>
/// implementations for connection / authentication / protocol failures.
/// <see cref="Kind"/> categorizes the cause so callers can branch on retryable
/// vs. fatal without parsing messages; <see cref="Exception.InnerException"/>
/// (when present) carries the underlying
/// <see cref="System.Net.Http.HttpRequestException"/> /
/// <see cref="System.Threading.Tasks.TaskCanceledException"/> for diagnostics.
/// </summary>
public sealed class NasConnectionException : Exception
{
    /// <summary>Failure category; never <c>default</c>.</summary>
    public NasConnectionKind Kind { get; }

    public NasConnectionException(NasConnectionKind kind, string message, Exception? inner = null)
        : base(message, inner)
    {
        Kind = kind;
    }
}
