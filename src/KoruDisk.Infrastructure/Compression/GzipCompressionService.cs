using KoruDisk.Core.Enums;
using KoruDisk.Core.Interfaces;
using System.IO.Compression;

namespace KoruDisk.Infrastructure.Compression;

public class GzipCompressionService : ICompressionService
{
    public CompressionType Type => CompressionType.Gzip;

    public async Task CompressFileAsync(string sourceFilePath, string destinationFilePath, Action<double>? progressCallback = null)
    {
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException("Sıkıştırılacak kaynak dosya bulunamadı.", sourceFilePath);

        var tempPath = destinationFilePath + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            using (var destStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            using (var gzipStream = new GZipStream(destStream, CompressionLevel.Optimal))
            using (var srcStream = new FileStream(sourceFilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true))
            {
                long totalBytes = srcStream.Length;
                long totalRead = 0;
                byte[] buffer = new byte[81920];
                int read;

                while ((read = await srcStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await gzipStream.WriteAsync(buffer, 0, read);
                    totalRead += read;
                    if (totalBytes > 0)
                    {
                        progressCallback?.Invoke((double)totalRead / totalBytes * 100);
                    }
                }
            }

            File.Move(tempPath, destinationFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
        
        progressCallback?.Invoke(100.0);
    }

    public async Task DecompressFileAsync(string sourceFilePath, string destinationFilePath)
    {
        if (!File.Exists(sourceFilePath))
            throw new FileNotFoundException("Açılacak GZip dosyası bulunamadı.", sourceFilePath);

        var tempPath = destinationFilePath + ".tmp-" + Guid.NewGuid().ToString("N");

        try
        {
            using (var srcStream = File.OpenRead(sourceFilePath))
            using (var gzipStream = new GZipStream(srcStream, CompressionMode.Decompress))
            using (var destStream = File.Create(tempPath))
            {
                await gzipStream.CopyToAsync(destStream);
            }

            File.Move(tempPath, destinationFilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }
}
