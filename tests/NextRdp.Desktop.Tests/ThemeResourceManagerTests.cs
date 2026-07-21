using System.Windows;
using System.Windows.Media;
using NextRdp.Desktop;

namespace NextRdp.Desktop.Tests;

public sealed class ThemeResourceManagerTests
{
    [Fact]
    public void HighContrastPaletteUsesWindowsSystemColorsAndSuppressesWatermark()
    {
        var resources = new ResourceDictionary();

        ThemeResourceManager.ApplyHighContrast(resources);

        Assert.Same(SystemColors.WindowBrush, resources["AppBackground"]);
        Assert.Same(SystemColors.WindowTextBrush, resources["PrimaryText"]);
        Assert.Same(SystemColors.HighlightBrush, resources["ButtonHoverBackground"]);
        Assert.Same(SystemColors.HighlightTextBrush, resources["ButtonHoverText"]);
        Assert.Same(SystemColors.WindowTextBrush, resources["FocusBorder"]);
        Assert.Same(SystemColors.WindowBrush, resources["ComboBoxBackground"]);
        Assert.Same(SystemColors.WindowTextBrush, resources["ComboBoxText"]);
        Assert.Same(Brushes.Transparent, resources["ProductionWatermarkBrush"]);
    }

    [Theory]
    [InlineData(true, "#0B1018", "#F4F7FB")]
    [InlineData(false, "#F3F6FA", "#172033")]
    public void StandardPaletteKeepsReadableThemePairs(bool dark, string background, string foreground)
    {
        var resources = new ResourceDictionary();

        ThemeResourceManager.ApplyStandard(resources, dark);

        Assert.Equal((Color)ColorConverter.ConvertFromString(background), Assert.IsType<SolidColorBrush>(resources["AppBackground"]).Color);
        Assert.Equal((Color)ColorConverter.ConvertFromString(foreground), Assert.IsType<SolidColorBrush>(resources["PrimaryText"]).Color);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StandardThemeTextPairsMeetNormalTextContrast(bool dark)
    {
        var resources = new ResourceDictionary();
        ThemeResourceManager.ApplyStandard(resources, dark);

        AssertContrast(resources, "PrimaryText", "AppBackground");
        AssertContrast(resources, "PrimaryText", "ButtonBackground");
        AssertContrast(resources, "PrimaryText", "ItemBackground");
        AssertContrast(resources, "SecondaryText", "AppBackground");
        AssertContrast(resources, "SecurityText", "StatusBackground");
        AssertContrast(resources, "FavoriteText", "ItemBackground");
        AssertContrast(resources, "AccentText", "Accent");
        AssertContrast(resources, "ButtonHoverText", "ButtonHoverBackground");
        AssertContrast(resources, "FocusBorder", "ButtonBackground", 3);
        AssertContrast(resources, "FocusBorder", "ButtonHoverBackground", 3);
        AssertContrast(resources, "ComboBoxText", "ComboBoxBackground");
    }

    private static void AssertContrast(ResourceDictionary resources, string foregroundKey, string backgroundKey, double minimum = 4.5)
    {
        var foreground = Assert.IsType<SolidColorBrush>(resources[foregroundKey]).Color;
        var background = Assert.IsType<SolidColorBrush>(resources[backgroundKey]).Color;
        var lighter = Math.Max(RelativeLuminance(foreground), RelativeLuminance(background));
        var darker = Math.Min(RelativeLuminance(foreground), RelativeLuminance(background));
        Assert.True((lighter + 0.05) / (darker + 0.05) >= minimum,
            $"{foregroundKey} on {backgroundKey} does not meet {minimum}:1 contrast.");
    }

    private static double RelativeLuminance(Color color) =>
        0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

    private static double Linear(byte value)
    {
        var channel = value / 255d;
        return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }
}
