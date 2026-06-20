using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Upload;
using KoruDisk.Core.Entities;
using KoruDisk.Core.Enums;
using KoruDisk.Core.Interfaces;

namespace KoruDisk.Infrastructure.Targets;

public class GoogleDriveTarget : IStorageTarget
{
    public DestinationType Type => DestinationType.GoogleDrive;

    public async Task UploadFileAsync(string localFilePath, StorageDestination destination, Action<double>? progressCallback = null)
    {
        if (!File.Exists(localFilePath))
            throw new FileNotFoundException("Yüklenecek kaynak dosya bulunamadı.", localFilePath);

        if (string.IsNullOrWhiteSpace(destination.GoogleCredentialJson))
            throw new ArgumentException("Google Drive yetkilendirme JSON verisi bulunamadı.");

        // Google Drive yetkilendirmesini yükle
        var credential = CredentialFactory.FromJson<GoogleCredential>(destination.GoogleCredentialJson)
            .CreateScoped(DriveService.Scope.DriveFile, DriveService.Scope.Drive);

        using var service = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "KoruDisk"
        });

        var fileMetadata = new Google.Apis.Drive.v3.Data.File
        {
            Name = Path.GetFileName(localFilePath)
        };

        // Eğer bir Google Drive Folder ID belirtildiyse, bu klasörün altına yükle
        if (!string.IsNullOrWhiteSpace(destination.PathOrFolder))
        {
            fileMetadata.Parents = new List<string> { destination.PathOrFolder };
        }

        using var stream = File.OpenRead(localFilePath);
        long fileLength = stream.Length;

        var request = service.Files.Create(fileMetadata, stream, "application/octet-stream");
        request.Fields = "id";

        var tcs = new TaskCompletionSource<bool>();

        request.ProgressChanged += (IUploadProgress progress) =>
        {
            switch (progress.Status)
            {
                case UploadStatus.Uploading:
                    if (fileLength > 0)
                    {
                        progressCallback?.Invoke((double)progress.BytesSent / fileLength * 100);
                    }
                    break;
                case UploadStatus.Completed:
                    progressCallback?.Invoke(100.0);
                    tcs.TrySetResult(true);
                    break;
                case UploadStatus.Failed:
                    tcs.TrySetException(progress.Exception ?? new Exception("Google Drive yüklemesi başarısız oldu."));
                    break;
            }
        };

        await request.UploadAsync();
        await tcs.Task; // Yükleme tamamlanana kadar bekle
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(StorageDestination destination)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(destination.GoogleCredentialJson))
            {
                return (false, "Google Drive yetkilendirme JSON verisi boş olamaz.");
            }

            var credential = CredentialFactory.FromJson<GoogleCredential>(destination.GoogleCredentialJson)
                .CreateScoped(DriveService.Scope.DriveReadonly);

            using var service = new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = "KoruDisk"
            });

            // Bağlantıyı test etmek için API ile basit bir sorgu yapıyoruz
            var request = service.Files.List();
            request.PageSize = 1;
            request.Fields = "files(id, name)";
            await request.ExecuteAsync();

            return (true, "Google Drive bağlantısı başarılı. Kimlik doğrulandı.");
        }
        catch (Exception ex)
        {
            return (false, $"Google Drive Yetkilendirme Hatası: {ex.Message}");
        }
    }
}
