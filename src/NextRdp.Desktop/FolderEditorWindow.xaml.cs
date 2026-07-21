using System.Windows;
using NextRdp.Core;
using MessageBox = System.Windows.MessageBox;

namespace NextRdp.Desktop;

public partial class FolderEditorWindow : Window
{
    public FolderEditorWindow(IReadOnlyList<FolderChoice> choices, ConnectionFolder? folder = null)
    {
        InitializeComponent();
        Title = folder is null ? "New folder" : $"Edit {folder.Name}";
        NameBox.Text = folder?.Name ?? string.Empty;
        ParentBox.ItemsSource = choices;
        ParentBox.SelectedItem = choices.FirstOrDefault(x => x.Id == folder?.ParentId) ?? choices[0];
        Loaded += (_, _) => NameBox.Focus();
    }

    public string FolderName => NameBox.Text.Trim();
    public Guid? ParentId => (ParentBox.SelectedItem as FolderChoice)?.Id;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (FolderName.Length == 0)
        {
            MessageBox.Show(this, "Folder name is required.", "Check folder", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
