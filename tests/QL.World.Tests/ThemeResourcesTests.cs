using System;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using Xunit;

namespace QL.World.Tests;

public sealed class ThemeResourcesTests
{
    private static readonly string[] ExpectedBrushKeys =
    [
        "BackgroundBrush",
        "SurfaceBrush",
        "SurfaceVariantBrush",
        "SurfaceHoverBrush",
        "SurfaceActiveBrush",
        "BorderBrush",
        "BorderSubtleBrush",
        "TextPrimaryBrush",
        "TextSecondaryBrush",
        "TextMutedBrush",
        "AccentBrush",
        "AccentDimBrush",
        "AccentBgBrush",
        "DangerBrush",
        "DangerBgBrush",
        "WarningBrush",
        "WarningBgBrush",
    ];

    [Theory]
    [InlineData("Themes/Dark.xaml")]
    [InlineData("Themes/Light.xaml")]
    public void Theme_contains_all_expected_brush_keys(string relativePath)
    {
        string basePath = AppDomain.CurrentDomain.BaseDirectory;
        // Walk upward to find the solution root (contains src/ and tests/).
        string? root = basePath;
        for (int i = 0; i < 8 && root is not null; i++)
        {
            string candidate = Path.Combine(root, "src", "QL.World");
            if (Directory.Exists(candidate))
                break;
            root = Path.GetDirectoryName(root);
        }
        Assert.NotNull(root);
        string fullPath = Path.Combine(root!, "src", "QL.World",
            relativePath.Replace('/', Path.DirectorySeparatorChar));

        var dict = (ResourceDictionary)XamlReader.Load(
            new FileStream(fullPath, FileMode.Open, FileAccess.Read));

        foreach (string key in ExpectedBrushKeys)
        {
            Assert.True(dict.Contains(key),
                $"Theme '{relativePath}' is missing brush key '{key}'.");
        }
    }
}
