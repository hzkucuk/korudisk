using KoruDisk.Core.Entities;
using System.Threading;

namespace KoruDisk.Core.Interfaces;

public interface IBackupService
{
    /// <summary>
    /// Bir yedekleme işini sırasıyla çalıştırır: İmajı oluşturur, sıkıştırır ve hedeflere yükler.
    /// </summary>
    Task<BackupHistory> ExecuteBackupJobAsync(
        BackupJob job, 
        Action<string, string>? logCallback = null, // (Level, Message)
        Action<string, double>? progressCallback = null,
        CancellationToken cancellationToken = default); // (StepName, Percentage)

    /// <summary>
    /// Geçmiş bir yedeklemeyi disk imajından yerel klasöre geri yükler.
    /// </summary>
    Task RestoreBackupAsync(
        BackupHistory history, 
        string destinationFolderPath, 
        Action<string, string>? logCallback = null);

    /// <summary>
    /// Yedek dosyasının bütünlük manifesti ve (varsa) açılmış içerik hash doğrulamasını yapar.
    /// </summary>
    Task VerifyBackupIntegrityAsync(
        BackupHistory history,
        Action<string, string>? logCallback = null,
        CancellationToken cancellationToken = default);
}
