namespace KoruDisk.Core.Entities;

public class BackupHistory
{
    public int Id { get; set; }
    public int JobId { get; set; }
    public string JobName { get; set; } = string.Empty;
    public int VersionNumber { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Running"; // Running, Success, Failed
    public long SizeInBytes { get; set; }
    
    // Disk imaj dosyasının yerel veya hedefteki dosya yolu
    public string ImagePath { get; set; } = string.Empty;
    
    // Eğer artımlı (incremental) yedek ise, bağlı olduğu bir önceki VHD dosyasının yolu
    public string ParentImagePath { get; set; } = string.Empty;
    
    public string ErrorMessage { get; set; } = string.Empty;

    // Bütünlük doğrulama durumu: Pending, Verified, Failed, NotAvailable
    public string IntegrityStatus { get; set; } = "Pending";

    public DateTime? IntegrityCheckedAtUtc { get; set; }

    public string IntegrityMessage { get; set; } = string.Empty;
    
    public ICollection<BackupLog> Logs { get; set; } = new List<BackupLog>();
}
