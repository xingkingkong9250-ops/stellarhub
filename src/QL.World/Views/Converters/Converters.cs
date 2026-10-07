using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace QL.World.Views.Converters;

/// <summary>bool → Visibility. Parameter "Invert" flips; "Hidden" hides instead of collapsing.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>Default parameter used when a binding does not supply ConverterParameter.</summary>
    public string? Parameter { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        string mode = parameter as string ?? Parameter ?? string.Empty;
        if (mode.Contains("Invert", StringComparison.OrdinalIgnoreCase))
            flag = !flag;
        var hidden = mode.Contains("Hidden", StringComparison.OrdinalIgnoreCase)
            ? Visibility.Hidden
            : Visibility.Collapsed;
        return flag ? Visibility.Visible : hidden;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility v && v == Visibility.Visible;
}

/// <summary>Null (or empty string) → Collapsed; any non-null → Visible. Parameter "Invert".</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasValue = value is string s ? s.Length > 0 : value is not null;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
            hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Zero integer → Visible (empty-library hint); non-zero → Collapsed.</summary>
public sealed class ZeroToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool isZero = value switch
        {
            int i => i == 0,
            long l => l == 0,
            _ => true,
        };
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
            isZero = !isZero;
        return isZero ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Numeric threshold: true when the bound number is less than ConverterParameter.
/// Used by responsive-layout triggers (e.g. compact top bar under 1500 logical px).
/// </summary>
public sealed class NumericThresholdConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not IConvertible number || parameter is null ||
            !double.TryParse(parameter.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double threshold))
            return false;
        try { return number.ToDouble(CultureInfo.InvariantCulture) < threshold; }
        catch { return false; }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>[rating, starIndex] → true when rating ≥ star.</summary>
public sealed class RatingStarConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return false;
        int rating = ToInt(values[0]);
        int star = ToInt(values[1]);
        return rating >= star;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static int ToInt(object? value) => value switch
    {
        int i => i,
        _ => 0,
    };
}

/// <summary>
/// Compares the bound enum value to the ConverterParameter enum value.
/// Returns true when equal — used for RadioButton.IsChecked binding.
/// </summary>
public sealed class EnumEqualityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is null) return false;
        try
        {
            object param = Enum.Parse(value.GetType(), parameter.ToString()!);
            return value.Equals(param);
        }
        catch
        {
            return false;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is bool b && b && parameter is not null)
        {
            try { return Enum.Parse(targetType, parameter.ToString()!); }
            catch { return Binding.DoNothing; }
        }
        return Binding.DoNothing;
    }
}

/// <summary>bool → text. ConverterParameter format: "TrueText|FalseText".</summary>
public sealed class BoolToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool flag = value is bool b && b;
        string param = parameter as string ?? string.Empty;
        string[] parts = param.Split('|');
        if (parts.Length == 2)
            return flag ? parts[0] : parts[1];
        return flag ? "是" : "否";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>U4-A (B3-4 主色调)："#RRGGBB" → SolidColorBrush；非法输入 → Transparent。</summary>
public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string hex)
        {
            try
            {
                if (ColorConverter.ConvertFromString(hex) is Color color)
                {
                    var brush = new SolidColorBrush(color);
                    brush.Freeze();
                    return brush;
                }
            }
            catch (FormatException)
            {
                // 非法 hex 落到 Transparent
            }
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Star foreground: active → AccentDim, idle → TextMuted.</summary>
public sealed class RatingStarBrushConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 3) return Brushes.Gray;
        int rating = values[0] is int r ? r : 0;
        int star = values[1] is int s ? s : 0;
        if (values[2] is not FrameworkElement element) return Brushes.Gray;
        object? brush = element.TryFindResource(rating >= star ? "AccentDimBrush" : "TextMutedBrush");
        return brush ?? Brushes.Gray;
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
