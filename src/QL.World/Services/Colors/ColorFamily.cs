using System.Globalization;

namespace QL.World.Services.Colors;

/// <summary>
/// U3-A (§4.B.2): maps a "#RRGGBB" dominant color to one of nine display
/// families (red/orange/yellow/green/cyan/blue/purple/magenta/gray) via HSV.
/// Low-saturation (S &lt; 0.18) and near-black (V &lt; 0.15) colors collapse to
/// gray; the remaining hue circle is split into eight perceptual sectors
/// (yellow centered on 60°, green on 120°, etc.) with red crossing 0°.
/// Pure function — the same hex always yields the same family.
/// </summary>
public static class ColorFamily
{
    public const string Red = "red";
    public const string Orange = "orange";
    public const string Yellow = "yellow";
    public const string Green = "green";
    public const string Cyan = "cyan";
    public const string Blue = "blue";
    public const string Purple = "purple";
    public const string Magenta = "magenta";
    public const string Gray = "gray";

    /// <summary>All nine families in hue order (gray last).</summary>
    public static readonly IReadOnlyList<string> All =
        [Red, Orange, Yellow, Green, Cyan, Blue, Purple, Magenta, Gray];

    /// <summary>
    /// Converts "#RRGGBB" (the leading '#' is optional) to its family.
    /// Returns null for malformed input — callers treat null as "no family".
    /// </summary>
    public static string? HexToFamily(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
            return null;

        ReadOnlySpan<char> span = hex.AsSpan().Trim();
        if (span.StartsWith("#"))
            span = span[1..];
        if (span.Length != 6)
            return null;
        if (!int.TryParse(span, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
            return null;

        double r = ((rgb >> 16) & 0xFF) / 255.0;
        double g = ((rgb >> 8) & 0xFF) / 255.0;
        double b = (rgb & 0xFF) / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double v = max;
        double s = max <= 0 ? 0 : (max - min) / max;
        if (s < 0.18 || v < 0.15)
            return Gray;

        double delta = max - min;
        double h;
        if (delta <= 0)
            h = 0; // unreachable: s >= 0.18 implies delta > 0
        else if (max == r)
            h = 60 * (((g - b) / delta) % 6);
        else if (max == g)
            h = 60 * (((b - r) / delta) + 2);
        else
            h = 60 * (((r - g) / delta) + 4);
        if (h < 0)
            h += 360;

        // Perceptual sector bounds: pure yellow (H=60) must be 黄, not 橙.
        return h switch
        {
            < 15 => Red,
            < 45 => Orange,
            < 75 => Yellow,
            < 165 => Green,
            < 195 => Cyan,
            < 255 => Blue,
            < 285 => Purple,
            < 345 => Magenta,
            _ => Red,
        };
    }
}
