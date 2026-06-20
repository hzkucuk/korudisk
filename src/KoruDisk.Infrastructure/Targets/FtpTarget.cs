using FluentFTP;
using KoruDisk.Core.Entities;
using KoruDisk.Core.Enums;
using KoruDisk.Core.Interfaces;

namespace KoruDisk.Infrastructure.Targets;

public class FtpTarget : IStorageTarget
{
    public DestinationType Type => DestinationType.Ftp;

    public async Task UploadFileAsync(string localFilePath, StorageDestination destination, Action<double>? progressCallback = null)
    {
        if (!File.Exists(localFilePath))
            throw new FileNotFoundException("Yüklenecek kaynak dosya bulunamadı.", localFilePath);

        using var client = new AsyncFtpClient(destination.Host, destination.Username, destination.Password, destination.Port);
        await client.Connect();

        var remoteDir = destination.PathOrFolder.Replace("\\", "/");
        if (!string.IsNullOrWhiteSpace(remoteDir) && !await client.DirectoryExists(remoteDir))
        {
            await client.CreateDirectory(remoteDir);
        }

        var fileName = Path.GetFileName(localFilePath);
        var remoteFilePath = string.IsNullOrEmpty(remoteDir) ? fileName : $"{remoteDir.TrimEnd('/')}/{fileName}";

        var progress = new Progress<FtpProgress>(p =>
        {
            progressCallback?.Invoke(p.Progress);
        });

        var status = await client.UploadFile(
            localFilePath, 
            remoteFilePath, 
            FtpRemoteExists.Overwrite, 
            createRemoteDir: true, 
            FtpVerify.None, 
            progress);

        if (status == FtpStatus.Failed)
        {
            throw new Exception("FTP üzerinden dosya yükleme işlemi başarısız oldu.");
        }

        await client.Disconnect();
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(StorageDestination destination)
    {
        try
        {
            using var client = new AsyncFtpClient(destination.Host, destination.Username, destination.Password, destination.Port);
            await client.Connect();

            if (client.IsConnected)
            {
                var remoteDir = destination.PathOrFolder.Replace("\\", "/");
                if (!string.IsNullOrWhiteSpace(remoteDir) && !await client.DirectoryExists(remoteDir))
                {
                    await client.CreateDirectory(remoteDir);
                }
                await client.Disconnect();
                return (true, "FTP sunucusuna başarıyla bağlanıldı ve dizin doğrulandı.");
            }

            return (false, "FTP sunucusuna bağlantı kurulamadı.");
        }
        catch (Exception ex)
        {
            return (false, $"FTP Bağlantı Hatası: {ex.Message}");
        }
    }
}
