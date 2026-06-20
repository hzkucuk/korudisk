namespace KoruDisk.Infrastructure.Services;

public class RunningBackupState
{
    public int JobId { get; set; }
    public string JobName { get; set; } = string.Empty;
    public double Progress { get; set; }
    public string CurrentStep { get; set; } = string.Empty;
    public List<string> LogLines { get; set; } = new();
    public bool IsCancellationRequested => CancellationTokenSource.IsCancellationRequested;
    public CancellationToken CancellationToken => CancellationTokenSource.Token;
    internal CancellationTokenSource CancellationTokenSource { get; } = new();
    
    public event Action? OnChanged;

    public void Update(string step, double progress)
    {
        CurrentStep = step;
        Progress = progress;
        OnChanged?.Invoke();
    }

    public void AddLog(string level, string message)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        LogLines.Add($"[{timestamp}] [{level}] {message}");
        OnChanged?.Invoke();
    }

    public void RequestCancellation()
    {
        if (CancellationTokenSource.IsCancellationRequested)
        {
            return;
        }

        AddLog("Warning", "İptal isteği alındı. İşlem güvenli bir noktada durdurulacak.");
        CancellationTokenSource.Cancel();
    }
}

public class BackupCoordinator
{
    private readonly Dictionary<int, RunningBackupState> _runningJobs = new();

    public event Action? OnStateChanged;

    public RunningBackupState? GetState(int jobId)
    {
        lock (_runningJobs)
        {
            return _runningJobs.TryGetValue(jobId, out var state) ? state : null;
        }
    }

    public List<RunningBackupState> GetActiveBackups()
    {
        lock (_runningJobs)
        {
            return _runningJobs.Values.ToList();
        }
    }

    public RunningBackupState StartJob(int jobId, string jobName)
    {
        lock (_runningJobs)
        {
            var state = new RunningBackupState { JobId = jobId, JobName = jobName };
            _runningJobs[jobId] = state;
            OnStateChanged?.Invoke();
            return state;
        }
    }

    public void CompleteJob(int jobId)
    {
        lock (_runningJobs)
        {
            if (_runningJobs.Remove(jobId, out var state))
            {
                state.CancellationTokenSource.Dispose();
            }
            OnStateChanged?.Invoke();
        }
    }

    public bool RequestCancel(int jobId)
    {
        lock (_runningJobs)
        {
            if (!_runningJobs.TryGetValue(jobId, out var state))
            {
                return false;
            }

            state.RequestCancellation();
            OnStateChanged?.Invoke();
            return true;
        }
    }
}
