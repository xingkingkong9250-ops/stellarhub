using System.Globalization;
using System.Windows.Media;
using QL.World.Views.Converters;

namespace QL.World.Tests;

/// <summary>U4-A (B3-4 主色调)：HexBrushConverter 合法 hex 转 Brush，非法输入回落 Transparent。</summary>
public sealed class HexBrushConverterTests
{
    private readonly HexBrushConverter _converter = new();

    [Fact]
    public void Valid_hex_returns_brush_with_matching_color()
    {
        var brush = Assert.IsType<SolidColorBrush>(
            _converter.Convert("#3A7BD5", typeof(object), null, CultureInfo.InvariantCulture));

        Assert.Equal(Color.FromRgb(0x3A, 0x7B, 0xD5), brush.Color);
    }

    [Theory]
    [InlineData("#GG0000")]
    [InlineData("#12345")]
    [InlineData("not-a-color")]
    public void Invalid_hex_returns_transparent(string hex)
    {
        var brush = Assert.IsType<SolidColorBrush>(
            _converter.Convert(hex, typeof(object), null, CultureInfo.InvariantCulture));

        Assert.Equal(Colors.Transparent, brush.Color);
    }

    [Fact]
    public void Non_string_returns_transparent()
    {
        var brush = Assert.IsType<SolidColorBrush>(
            _converter.Convert(null, typeof(object), null, CultureInfo.InvariantCulture));

        Assert.Equal(Colors.Transparent, brush.Color);
    }
}
