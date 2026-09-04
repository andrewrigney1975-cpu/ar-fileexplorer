using FileExplorer.Models;

namespace FileExplorer.Services;

/// Bridges backup jobs to ScheduleService: a Backup-kind schedule whose TargetName is the job Id
/// and whose interval is the job's differential cadence. Each firing decides full vs differential.
public static class BackupScheduling
{
    public static bool IsScheduled(string jobId) =>
        ScheduleService.Schedules.Any(s => s.Kind == ScheduleKind.Backup && s.TargetName == jobId);

    public static void SetScheduled(BackupJob job, bool scheduled)
    {
        var existing = ScheduleService.Schedules.FirstOrDefault(s => s.Kind == ScheduleKind.Backup && s.TargetName == job.Id);

        if (!scheduled)
        {
            if (existing is not null)
            {
                ScheduleService.RemoveSchedule(existing.Id);
            }

            return;
        }

        var intervalMinutes = Math.Max(1, job.DifferentialEveryDays) * 24 * 60;
        if (existing is null)
        {
            ScheduleService.AddSchedule(ScheduleKind.Backup, job.Id, intervalMinutes);
        }
        else if (existing.IntervalMinutes != intervalMinutes)
        {
            ScheduleService.RemoveSchedule(existing.Id);
            ScheduleService.AddSchedule(ScheduleKind.Backup, job.Id, intervalMinutes);
        }
    }
}
