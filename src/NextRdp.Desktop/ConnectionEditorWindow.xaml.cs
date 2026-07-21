using System.Windows;
using NextRdp.Core;
using MessageBox = System.Windows.MessageBox;

namespace NextRdp.Desktop;

public partial class ConnectionEditorWindow : Window
{
    private readonly ConnectionProfile original;

    public ConnectionEditorWindow(IReadOnlyList<FolderChoice> folderChoices, ConnectionProfile? profile = null)
    {
        InitializeComponent();
        original = profile ?? new ConnectionProfile();
        Title = profile is null ? "New connection" : $"Edit {profile.Name}";
        NameBox.Text = original.Name;
        HostBox.Text = original.Host;
        PortBox.Text = original.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        UsernameBox.Text = original.UsernameHint ?? string.Empty;
        TagsBox.Text = string.Join(", ", original.Tags);
        NotesBox.Text = original.Notes;
        FavoriteBox.IsChecked = original.IsFavorite;
        EnvironmentBox.ItemsSource = Enum.GetValues<ConnectionEnvironment>();
        EnvironmentBox.SelectedItem = original.Environment;
        FolderBox.ItemsSource = folderChoices;
        FolderBox.SelectedItem = folderChoices.FirstOrDefault(x => x.Id == original.FolderId) ?? folderChoices[0];
        NlaBox.IsChecked = original.Settings.RequireNetworkLevelAuthentication;
        SmartSizingBox.IsChecked = original.Settings.SmartSizing;
        ClipboardBox.IsChecked = original.Settings.RedirectClipboard;
        DrivesBox.IsChecked = original.Settings.RedirectDrives;
        PrintersBox.IsChecked = original.Settings.RedirectPrinters;
        AudioBox.IsChecked = original.Settings.RedirectAudio;
        ProductionWatermarkBox.IsChecked = original.Settings.ShowProductionWatermark;
        ReconnectBox.Text = original.Settings.ReconnectAttempts.ToString(System.Globalization.CultureInfo.InvariantCulture);
        ColorDepthBox.ItemsSource = new[] { 15, 16, 24, 32 };
        ColorDepthBox.SelectedItem = original.Settings.ColorDepth;
        Loaded += (_, _) => NameBox.Focus();
    }

    public ConnectionProfile? Result { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text, out var port) || !int.TryParse(ReconnectBox.Text, out var reconnectAttempts))
        {
            MessageBox.Show(this, "Port and reconnect attempts must be whole numbers.", "Check connection", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var profile = new ConnectionProfile
        {
            Id = original.Id,
            CreatedAtUtc = original.CreatedAtUtc,
            LastConnectedAtUtc = original.LastConnectedAtUtc,
            Name = NameBox.Text.Trim(),
            Host = HostBox.Text.Trim(),
            Port = port,
            UsernameHint = string.IsNullOrWhiteSpace(UsernameBox.Text) ? null : UsernameBox.Text.Trim(),
            FolderId = (FolderBox.SelectedItem as FolderChoice)?.Id,
            Environment = EnvironmentBox.SelectedItem is ConnectionEnvironment environment ? environment : ConnectionEnvironment.Development,
            Notes = NotesBox.Text.Trim(),
            IsFavorite = FavoriteBox.IsChecked == true,
            Tags = TagsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            Settings = new RdpSettings
            {
                RequireNetworkLevelAuthentication = NlaBox.IsChecked == true,
                SmartSizing = SmartSizingBox.IsChecked == true,
                RedirectClipboard = ClipboardBox.IsChecked == true,
                RedirectDrives = DrivesBox.IsChecked == true,
                RedirectPrinters = PrintersBox.IsChecked == true,
                RedirectAudio = AudioBox.IsChecked == true,
                ShowProductionWatermark = ProductionWatermarkBox.IsChecked == true,
                ReconnectAttempts = reconnectAttempts,
                ColorDepth = ColorDepthBox.SelectedItem is int colorDepth ? colorDepth : 32
            }
        };
        var errors = profile.Validate();
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, errors), "Check connection", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        Result = profile;
        DialogResult = true;
    }
}
