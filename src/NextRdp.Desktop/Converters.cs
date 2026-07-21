using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using NextRdp.Core;
using MediaColor = System.Windows.Media.Color;

namespace NextRdp.Desktop;

public sealed class EnvironmentBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        ConnectionEnvironment.Production => new SolidColorBrush(MediaColor.FromRgb(255, 92, 92)),
        ConnectionEnvironment.Test => new SolidColorBrush(MediaColor.FromRgb(255, 190, 77)),
        ConnectionEnvironment.Development => new SolidColorBrush(MediaColor.FromRgb(94, 160, 255)),
        ConnectionEnvironment.DisasterRecovery => new SolidColorBrush(MediaColor.FromRgb(190, 112, 255)),
        _ => new SolidColorBrush(MediaColor.FromRgb(110, 214, 164))
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class TagsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is IEnumerable<string> tags ? string.Join("  •  ", tags) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class RecentConnectionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        DateTimeOffset timestamp => $"Last used {timestamp.ToLocalTime():g}",
        _ => "Never connected"
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ConnectionAccessibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ConnectionProfile connection
            ? $"{connection.Name}, {connection.Host}, port {connection.Port}, {connection.Environment}, {(connection.IsFavorite ? "favorite" : "not favorite")}, {(connection.LastConnectedAtUtc is null ? "never connected" : "recently used")}"
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class HostPortConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ConnectionProfile connection ? Format(connection) : string.Empty;

    internal static string Format(ConnectionProfile connection)
    {
        var host = connection.Host.Contains(':', StringComparison.Ordinal) ? $"[{connection.Host}]" : connection.Host;
        return $"{host}:{connection.Port.ToString(CultureInfo.InvariantCulture)}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ConnectionOperationalDetailsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length == 0 || values[0] is not ConnectionProfile connection) return string.Empty;
        var isManaged = values.Length > 1 && values[1] is MainViewModel viewModel && viewModel.IsManagedConnection(connection);
        var visibleTags = connection.Tags
            .Where(tag => !isManaged || !string.Equals(tag, "Managed", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var tags = visibleTags.Length == 0 ? "None" : string.Join(", ", visibleTags);
        var lastUsed = connection.LastConnectedAtUtc is { } timestamp
            ? timestamp.ToLocalTime().ToString("g", culture)
            : "Never";

        return string.Join(System.Environment.NewLine,
            connection.Name,
            HostPortConverter.Format(connection),
            $"Environment: {connection.Environment}",
            $"Source: {(isManaged ? "Managed workspace" : "Local inventory")}",
            $"Tags: {tags}",
            $"Last used: {lastUsed}");
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ManagedConnectionVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length > 1 && values[0] is ConnectionProfile connection && values[1] is MainViewModel viewModel && viewModel.IsManagedConnection(connection)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
