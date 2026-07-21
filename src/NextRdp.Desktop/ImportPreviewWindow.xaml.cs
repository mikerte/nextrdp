using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using NextRdp.Core;
using MessageBox = System.Windows.MessageBox;

namespace NextRdp.Desktop;

public partial class ImportPreviewWindow : Window
{
    public ImportPreviewWindow(ImportPreview preview, Func<ImportCandidate, bool> duplicateCheck)
    {
        InitializeComponent();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Items = new ObservableCollection<ImportPreviewItem>(preview.Candidates.Select(candidate =>
        {
            var duplicate = duplicateCheck(candidate) || !seen.Add(ConnectionIdentity.CreateKey(candidate.Host, candidate.Port));
            return new ImportPreviewItem(candidate, !duplicate && candidate.Warnings.Count == 0, duplicate);
        }));
        PreviewGrid.ItemsSource = Items;
        SummaryText.Text = $"{preview.Candidates.Count} candidate(s) found. {preview.SecretFieldsDiscarded} sensitive field(s) were removed before this preview. Existing host/port pairs are marked as duplicates and left unselected.";
    }

    public ObservableCollection<ImportPreviewItem> Items { get; }
    public IReadOnlyList<ImportCandidate> SelectedCandidates => Items.Where(x => x.IsSelected).Select(x => x.Candidate).ToArray();

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Items) item.IsSelected = item.CanImport;
        PreviewGrid.Items.Refresh();
    }

    private void SelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in Items) item.IsSelected = false;
        PreviewGrid.Items.Refresh();
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCandidates.Count == 0)
        {
            MessageBox.Show(this, "Select at least one new connection to import.", "Import preview", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}

public sealed partial class ImportPreviewItem : ObservableObject
{
    public ImportPreviewItem(ImportCandidate candidate, bool isSelected, bool isDuplicate)
    {
        Candidate = candidate;
        this.isSelected = isSelected;
        IsDuplicate = isDuplicate;
    }

    public ImportCandidate Candidate { get; }
    [ObservableProperty] private bool isSelected;
    public bool IsDuplicate { get; }
    public bool CanImport => !IsDuplicate && Candidate.Warnings.Count == 0;
    public string Target => $"{Candidate.Host}:{Candidate.Port}";
    public string FolderPath => Candidate.FolderPath.Count == 0 ? "No folder" : string.Join(" / ", Candidate.FolderPath);
    public string Status => IsDuplicate ? "Duplicate" : Candidate.Warnings.Count == 0 ? "Ready" : string.Join("; ", Candidate.Warnings);
    public string AccessibilityName => $"{Candidate.Name}, {Target}, {FolderPath}, {Status}, {(IsSelected ? "selected for import" : "not selected for import")}";

    partial void OnIsSelectedChanged(bool value) => OnPropertyChanged(nameof(AccessibilityName));
}
