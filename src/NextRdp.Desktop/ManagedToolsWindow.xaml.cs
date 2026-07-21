using System.Windows;
using NextRdp.Core;
using NextRdp.Platform.Application;

namespace NextRdp.Desktop;

public partial class ManagedToolsWindow : Window
{
    private readonly MainViewModel viewModel;
    private readonly ConnectionProfile? selected;

    public ManagedToolsWindow(MainViewModel viewModel, ConnectionProfile? selected)
    {
        InitializeComponent();
        this.viewModel = viewModel;
        this.selected = selected;
        DataContext = viewModel;
        PolicySummary.Text = selected is null ? "Select a managed connection in the main window to inspect its effective policy." : viewModel.ManagedPolicySummary(selected);
        CopyNameBox.Text = selected is null ? string.Empty : $"{selected.Name} copy";
        if (viewModel.ManagedTemplates.Count > 0) TemplateBox.SelectedIndex = 0;
    }

    private async void CreateTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (TemplateBox.SelectedItem is not ConnectionTemplateDto template || !int.TryParse(PortBox.Text, out var port))
        {
            System.Windows.MessageBox.Show(this, "Choose a template and enter a numeric port.", "Create from template", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            await viewModel.CreateFromTemplateAsync(template, NameBox.Text.Trim(), HostBox.Text.Trim(), port, UsernameBox.Text.Trim());
            System.Windows.MessageBox.Show(this, "The personal managed profile was created without credentials.", "Profile created", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (MainWindow.IsRecoverableUserOperationFailure(exception))
        {
            System.Windows.MessageBox.Show(this, exception.Message, "Profile could not be created", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (selected is null || !viewModel.IsManagedConnection(selected))
        {
            System.Windows.MessageBox.Show(this, "Select a managed connection in the main window first.", "Safe copy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            await viewModel.CopyManagedConnectionAsync(selected, CopyNameBox.Text.Trim());
            System.Windows.MessageBox.Show(this, "The personal copy was created without credentials, secret references, or history.", "Copy created", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception) when (MainWindow.IsRecoverableUserOperationFailure(exception))
        {
            System.Windows.MessageBox.Show(this, exception.Message, "Copy could not be created", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
