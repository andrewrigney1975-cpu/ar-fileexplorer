using FileExplorer.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FileExplorer.Views;

public sealed partial class AutomationDialog : UserControl
{
    private sealed record WatchDisplayRow(string Id, string FolderPath, string ScriptName);

    private sealed record ScheduleDisplayRow(string Id, string Description, string ScheduleDisplay);

    private sealed record TargetOption(string Id, string Display);

    public Action? RequestClose { get; set; }

    public AutomationDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RefreshWatches();
            PopulateTargetOptions();
            RefreshSchedules();
            WatchService.Changed += OnWatchesChanged;
            ScheduleService.Changed += OnSchedulesChanged;
        };
        Unloaded += (_, _) =>
        {
            WatchService.Changed -= OnWatchesChanged;
            ScheduleService.Changed -= OnSchedulesChanged;
        };
    }

    private void OnWatchesChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RefreshWatches);

    private void OnSchedulesChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(RefreshSchedules);

    private void Close_Click(object sender, RoutedEventArgs e) => RequestClose?.Invoke();

    private void RefreshWatches()
    {
        var rows = WatchService.Tasks
            .Select(t => new WatchDisplayRow(t.Id, t.FolderPath, $"Runs: {t.ScriptName}"))
            .ToList();

        WatchesList.ItemsSource = rows;
        WatchesEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DeleteWatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            WatchService.RemoveTask(id);
        }
    }

    private void ScheduleKindBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => PopulateTargetOptions();

    private void PopulateTargetOptions()
    {
        var isSync = ScheduleKindBox.SelectedIndex == 1;

        var options = isSync
            ? SyncTaskService.Tasks.Select(t => new TargetOption(t.Id, t.Name)).ToList()
            : ScriptService.List().Select(name => new TargetOption(name, name)).ToList();

        ScheduleTargetBox.ItemsSource = options;
        ScheduleTargetBox.DisplayMemberPath = nameof(TargetOption.Display);
        ScheduleTargetBox.SelectedIndex = options.Count > 0 ? 0 : -1;
    }

    private void RefreshSchedules()
    {
        var rows = ScheduleService.Schedules.Select(s =>
        {
            var description = s.Kind == ScheduleKind.Sync
                ? $"Sync: {SyncTaskService.Tasks.FirstOrDefault(t => t.Id == s.TargetName)?.Name ?? "(deleted)"}"
                : $"Script: {s.TargetName}";

            var scheduleDisplay = $"Every {s.IntervalMinutes} min · next run {s.NextRunUtc.ToLocalTime():t}";

            return new ScheduleDisplayRow(s.Id, description, scheduleDisplay);
        }).ToList();

        SchedulesList.ItemsSource = rows;
        SchedulesEmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (ScheduleTargetBox.SelectedItem is not TargetOption target)
        {
            return;
        }

        var kind = ScheduleKindBox.SelectedIndex == 1 ? ScheduleKind.Sync : ScheduleKind.Script;
        var interval = (int)Math.Max(1, ScheduleIntervalBox.Value);

        ScheduleService.AddSchedule(kind, target.Id, interval);
        RefreshSchedules();
    }

    private void DeleteSchedule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id })
        {
            ScheduleService.RemoveSchedule(id);
        }
    }
}
