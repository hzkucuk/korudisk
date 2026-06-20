using KoruDisk.Core.Enums;

namespace KoruDisk.Core.Interfaces;

public interface ICompressionService
{
    CompressionType Type { get; }

    /// <summary>
    /// Bir dosyayı belirtilen sıkıştırma yöntemi ile sıkıştırır.
    /// </summary>
    Task CompressFileAsync(string sourceFilePath, string destinationFilePath, Action<double>? progressCallback = null);

    /// <summary>
    /// Sıkıştırılmış bir dosyanın içeriğini açar.
    /// </summary>
    Task DecompressFileAsync(string sourceFilePath, string destinationFilePath);
}
