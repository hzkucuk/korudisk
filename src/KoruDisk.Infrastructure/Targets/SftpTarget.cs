using KoruDisk.Core.Entities;
using KoruDisk.Core.Enums;
using KoruDisk.Core.Interfaces;
using Renci.SshNet;

namespace KoruDisk.Infrastructure.Targets;

public class SftpTarget : IStorageTarget
{
    public DestinationType Type => DestinationType.Sftp;

    public async Task UploadFileAsync(string localFilePath, StorageDestination destination, Action<double>? progressCallback = null)
    {
        await Task.Run(() =>
        {
            if (!File.Exists(localFilePath))
                throw new FileNotFoundException("Yüklenecek kaynak dosya bulunamadı.", localFilePath);

            var connectionInfo = new ConnectionInfo(
                destination.Host,
                destination.Port,
                destination.Username,
                new PasswordAuthenticationMethod(destination.Username, destination.Password)
            );

            using var client = new SftpClient(connectionInfo);
            client.Connect();

            // Uzak klasör yoksa oluştur
            var remoteDir = destination.PathOrFolder.Replace("\\", "/");
            if (!string.IsNullOrWhiteSpace(remoteDir) && !client.Exists(remoteDir))
            {
                CreateDirectoryRecursive(client, remoteDir);
            }

            var fileName = Path.GetFileName(localFilePath);
            var remoteFilePath = (string.IsNullOrEmpty(remoteDir) ? fileName : $"{remoteDir.TrimEnd('/')}/{fileName}");

            using var srcStream = File.OpenRead(localFilePath);
            long fileLength = srcStream.Length;

            client.UploadFile(srcStream, remoteFilePath, (uploaded) =>
            {
                if (fileLength > 0)
                {
                    progressCallback?.Invoke((double)uploaded / fileLength * 100);
                }
            });

            client.Disconnect();
        });
    }

    public async Task<(bool Success, string Message)> TestConnectionAsync(StorageDestination destination)
    {
        return await Task.Run<(bool, string)>(() =>
        {
            try
            {
                using var client = new SftpClient(destination.Host, destination.Port, destination.Username, destination.Password);
                client.Connect();
                
                if (client.IsConnected)
                {
                    var remoteDir = destination.PathOrFolder.Replace("\\", "/");
                    if (!string.IsNullOrWhiteSpace(remoteDir) && !client.Exists(remoteDir))
                    {
                        client.CreateDirectory(remoteDir);
                    }
                    client.Disconnect();
                    return (true, "SFTP sunucusuna başarıyla bağlanıldı ve dizin doğrulandı.");
                }
                
                return (false, "SFTP sunucusuna bağlantı kurulamadı.");
            }
            catch (Exception ex)
            {
                return (false, $"SFTP Bağlantı Hatası: {ex.Message}");
            }
        });
    }

    private void CreateDirectoryRecursive(SftpClient client, string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var current = "";
        
        // Eğer mutlak yol ise / ile başla
        if (path.StartsWith('/'))
        {
            current = "/";
        }

        foreach (var part in parts)
        {
            current = string.IsNullOrEmpty(current) || current == "/" ? $"{current}{part}" : $"{current}/{part}";
            if (!client.Exists(current))
            {
                client.CreateDirectory(current);
            }
        }
    }
}
