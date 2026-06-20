using KoruDisk.Core.Entities;
using KoruDisk.Core.Interfaces;
using KoruDisk.Infrastructure.Data;
using KoruDisk.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace KoruDisk.Web.Services;

public sealed class ScheduledBackupService : BackgroundService
{
    private static readonly TimeSpan MinPollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan MaxPollInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IdlePollInterval = TimeSpan.FromMinutes(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackupCoordinator _backupCoordinator;
    private readonly ILogger<ScheduledBackupService> _logger;
    private readonly bool _schedulerEnabled;

    public ScheduledBackupService(
        IServiceScopeFactory scopeFactory,
        BackupCoordinator backupCoordinator,
        ILogger<ScheduledBackupService> logger,
        IConfiguration configuration)
    {
        _scopeFactory = scopeFactory;
        _backupCoordinator = backupCoordinator;
        _logger = logger;
        _schedulerEnabled = configuration.GetValue<bool?>("Scheduler:Enabled") ?? true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_schedulerEnabled)
        {
            _logger.LogInformation("Zamanlanmis yedekleme servisi konfigurasyon geregi devre disi.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var nextDelay = IdlePollInterval;
            try
            {
                nextDelay = await DispatchDueJobsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Zamanlanmış yedekler taranırken hata oluştu.");
            }

            if (nextDelay < MinPollInterval)
            {
                nextDelay = MinPollInterval;
            }
            else if (nextDelay > MaxPollInterval)
            {
                nextDelay = MaxPollInterval;
            }

            await Task.Delay(nextDelay, stoppingToken);
        }
    }

    private async Task<TimeSpan> DispatchDueJobsAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<KoruDiskDbContext>();
        var now = DateTime.Now;

        var scheduledJobs = await dbContext.BackupJobs
            .Where(job => job.IsActive && !string.IsNullOrWhiteSpace(job.CronExpression))
            .ToListAsync(cancellationToken);

        var dispatchPlan = ScheduledBackupPlanner.CreatePlan(
            scheduledJobs,
            now,
            jobId => _backupCoordinator.GetState(jobId) != null,
            (jobId, expression) => _logger.LogWarning("Gecersiz cron ifadesi nedeniyle zamanlanmis is atlandi. JobId={JobId}, Cron={CronExpression}", jobId, expression));

        if (dispatchPlan.HasChanges)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        foreach (var job in dispatchPlan.JobsToRun)
        {
            StartScheduledJob(job.JobId, job.JobName);
        }

        var nextRun = scheduledJobs
            .Where(job => job.NextRun.HasValue)
            .Select(job => job.NextRun!.Value)
            .OrderBy(value => value)
            .FirstOrDefault();

        if (nextRun == default)
        {
            return IdlePollInterval;
        }

        var delay = nextRun - DateTime.Now;
        return delay > TimeSpan.Zero ? delay : MinPollInterval;
    }

    private void StartScheduledJob(int jobId, string jobName)
    {
        if (_backupCoordinator.GetState(jobId) != null)
        {
            return;
        }

        var runningState = _backupCoordinator.StartJob(jobId, jobName);
        runningState.AddLog("Info", "Zamanlanmış yedekleme tetiklendi.");

        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var backupService = scope.ServiceProvider.GetRequiredService<IBackupService>();
                var dbContext = scope.ServiceProvider.GetRequiredService<KoruDiskDbContext>();

                var job = await dbContext.BackupJobs
                    .Include(item => item.Destinations)
                    .FirstOrDefaultAsync(item => item.Id == jobId);

                if (job == null)
                {
                    runningState.AddLog("Warning", "Zamanlanmış iş bulunamadı, yürütme iptal edildi.");
                    return;
                }

                await backupService.ExecuteBackupJobAsync(
                    job,
                    (level, message) => runningState.AddLog(level, message),
                    (step, percent) => runningState.Update(step, percent),
                    runningState.CancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Zamanlanmış yedekleme çalıştırılırken hata oluştu. JobId={JobId}", jobId);
                runningState.AddLog("Error", $"Zamanlanmış yedekleme başlatılamadı: {ex.Message}");
            }
            finally
            {
                _backupCoordinator.CompleteJob(jobId);
            }
        });
    }
}