using Cronos;
using KoruDisk.Core.Entities;

namespace KoruDisk.Web.Services;

public static class ScheduledBackupPlanner
{
    public static SchedulerDispatchPlan CreatePlan(
        IEnumerable<BackupJob> scheduledJobs,
        DateTime now,
        Func<int, bool> isJobRunning,
        Action<int, string>? invalidCronLogger = null)
    {
        var jobsToRun = new List<ScheduledJobLaunch>();
        var hasChanges = false;

        foreach (var job in scheduledJobs)
        {
            if (!TryParseCron(job.CronExpression, out var cronExpression))
            {
                if (job.NextRun != null)
                {
                    job.NextRun = null;
                    hasChanges = true;
                }

                invalidCronLogger?.Invoke(job.Id, job.CronExpression);
                continue;
            }

            if (job.NextRun == null)
            {
                job.NextRun = GetNextRun(cronExpression, now);
                hasChanges = true;
            }

            if (job.NextRun is null || job.NextRun > now || isJobRunning(job.Id))
            {
                continue;
            }

            job.LastRun = now;
            job.NextRun = GetNextRun(cronExpression, now.AddSeconds(1));
            hasChanges = true;
            jobsToRun.Add(new ScheduledJobLaunch(job.Id, job.Name));
        }

        return new SchedulerDispatchPlan(hasChanges, jobsToRun);
    }

    private static bool TryParseCron(string expression, out CronExpression cronExpression)
    {
        try
        {
            cronExpression = CronExpression.Parse(expression, CronFormat.Standard);
            return true;
        }
        catch (CronFormatException)
        {
            cronExpression = null!;
            return false;
        }
    }

    private static DateTime? GetNextRun(CronExpression cronExpression, DateTime fromLocalTime)
    {
        var localTime = fromLocalTime.Kind == DateTimeKind.Local
            ? fromLocalTime
            : DateTime.SpecifyKind(fromLocalTime, DateTimeKind.Local);

        var fromUtc = TimeZoneInfo.ConvertTimeToUtc(localTime, TimeZoneInfo.Local);
        var nextUtc = cronExpression.GetNextOccurrence(fromUtc, TimeZoneInfo.Local);
        return nextUtc?.ToLocalTime();
    }
}

public sealed record ScheduledJobLaunch(int JobId, string JobName);

public sealed record SchedulerDispatchPlan(bool HasChanges, IReadOnlyList<ScheduledJobLaunch> JobsToRun);