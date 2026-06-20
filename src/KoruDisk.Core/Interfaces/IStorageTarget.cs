using KoruDisk.Core.Entities;
using KoruDisk.Core.Enums;

namespace KoruDisk.Core.Interfaces;

public interface IStorageTarget
{
    DestinationType Type { get; }

    /// <summary>
    /// Yerel bir dosyayı belirtilen hedefe yükler.
    /// </summary>
    Task UploadFileAsync(
        string localFilePath, 
        StorageDestination destination, 
        Action<double>? progressCallback = null);

    /// <summary>
    /// Hedefin bağlantı ayarlarının doğruluğunu test eder.
    /// </summary>
    Task<(bool Success, string Message)> TestConnectionAsync(StorageDestination destination);
}
