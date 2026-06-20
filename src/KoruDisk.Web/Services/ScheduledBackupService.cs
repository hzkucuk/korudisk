using KoruDisk.Core.Entities;
using KoruDisk.Core.Interfaces;
using KoruDisk.Infrastructure.Data;
using KoruDisk.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace KoruDisk.Web.Services;

public sealed class ScheduledBackupService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BackupCoordinator _backupCoordinator;
    private readonly ILogger<ScheduledBackupService> _logger;

    public ScheduledBackupService(
        IServiceScopeFactory scopeFactory,
        BackupCoordinator backupCoordinator,
        ILogger<ScheduledBackupService> logger)
    {
        _scopeFactory = scopeFactory;
        _backupCoordinator = backupCoordinator;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DispatchDueJobsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Zamanlanmış yedekler taranırken hata oluştu.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task DispatchDueJobsAsync(CancellationToken cancellationToken)
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