using QL.World.Services.Colors;

namespace QL.World.Tests;

/// <summary>U3-A (§4.B.2): hue-family mapping boundaries of ColorFamily.HexToFamily.</summary>
public sealed class ColorFamilyTests
{
    [Theory]
    [InlineData("#FF0000", ColorFamily.Red)]
    [InlineData("#E04030", ColorFamily.Red)]     // H≈5.7°
    [InlineData("#FF0040", ColorFamily.Magenta)] // H≈344.9°, just below the 345° red wrap
    [InlineData("#FF002B", ColorFamily.Red)]     // H≈349.9°, red wraps past 345°
    [InlineData("#BF3F00", ColorFamily.Orange)]  // H≈19.8°, red/orange boundary is 15°
    [InlineData("#FF8000", ColorFamily.Orange)]  // H=30°
    [InlineData("#FFFF00", ColorFamily.Yellow)]  // H=60° — pure yellow is 黄
    [InlineData("#00FF00", ColorFamily.Green)]   // H=120°
    [InlineData("#80FF00", ColorFamily.Green)]   // H=90°, green sector is 75–165°
    [InlineData("#00FF80", ColorFamily.Green)]   // H=150°
    [InlineData("#00FFFF", ColorFamily.Cyan)]    // H=180°
    [InlineData("#0080FF", ColorFamily.Blue)]    // H=210°
    [InlineData("#0000FF", ColorFamily.Blue)]    // H=240°
    [InlineData("#8000FF", ColorFamily.Purple)]  // H=270°
    [InlineData("#FF00FF", ColorFamily.Magenta)] // H=300°
    [InlineData("#FF0080", ColorFamily.Magenta)] // H=330°
    public void Hue_sectors_map_to_expected_family(string hex, string expected)
    {
        Assert.Equal(expected, ColorFamily.HexToFamily(hex));
    }

    [Theory]
    [InlineData("#808080")]  // S=0 → gray
    [InlineData("#C8C8C8")]  // S=0 → gray
    [InlineData("#000000")]  // V=0 → gray
    [InlineData("#1A1A1A")]  // V≈0.10 < 0.15 → gray
    [InlineData("#B8B4B4")]  // S≈0.022 < 0.18 → gray
    public void Low_saturation_or_dark_colors_collapse_to_gray(string hex)
    {
        Assert.Equal(ColorFamily.Gray, ColorFamily.HexToFamily(hex));
    }

    [Fact]
    public void Saturation_threshold_is_strict()
    {
        // S = (255-209)/255 ≈ 0.1804 ≥ 0.18 → not gray; H=0 → red.
        Assert.Equal(ColorFamily.Red, ColorFamily.HexToFamily("#FFD1D1"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#12345")]    // too short
    [InlineData("#1234567")]  // too long
    [InlineData("GGGGGG")]    // non-hex
    public void Malformed_input_returns_null(string? hex)
    {
        Assert.Null(ColorFamily.HexToFamily(hex));
    }

    [Fact]
    public void Leading_hash_is_optional_and_case_insensitive()
    {
        Assert.Equal(ColorFamily.Blue, ColorFamily.HexToFamily("0000ff"));
        Assert.Equal(ColorFamily.Blue, ColorFamily.HexToFamily("#0000FF"));
    }
}
