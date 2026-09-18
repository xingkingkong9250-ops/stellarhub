namespace QL.World.Models;

/// <summary>
/// AI analysis operating mode (W3/A3).
/// Defaults to <see cref="Disabled"/> — AI is a pure add-on capability.
/// </summary>
public enum AiMode
{
    /// <summary>AI features disabled; core library functions unaffected.</summary>
    Disabled = 0,

    /// <summary>Local ONNX model, fully offline (M3-D).</summary>
    Local = 1,

    /// <summary>OpenAI-compatible cloud API over HTTPS.</summary>
    Cloud = 2,
}
