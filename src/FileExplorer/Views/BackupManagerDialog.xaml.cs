using FileExplorer.Models;
using FileExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileExplorer.Views;

public enum BackupManagerAction
{
    None,
    RunFull,
    RunDifferential,
    Restore,
}

public sealed partial class BackupManagerDialog : ContentDialog
{
    private readonly Func<Task<string?>> _pickFolder;
    private BackupJob? _current;

    public BackupManagerDialog(Func<Task<string?>> pickFolder)
    {
        InitializeComponent();
        _pickFolder = pickFolder;
        ReloadJobs();
    }

    /// Set when the user asks to run or restore; MainWindow acts on it after the dialog closes.
    public BackupManagerAction Action { get; private set; }

    public BackupJob? SelectedJob => _current;

    private sealed record SetRow(string Line, string Size);

    private void ReloadJobs(string? selectId = null)
    {
        var jobs = BackupService.All.OrderBy(j => j.Name).ToList();
        JobList.ItemsSource = jobs;
        JobList.SelectedItem = jobs.FirstOrDefault(j => j.Id == selectId) ?? jobs.FirstOrDefault();
    }

    private void JobList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _current = JobList.SelectedItem as BackupJob;
        DetailScroller.Visibility = _current is null ? Visibility.Collapsed : Visibility.Visible;
        if (_current is null)
        {
            return;
        }

        NameBox.Text = _current.Name;
        SourceBox.Text = _current.SourceRoot;
        DestBox.Text = _current.DestinationRoot;
        VssBox.IsChecked = _current.UseVolumeShadowCopy;
        FullDaysBox.Value = _current.FullEveryDays;
        DiffDaysBox.Value = _current.DifferentialEveryDays;
        KeepBox.Value = _current.KeepFullSets;
        ScheduleBox.IsChecked = BackupScheduling.IsScheduled(_current.Id);
        InfoBar.IsOpen = false;

        LoadSets(_current);
    }

    private void LoadSets(BackupJob job)
    {
        SetList.ItemsSource = BackupService.EnumerateSets(job)
            .OrderByDescending(s => s.Manifest.TimestampUtc)
            .Select(s => new SetRow(
                $"{(s.Manifest.Type == BackupSetType.Full ? "Full" : "Diff")}  {s.Manifest.TimestampUtc.LocalDateTime:g}" +
                (s.Completed ? string.Empty : "  (incomplete)"),
                $"{s.Manifest.FileCount:N0} files"))
            .ToList();
    }

    private void NewJob_Click(object sender, RoutedEventArgs e)
    {
        var job = new BackupJob { Name = "New backup" };
        BackupService.AddOrUpdate(job);
        ReloadJobs(job.Id);
    }

    private async void BrowseSource_Click(object sender, RoutedEventArgs e)
    {
        if (await _pickFolder() is { } path)
        {
            SourceBox.Text = path;
        }
    }

    private async void BrowseDest_Click(object sender, RoutedEventArgs e)
    {
        if (await _pickFolder() is { } path)
        {
            DestBox.Text = path;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(NameBox.Text) || string.IsNullOrWhiteSpace(SourceBox.Text) || string.IsNullOrWhiteSpace(DestBox.Text))
        {
            Info(InfoBarSeverity.Warning, "Name, source and destination are all required.");
            return;
        }

        _current.Name = NameBox.Text.Trim();
        _current.SourceRoot = SourceBox.Text.Trim();
        _current.DestinationRoot = DestBox.Text.Trim();
        _current.UseVolumeShadowCopy = VssBox.IsChecked == true;
        _current.FullEveryDays = (int)FullDaysBox.Value;
        _current.DifferentialEveryDays = (int)DiffDaysBox.Value;
        _current.KeepFullSets = (int)KeepBox.Value;
        BackupService.AddOrUpdate(_current);

        BackupScheduling.SetScheduled(_current, ScheduleBox.IsChecked == true);

        ReloadJobs(_current.Id);
        Info(InfoBarSeverity.Success, "Saved.");
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null)
        {
            return;
        }

        BackupScheduling.SetScheduled(_current, false);
        BackupService.Remove(_current.Id);
        ReloadJobs();
    }

    private void RunFull_Click(object sender, RoutedEventArgs e) => CloseWith(BackupManagerAction.RunFull);

    private void RunDiff_Click(object sender, RoutedEventArgs e) => CloseWith(BackupManagerAction.RunDifferential);

    private void Restore_Click(object sender, RoutedEventArgs e) => CloseWith(BackupManagerAction.Restore);

    private void CloseWith(BackupManagerAction action)
    {
        if (_current is null)
        {
            return;
        }

        Action = action;
        Hide();
    }

    private void Info(InfoBarSeverity severity, string message)
    {
        InfoBar.Severity = severity;
        InfoBar.Message = message;
        InfoBar.IsOpen = true;
    }
}
