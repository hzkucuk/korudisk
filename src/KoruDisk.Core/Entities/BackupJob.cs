using KoruDisk.Core.Enums;

namespace KoruDisk.Core.Entities;

public class BackupJob
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    
    // Noktalı virgül (;) ile ayrılmış kaynak klasör yolları (örneğin: /User/Path1;/User/Path2)
    public string SourcePaths { get; set; } = string.Empty;
    
    // Filtreler (örn: *.*;!*.tmp;!*.log veya sadece dahil edilecekler: *.docx;*.xlsx)
    public string FileFilters { get; set; } = string.Empty;
    
    public CompressionType Compression { get; set; } = CompressionType.None;
    public VersioningType Versioning { get; set; } = VersioningType.Full;
    
    // Bu işin yükleneceği hedefler
    public ICollection<StorageDestination> Destinations { get; set; } = new List<StorageDestination>();
    
    // Yedekleme Zamanlaması için Cron ifadesi (örn: "0 12 * * *" -> Her gün 12:00)
    public string CronExpression { get; set; } = string.Empty;
    
    public bool IsActive { get; set; } = true;
    public int RetentionCount { get; set; } = 5; // Kaç adet versiyon saklanacak
    
    public DateTime? LastRun { get; set; }
    public DateTime? NextRun { get; set; }
}
