using System.Windows;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfSystemColors = System.Windows.SystemColors;

namespace NextRdp.Desktop;

internal static class ThemeResourceManager
{
    public static void ApplyStandard(ResourceDictionary resources, bool dark)
    {
        SetColor(resources, "AppBackground", dark ? "#0B1018" : "#F3F6FA");
        SetColor(resources, "PanelBackground", dark ? "#121A26" : "#FFFFFF");
        SetColor(resources, "PrimaryText", dark ? "#F4F7FB" : "#172033");
        SetColor(resources, "SecondaryText", dark ? "#9EACBF" : "#55657A");
        SetColor(resources, "TopBarBackground", dark ? "#0E1520" : "#E8EEF6");
        SetColor(resources, "BorderColor", dark ? "#26364B" : "#C7D1DF");
        SetColor(resources, "ItemBackground", dark ? "#172131" : "#E9EFF7");
        SetColor(resources, "InsetBackground", dark ? "#0D141E" : "#F7F9FC");
        SetColor(resources, "SessionBackground", dark ? "#080C12" : "#E3EAF3");
        SetColor(resources, "StatusBackground", dark ? "#0A1018" : "#E8EEF6");
        SetColor(resources, "ButtonBackground", dark ? "#223149" : "#D7E2F0");
        SetColor(resources, "ButtonHoverBackground", dark ? "#334A6B" : "#B9D2EE");
        SetColor(resources, "ButtonPressedBackground", dark ? "#41618E" : "#9EBFDF");
        SetColor(resources, "ButtonHoverText", dark ? "#FFFFFF" : "#172033");
        SetColor(resources, "FocusBorder", dark ? "#F4F7FB" : "#172033");
        SetColor(resources, "ComboBoxBackground", "#E8EEF6");
        SetColor(resources, "ComboBoxText", "#172033");
        SetColor(resources, "Accent", "#5EA0FF");
        SetColor(resources, "AccentText", "#08111F");
        SetColor(resources, "SecurityText", dark ? "#36A06B" : "#16794E");
        SetColor(resources, "FavoriteText", dark ? "#FFBE4D" : "#7A4B00");
        SetColor(resources, "ProductionWatermarkBrush", "#66FF5C5C");
    }

    public static void ApplyHighContrast(ResourceDictionary resources)
    {
        SetBrush(resources, "AppBackground", WpfSystemColors.WindowBrush);
        SetBrush(resources, "PanelBackground", WpfSystemColors.WindowBrush);
        SetBrush(resources, "PrimaryText", WpfSystemColors.WindowTextBrush);
        SetBrush(resources, "SecondaryText", WpfSystemColors.WindowTextBrush);
        SetBrush(resources, "TopBarBackground", WpfSystemColors.WindowBrush);
        SetBrush(resources, "BorderColor", WpfSystemColors.ActiveBorderBrush);
        SetBrush(resources, "ItemBackground", WpfSystemColors.WindowBrush);
        SetBrush(resources, "InsetBackground", WpfSystemColors.WindowBrush);
        SetBrush(resources, "SessionBackground", WpfSystemColors.WindowBrush);
        SetBrush(resources, "StatusBackground", WpfSystemColors.WindowBrush);
        SetBrush(resources, "ButtonBackground", WpfSystemColors.ControlBrush);
        SetBrush(resources, "ButtonHoverBackground", WpfSystemColors.HighlightBrush);
        SetBrush(resources, "ButtonPressedBackground", WpfSystemColors.HighlightBrush);
        SetBrush(resources, "ButtonHoverText", WpfSystemColors.HighlightTextBrush);
        SetBrush(resources, "FocusBorder", WpfSystemColors.WindowTextBrush);
        SetBrush(resources, "ComboBoxBackground", WpfSystemColors.WindowBrush);
        SetBrush(resources, "ComboBoxText", WpfSystemColors.WindowTextBrush);
        SetBrush(resources, "Accent", WpfSystemColors.HighlightBrush);
        SetBrush(resources, "AccentText", WpfSystemColors.HighlightTextBrush);
        SetBrush(resources, "SecurityText", WpfSystemColors.WindowTextBrush);
        SetBrush(resources, "FavoriteText", WpfSystemColors.WindowTextBrush);
        SetBrush(resources, "ProductionWatermarkBrush", WpfBrushes.Transparent);
    }

    private static void SetColor(ResourceDictionary resources, string key, string value) =>
        SetBrush(resources, key, new WpfSolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(value)));

    private static void SetBrush(ResourceDictionary resources, string key, System.Windows.Media.Brush brush) => resources[key] = brush;
}
