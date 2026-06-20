using KoruDisk.Core.Models;

namespace KoruDisk.Core.Interfaces;

public interface IDiskImageService
{
    /// <summary>
    /// Belirtilen klasörden yeni bir disk imajı (.vhd, .iso) oluşturur.
    /// </summary>
    Task CreateImageFromFolderAsync(
        string sourceFolderPath, 
        string destinationImagePath, 
        bool useNtfs, 
        string fileFilters, 
        Action<string, string>? logCallback = null, // (Level, Message)
        Action<double>? progressCallback = null);

    /// <summary>
    /// Parent VHD'yi temel alarak sadece değişiklikleri içeren yeni bir fark (child) VHD dosyası oluşturur.
    /// </summary>
    Task CreateIncrementalVhdAsync(
        string sourceFolderPath, 
        string parentVhdPath, 
        string childVhdPath, 
        string fileFilters, 
        Action<string, string>? logCallback = null,
        Action<double>? progressCallback = null);

    /// <summary>
    /// Bir imaj dosyasının (.vhd, .iso) içindeki dosya/klasör ağacını okur.
    /// </summary>
    Task<List<VirtualFileNode>> GetImageContentAsync(string imagePath);

    /// <summary>
    /// Bir imaj dosyasından belirtilen sanal dosyayı yerel diske çıkartır.
    /// </summary>
    Task ExtractFileFromImageAsync(string imagePath, string virtualFilePath, string destinationLocalPath);

    /// <summary>
    /// Bir imaj dosyasından belirtilen sanal dosyayı tarayıcı indirmeleri için Stream olarak okur.
    /// </summary>
    Task<Stream> ExtractFileToStreamAsync(string imagePath, string virtualFilePath);
}
