namespace KoruDisk.Core.Entities;

public class BackupLog
{
    public int Id { get; set; }
    public int BackupHistoryId { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Level { get; set; } = "Info"; // Info, Warning, Error
    public string Message { get; set; } = string.Empty;
}
