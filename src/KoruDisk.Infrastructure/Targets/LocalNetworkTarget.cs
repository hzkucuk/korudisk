using KoruDisk.Core.Entities;
using KoruDisk.Core.Enums;
using KoruDisk.Core.Interfaces;

namespace KoruDisk.Infrastructure.Targets;

public class LocalNetworkTarget : IStorageTarget
{
    public DestinationType Type => DestinationType.LocalOrNetwork;

    public async Task UploadFileAsync(string localFilePath, StorageDestination destination, Action<double>? progressCallback = null)
    {
        if (!File.Exists(localFilePath))
            throw new FileNotFoundException("Yüklenecek kaynak dosya bulunamadı.", localFilePath);

        var destDir = destination.PathOrFolder;
        if (string.IsNullOrWhiteSpace(destDir))
        {
            throw new ArgumentException("Yedekleme hedef dizini belirtilmemiş.");
        }

        if (!Directory.Exists(destDir))
        {
            Directory.CreateDirectory(destDir);
        }

        var fileName = Path.GetFileName(localFilePath);
        var destFilePath = Path.Combine(destDir, fileName);

        // İlerlemeyi bildirmek için dosyayı bloklar halinde kopyalıyoruz
        using var srcStream = new FileStream(localFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using var destStream = new FileStream(destFilePath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        byte[] buffer = new byte[81920]; // 80 KB
        long totalBytes = srcStream.Length;
        long totalRead = 0;
        int read;

        while ((read = await srcStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await destStream.WriteAsync(buffer, 0, read);
            totalRead += read;
            if (totalBytes > 0)
            {
                progressCallback?.Invoke((double)totalRead / totalBytes * 100);
            }
        }
        
        progressCallback?.Invoke(100.0);
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(StorageDestination destination)
    {
        try
        {
            var testDir = destination.PathOrFolder;
            if (string.IsNullOrWhiteSpace(testDir))
            {
                return (false, "Dizin yolu boş olamaz.");
            }

            if (!Directory.Exists(testDir))
            {
                Directory.CreateDirectory(testDir);
            }

            // Dizin içerisine test amaçlı dosya yazıp siliyoruz (yazma yetkisi testi)
            var tempFile = Path.Combine(testDir, $".korudisk_test_{Guid.NewGuid():N}.tmp");
            await File.WriteAllTextAsync(tempFile, "KoruDisk Connection Test");
            File.Delete(tempFile);

            return (true, "Hedef dizine başarıyla erişildi ve yazma yetkisi doğrulandı.");
        }
        catch (Exception ex)
        {
            return (false, $"Hedef dizine erişilemedi: {ex.Message}");
        }
    }
}
