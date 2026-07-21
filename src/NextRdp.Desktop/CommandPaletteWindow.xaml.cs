using System.Windows;
using System.Windows.Input;

namespace NextRdp.Desktop;

public sealed record CommandPaletteItem(string Label, string Shortcut, Func<Task> Execute)
{
    public override string ToString() => string.IsNullOrWhiteSpace(Shortcut) ? Label : $"{Label}    {Shortcut}";
}

public partial class CommandPaletteWindow : Window
{
    private readonly IReadOnlyList<CommandPaletteItem> commands;

    public CommandPaletteWindow(IReadOnlyList<CommandPaletteItem> commands)
    {
        InitializeComponent();
        this.commands = commands;
        Refresh();
        Loaded += (_, _) => FilterBox.Focus();
    }

    private void FilterBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (CommandList is null) return;
        var query = FilterBox?.Text.Trim() ?? string.Empty;
        CommandList.ItemsSource = commands.Where(command => query.Length == 0 || command.Label.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (CommandList.Items.Count > 0) CommandList.SelectedIndex = 0;
    }

    private async Task ExecuteSelectedAsync()
    {
        if (CommandList.SelectedItem is not CommandPaletteItem command) return;
        Close();
        await command.Execute();
    }

    private async void CommandList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await ExecuteSelectedAsync();

    private async void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; await ExecuteSelectedAsync(); }
        else if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.Down && CommandList.Items.Count > 0) { CommandList.SelectedIndex = Math.Min(CommandList.SelectedIndex + 1, CommandList.Items.Count - 1); CommandList.ScrollIntoView(CommandList.SelectedItem); e.Handled = true; }
        else if (e.Key == Key.Up && CommandList.Items.Count > 0) { CommandList.SelectedIndex = Math.Max(CommandList.SelectedIndex - 1, 0); CommandList.ScrollIntoView(CommandList.SelectedItem); e.Handled = true; }
    }
}
