namespace QL.World.Models;

/// <summary>
/// Where an image row's bytes live. <see cref="Local"/> rows point at a
/// path on the local file system; <see cref="Remote"/> rows point at a
/// canonical <c>nas://{connectionId}/{remotePath}</c> identifier whose bytes
/// are fetched on demand through an <see cref="QL.World.Services.Nas.INasConnector"/>.
/// Stored in <c>images.source</c> as the lower-cased enum name.
/// </summary>
public enum ImageSource
{
    Local,
    Remote,
}
