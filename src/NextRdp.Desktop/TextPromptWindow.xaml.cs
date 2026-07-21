using System.Windows;

namespace NextRdp.Desktop;

public partial class TextPromptWindow : Window
{
    public TextPromptWindow(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initialValue;
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }

    public string Value => ValueBox.Text.Trim();

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (Value.Length == 0)
        {
            System.Windows.MessageBox.Show(this, "Enter a value before continuing.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
