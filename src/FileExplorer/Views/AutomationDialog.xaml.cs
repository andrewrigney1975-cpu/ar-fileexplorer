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
            // Setting this (rather than relying on XAML IsSelected="True") fires SelectionChanged
            // -> PopulateTargetOptions() safely here, after InitializeComponent has finished wiring
            // every named field it touches (ScheduleTargetBox included) - IsSelected="True" fires
            // that same event mid-parse, before later-declared fields like ScheduleTargetBox exist,
            // which crashed with a NullReferenceException.
            ScheduleKindBox.SelectedIndex = 0;
            RefreshSchedules();
            ApplyFeatureState();
            WatchService.Changed += OnWatchesChanged;
            ScheduleService.Changed += OnSchedulesChanged;
            SettingsService.Changed += OnSettingsChanged;
        };
        Unloaded += (_, _) =>
        {
            WatchService.Changed -= OnWatchesChanged;
            ScheduleService.Changed -= OnSchedulesChanged;
            SettingsService.Changed -= OnSettingsChanged;
        };
    }

    public void HideCloseButton() => CloseButtonElement.Visibility = Visibility.Collapsed;

    private void OnSettingsChanged(object? sender, EventArgs e) => DispatcherQueue.TryEnqueue(() =>
    {
        ApplyFeatureState();
        PopulateTargetOptions();
    });

    private void ApplyFeatureState()
    {
        var settings = SettingsService.Current;
        WatchesDisabledText.Visibility = settings.EnableFolderWatching ? Visibility.Collapsed : Visibility.Visible;

        ScheduleKindScriptItem.IsEnabled = settings.EnableScripting;
        ScheduleKindSyncItem.IsEnabled = settings.EnableSyncTasks;

        if ((ReferenceEquals(ScheduleKindBox.SelectedItem, ScheduleKindScriptItem) && !settings.EnableScripting) ||
            (ReferenceEquals(ScheduleKindBox.SelectedItem, ScheduleKindSyncItem) && !settings.EnableSyncTasks))
        {
            ScheduleKindBox.SelectedItem = settings.EnableScripting ? ScheduleKindScriptItem
                : settings.EnableSyncTasks ? ScheduleKindSyncItem
                : null;
        }
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
