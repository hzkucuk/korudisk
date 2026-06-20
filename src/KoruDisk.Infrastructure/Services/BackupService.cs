using DiscUtils;
using DiscUtils.Fat;
using DiscUtils.Iso9660;
using DiscUtils.Ntfs;
using DiscUtils.Vhd;
using DiscUtils.Streams;
using KoruDisk.Core.Entities;
using KoruDisk.Core.Enums;
using KoruDisk.Core.Interfaces;
using KoruDisk.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;

namespace KoruDisk.Infrastructure.Services;

public class BackupService : IBackupService
{
    private readonly KoruDiskDbContext _dbContext;
    private readonly IDiskImageService _diskImageService;
    private readonly IEnumerable<IStorageTarget> _storageTargets;
    private readonly IEnumerable<ICompressionService> _compressionServices;
    private readonly string _localBackupsFolder;

    public BackupService(
        KoruDiskDbContext dbContext,
        IDiskImageService diskImageService,
        IEnumerable<IStorageTarget> storageTargets,
        IEnumerable<ICompressionService> compressionServices)
    {
        _dbContext = dbContext;
        _diskImageService = diskImageService;
        _storageTargets = storageTargets;
        _compressionServices = compressionServices;
        
        // Uygulamanın çalıştığı klasörde yerel yedek saklama dizini oluşturuyoruz
        _localBackupsFolder = Path.Combine(AppContext.BaseDirectory, "KoruDiskLocalBackups");
        if (!Directory.Exists(_localBackupsFolder))
        {
            Directory.CreateDirectory(_localBackupsFolder);
        }
    }

    public async Task<BackupHistory> ExecuteBackupJobAsync(
        BackupJob job,
        Action<string, string>? logCallback = null,
        Action<string, double>? progressCallback = null,
        CancellationToken cancellationToken = default)
    {
        // 1. Yeni Geçmiş Tanımı ve Sürüm Numarası Belirleme
        var lastSuccess = await _dbContext.BackupHistories
            .Where(h => h.JobId == job.Id && h.Status == "Success")
            .OrderByDescending(h => h.VersionNumber)
            .FirstOrDefaultAsync();

        int nextVersion = (lastSuccess?.VersionNumber ?? 0) + 1;
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        
        var history = new BackupHistory
            {
                JobId = job.Id,
                JobName = job.Name,
                VersionNumber = nextVersion,
                Timestamp = DateTime.UtcNow,
                Status = "Running"
            };

        _dbContext.BackupHistories.Add(history);
        await _dbContext.SaveChangesAsync();

        void AddLog(string level, string message)
        {
            var log = new BackupLog
            {
                BackupHistoryId = history.Id,
                Timestamp = DateTime.UtcNow,
                Level = level,
                Message = message
            };
            history.Logs.Add(log);
            logCallback?.Invoke(level, message);
        }

        void ThrowIfCancellationRequested()
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        void ReportProgress(string step, double progress)
        {
            ThrowIfCancellationRequested();
            progressCallback?.Invoke(step, progress);
        }

        AddLog("Info", $"Yedekleme görevi '{job.Name}' başlatıldı. Sürüm: v{nextVersion}");

        var isWindowsPlatform = OperatingSystem.IsWindows();
        AddLog("Info", $"Çalışan platform: {(isWindowsPlatform ? "Windows" : "Non-Windows")} ({Environment.OSVersion.Platform})");

        bool isIncrementalRequested = job.Versioning == VersioningType.Incremental;
        bool isIncremental = isIncrementalRequested && lastSuccess != null && File.Exists(lastSuccess.ImagePath);

        if (isIncremental && !isWindowsPlatform)
        {
            AddLog("Warning", "Artımlı yedekleme bu platformda desteklenmediği için tam yedeklemeye düşürüldü.");
            isIncremental = false;
        }

        var imageExtension = isWindowsPlatform ? ".vhd" : ".iso";
        AddLog("Info", $"Hedef imaj uzantısı: {imageExtension}");
        string tempImagePath = Path.Combine(_localBackupsFolder, $"{job.Name}_v{nextVersion}_{timestamp}{imageExtension}");
        string finalFilePath = tempImagePath;
        string imageSha256 = string.Empty;

        try
        {
            ThrowIfCancellationRequested();

            // 2. İmaj Oluşturma Aşaması
            ReportProgress("Disk İmajı Oluşturuluyor", 10.0);

            var sourceDirectories = job.SourcePaths
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .ToList();

            if (sourceDirectories.Count == 0)
            {
                throw new DirectoryNotFoundException("Yedeklenecek en az bir kaynak klasör belirtilmelidir.");
            }

            var missingSourceDir = sourceDirectories.FirstOrDefault(path => !Directory.Exists(path));
            if (missingSourceDir != null)
            {
                throw new DirectoryNotFoundException($"Yedeklenecek kaynak klasör bulunamadı veya erişilemez: {missingSourceDir}");
            }

            if (isIncremental)
            {
                ThrowIfCancellationRequested();
                string parentVhd = lastSuccess!.ImagePath;
                history.ParentImagePath = parentVhd;
                AddLog("Info", $"Artımlı (Incremental) yedekleme yapılıyor. Ebeveyn imaj: {Path.GetFileName(parentVhd)}");
                
                await _diskImageService.CreateIncrementalVhdAsync(
                    job.SourcePaths,
                    parentVhd,
                    tempImagePath,
                    job.FileFilters,
                    (lvl, msg) => AddLog(lvl, msg),
                    pct => ReportProgress("Disk İmajı Oluşturuluyor", 10.0 + (pct * 0.4)));
            }
            else
            {
                ThrowIfCancellationRequested();
                var useNtfs = isWindowsPlatform;
                AddLog("Info", useNtfs
                    ? "Tam (Full) yedekleme yapılıyor. İmaj formatı: VHD (NTFS)"
                    : "Tam (Full) yedekleme yapılıyor. İmaj formatı: ISO (macOS/Linux için uyumlu).");
                await _diskImageService.CreateImageFromFolderAsync(
                    job.SourcePaths,
                    tempImagePath,
                    useNtfs,
                    job.FileFilters,
                    (lvl, msg) => AddLog(lvl, msg),
                    pct => ReportProgress("Disk İmajı Oluşturuluyor", 10.0 + (pct * 0.4)));
            }

            ThrowIfCancellationRequested();
            await VerifyImageReadableAsync(tempImagePath, AddLog);
            imageSha256 = await ComputeSha256Async(tempImagePath, cancellationToken);
            AddLog("Info", $"İmaj bütünlük özeti (SHA-256): {imageSha256}");
            await WriteIntegrityManifestAsync(tempImagePath, imageSha256, null, cancellationToken);
            history.IntegrityStatus = "Verified";
            history.IntegrityCheckedAtUtc = DateTime.UtcNow;
            history.IntegrityMessage = "Yedek oluşturma sonrası bütünlük doğrulaması başarılı.";

            // 3. Sıkıştırma Aşaması (Opsiyonel)
            if (job.Compression != CompressionType.None)
            {
                ThrowIfCancellationRequested();
                ReportProgress("İmaj Sıkıştırılıyor", 50.0);
                var compService = _compressionServices.FirstOrDefault(c => c.Type == job.Compression);
                if (compService == null)
                {
                    throw new NotSupportedException($"Seçilen sıkıştırma yöntemi ({job.Compression}) sistemde bulunamadı.");
                }

                string compressedPath = tempImagePath + GetCompressionExtension(job.Compression);
                AddLog("Info", $"İmaj '{job.Compression}' yöntemiyle sıkıştırılıyor...");
                
                await compService.CompressFileAsync(tempImagePath, compressedPath, 
                    pct => ReportProgress("İmaj Sıkıştırılıyor", 50.0 + (pct * 0.2)));

                var verifyExtractPath = Path.Combine(_localBackupsFolder, $"verify_{Guid.NewGuid():N}{imageExtension}");
                try
                {
                    await compService.DecompressFileAsync(compressedPath, verifyExtractPath);
                    var extractedSha256 = await ComputeSha256Async(verifyExtractPath, cancellationToken);
                    if (!string.Equals(extractedSha256, imageSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Sıkıştırma doğrulaması başarısız: açılan imajın SHA-256 özeti kaynak imajla eşleşmiyor.");
                    }

                    AddLog("Info", "Sıkıştırma doğrulaması başarılı: açılan içerik kaynak imajla eşleşiyor.");
                }
                finally
                {
                    if (File.Exists(verifyExtractPath))
                    {
                        try { File.Delete(verifyExtractPath); } catch { }
                    }
                }

                await WriteIntegrityManifestAsync(compressedPath, await ComputeSha256Async(compressedPath, cancellationToken), imageSha256, cancellationToken);

                // Sıkıştırılmamış geçici imajı siliyoruz
                if (File.Exists(tempImagePath))
                {
                    File.Delete(tempImagePath);
                }

                var tempImageManifestPath = GetIntegrityManifestPath(tempImagePath);
                if (File.Exists(tempImageManifestPath))
                {
                    File.Delete(tempImageManifestPath);
                }
                
                finalFilePath = compressedPath;
                AddLog("Info", $"Sıkıştırma tamamlandı. Dosya: {Path.GetFileName(finalFilePath)}");
            }

            // Oluşan dosya boyutu
            history.SizeInBytes = new FileInfo(finalFilePath).Length;
            history.ImagePath = finalFilePath;

            // 4. Hedef Dağıtım Aşaması
            if (job.Destinations != null && job.Destinations.Count > 0)
            {
                double stepWeight = 30.0 / job.Destinations.Count;
                int currentDestIndex = 0;

                foreach (var dest in job.Destinations.Where(d => d.IsActive))
                {
                    ThrowIfCancellationRequested();
                    AddLog("Info", $"Yedek dosyası '{dest.Name}' ({dest.Type}) hedefine gönderiliyor...");
                    ReportProgress($"Hedef Yüklemesi: {dest.Name}", 70.0 + (currentDestIndex * stepWeight));

                    var targetAdapter = _storageTargets.FirstOrDefault(t => t.Type == dest.Type);
                    if (targetAdapter == null)
                    {
                        AddLog("Warning", $"'{dest.Type}' hedef adaptörü bulunamadı. Bu adım atlanıyor.");
                        continue;
                    }

                    await targetAdapter.UploadFileAsync(finalFilePath, dest, pct => 
                        ReportProgress($"Hedef Yüklemesi: {dest.Name}", 70.0 + (currentDestIndex * stepWeight) + (pct * 0.01 * stepWeight)));

                    var manifestPath = GetIntegrityManifestPath(finalFilePath);
                    if (File.Exists(manifestPath))
                    {
                        await targetAdapter.UploadFileAsync(manifestPath, dest);
                        AddLog("Info", $"Bütünlük manifesti yüklendi: {Path.GetFileName(manifestPath)}");
                    }

                    AddLog("Info", $"'{dest.Name}' hedefine yükleme tamamlandı.");
                    currentDestIndex++;
                }
            }

            // 5. Saklama Politikası (Retention Policy) Uygulama
            ThrowIfCancellationRequested();
            ReportProgress("Saklama Politikası Uygulanıyor", 99.0);
            await ApplyRetentionPolicyAsync(job, AddLog);

            // Başarılı tamamlama
            history.Status = "Success";
            history.Timestamp = DateTime.UtcNow;
            AddLog("Info", $"Yedekleme görevi başarıyla tamamlandı. Toplam Boyut: {FormatBytes(history.SizeInBytes)}");
            ReportProgress("Tamamlandı", 100.0);
        }
        catch (OperationCanceledException)
        {
            history.Status = "Failed";
            history.ErrorMessage = "İşlem kullanıcı tarafından iptal edildi.";
            history.IntegrityStatus = "Pending";
            history.IntegrityMessage = "Doğrulama tamamlanmadan işlem iptal edildi.";
            AddLog("Warning", "Yedekleme işlemi kullanıcı isteğiyle iptal edildi.");
            progressCallback?.Invoke("İptal Edildi", 100.0);

            if (File.Exists(tempImagePath))
            {
                try { File.Delete(tempImagePath); } catch { }
            }

            var tempManifestPath = GetIntegrityManifestPath(tempImagePath);
            if (File.Exists(tempManifestPath))
            {
                try { File.Delete(tempManifestPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            history.Status = "Failed";
            history.ErrorMessage = ex.Message;
            history.IntegrityStatus = "Failed";
            history.IntegrityCheckedAtUtc = DateTime.UtcNow;
            history.IntegrityMessage = ex.Message;
            AddLog("Error", $"Yedekleme Sırasında Hata Oluştu: {ex.Message}");
            progressCallback?.Invoke("Hata Oluştu", 100.0);

            // Hata durumunda geçici imajı temizlemeye çalış
            if (File.Exists(tempImagePath))
            {
                try { File.Delete(tempImagePath); } catch { }
            }

            var tempManifestPath = GetIntegrityManifestPath(tempImagePath);
            if (File.Exists(tempManifestPath))
            {
                try { File.Delete(tempManifestPath); } catch { }
            }
        }

        await _dbContext.SaveChangesAsync();
        return history;
    }

    public async Task RestoreBackupAsync(
        BackupHistory history,
        string destinationFolderPath,
        Action<string, string>? logCallback = null)
    {
        await Task.Run(async () =>
        {
            logCallback?.Invoke("Info", $"Geri yükleme işlemi başlatıldı. Kaynak imaj: {Path.GetFileName(history.ImagePath)}");
            await VerifyAgainstIntegrityManifestAsync(history.ImagePath, logCallback, CancellationToken.None);
            
            string fileToOpen = history.ImagePath;
            string? decompressedTempPath = null;
            string? expectedDecompressedSha256 = null;

            // 1. Dosya sıkıştırılmışsa aç
            var ext = Path.GetExtension(history.ImagePath).ToLower();
            if (ext == ".zip" || ext == ".gz" || ext == ".deflate")
            {
                logCallback?.Invoke("Info", "Sıkıştırılmış arşiv açılıyor (Decompress)...");
                var compType = GetCompressionTypeFromExtension(ext);
                var compService = _compressionServices.FirstOrDefault(c => c.Type == compType);
                if (compService == null)
                {
                    throw new NotSupportedException($"Gerekli sıkıştırma servisi ({compType}) bulunamadı.");
                }

                var sourceManifest = await TryReadIntegrityManifestAsync(history.ImagePath);
                expectedDecompressedSha256 = sourceManifest?.OriginalContentSha256;

                var originalImageExtension = Path.GetExtension(Path.GetFileNameWithoutExtension(history.ImagePath)).ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(originalImageExtension) ||
                    (originalImageExtension != ".vhd" && originalImageExtension != ".iso"))
                {
                    originalImageExtension = ".vhd";
                }

                decompressedTempPath = Path.Combine(_localBackupsFolder, $"temp_restore_{Guid.NewGuid():N}{originalImageExtension}");
                await compService.DecompressFileAsync(history.ImagePath, decompressedTempPath);

                if (!string.IsNullOrWhiteSpace(expectedDecompressedSha256))
                {
                    var actualDecompressedSha256 = await ComputeSha256Async(decompressedTempPath, CancellationToken.None);
                    if (!string.Equals(expectedDecompressedSha256, actualDecompressedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Açılan imaj doğrulaması başarısız: SHA-256 özeti beklenen değerle eşleşmiyor.");
                    }

                    logCallback?.Invoke("Info", "Açılan imaj bütünlük doğrulaması başarılı.");
                }

                fileToOpen = decompressedTempPath;
            }

            // 2. İmajı oku ve yerel klasöre çıkart
            try
            {
                var fileExtension = Path.GetExtension(fileToOpen).ToLower();
                if (fileExtension == ".vhd")
                {
                    // VHD + differencing parent zinciri için kendi klasöründe arar
                    var dir = Path.GetDirectoryName(fileToOpen) ?? Directory.GetCurrentDirectory();
                    var locator = new KoruDiskFileLocator(dir);
                    var fileName = Path.GetFileName(fileToOpen);
                    using var diskStream = locator.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
                    using var disk = new Disk(diskStream, Ownership.None);
                    if (disk.Partitions.Count == 0)
                    {
                        throw new InvalidOperationException("VHD imajında geçerli bir bölüm bulunamadı.");
                    }

                    var partition = disk.Partitions[0];
                    using var volStream = partition.Open();
                    using var fs = OpenFileSystem(volStream);
                    
                    logCallback?.Invoke("Info", "İmaj dosya sistemi bağlandı. Dosyalar kopyalanıyor...");
                    ExtractFolderFromVfs(fs, "", destinationFolderPath, logCallback);
                }
                else if (fileExtension == ".iso")
                {
                    using var stream = File.OpenRead(fileToOpen);
                    using var reader = new CDReader(stream, joliet: true);
                    
                    logCallback?.Invoke("Info", "ISO imajı bağlandı. Dosyalar kopyalanıyor...");
                    ExtractFolderFromVfs(reader, "", destinationFolderPath, logCallback);
                }
                
                logCallback?.Invoke("Info", "Geri yükleme işlemi başarıyla tamamlandı.");
            }
            finally
            {
                // Geçici olarak açılan VHD dosyasını temizle
                if (decompressedTempPath != null && File.Exists(decompressedTempPath))
                {
                    try { File.Delete(decompressedTempPath); } catch { }
                }
            }
        });
    }

    public async Task VerifyBackupIntegrityAsync(
        BackupHistory history,
        Action<string, string>? logCallback = null,
        CancellationToken cancellationToken = default)
    {
        if (history == null)
        {
            throw new ArgumentNullException(nameof(history));
        }

        if (string.IsNullOrWhiteSpace(history.ImagePath) || !File.Exists(history.ImagePath))
        {
            await PersistIntegrityResultAsync(history.Id, "NotAvailable", "Doğrulanacak yedek dosyası bulunamadı.");
            throw new FileNotFoundException("Doğrulanacak yedek dosyası bulunamadı.", history.ImagePath);
        }

        try
        {
            logCallback?.Invoke("Info", $"Bütünlük doğrulaması başlatıldı: {Path.GetFileName(history.ImagePath)}");
            await VerifyAgainstIntegrityManifestAsync(history.ImagePath, logCallback, cancellationToken);

            var ext = Path.GetExtension(history.ImagePath).ToLowerInvariant();
            if (ext == ".zip" || ext == ".gz" || ext == ".deflate")
            {
                var sourceManifest = await TryReadIntegrityManifestAsync(history.ImagePath);
                var expectedDecompressedSha256 = sourceManifest?.OriginalContentSha256;
                if (string.IsNullOrWhiteSpace(expectedDecompressedSha256))
                {
                    logCallback?.Invoke("Warning", "Manifestte açılmış içerik hash değeri bulunamadı. Sadece arşiv dosyası doğrulandı.");
                    await PersistIntegrityResultAsync(history.Id, "Verified", "Manifestte içerik hash yok, arşiv bütünlüğü doğrulandı.");
                    return;
                }

                var compType = GetCompressionTypeFromExtension(ext);
                var compService = _compressionServices.FirstOrDefault(c => c.Type == compType);
                if (compService == null)
                {
                    throw new NotSupportedException($"Gerekli sıkıştırma servisi ({compType}) bulunamadı.");
                }

                var originalImageExtension = Path.GetExtension(Path.GetFileNameWithoutExtension(history.ImagePath)).ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(originalImageExtension) ||
                    (originalImageExtension != ".vhd" && originalImageExtension != ".iso"))
                {
                    originalImageExtension = ".vhd";
                }

                var decompressedTempPath = Path.Combine(_localBackupsFolder, $"verify_manual_{Guid.NewGuid():N}{originalImageExtension}");
                try
                {
                    await compService.DecompressFileAsync(history.ImagePath, decompressedTempPath);
                    var actualDecompressedSha256 = await ComputeSha256Async(decompressedTempPath, cancellationToken);
                    if (!string.Equals(expectedDecompressedSha256, actualDecompressedSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Açılmış imaj doğrulaması başarısız: SHA-256 özeti beklenen değerle eşleşmiyor.");
                    }

                    await VerifyImageReadableAsync(decompressedTempPath, (level, message) => logCallback?.Invoke(level, message));
                    logCallback?.Invoke("Info", "Sıkıştırılmış yedeğin açılmış içerik doğrulaması başarılı.");
                }
                finally
                {
                    if (File.Exists(decompressedTempPath))
                    {
                        try { File.Delete(decompressedTempPath); } catch { }
                    }
                }

                logCallback?.Invoke("Info", "Bütünlük doğrulaması başarıyla tamamlandı.");
                await PersistIntegrityResultAsync(history.Id, "Verified", "Bütünlük doğrulaması başarıyla tamamlandı.");
                return;
            }

            await VerifyImageReadableAsync(history.ImagePath, (level, message) => logCallback?.Invoke(level, message));
            logCallback?.Invoke("Info", "Bütünlük doğrulaması başarıyla tamamlandı.");
            await PersistIntegrityResultAsync(history.Id, "Verified", "Bütünlük doğrulaması başarıyla tamamlandı.");
        }
        catch (Exception ex)
        {
            await PersistIntegrityResultAsync(history.Id, "Failed", ex.Message);
            throw;
        }
    }

    #region Helpers

    private async Task ApplyRetentionPolicyAsync(BackupJob job, Action<string, string> logAction)
    {
        var histories = await _dbContext.BackupHistories
            .Where(h => h.JobId == job.Id && h.Status == "Success")
            .OrderByDescending(h => h.VersionNumber)
            .ToListAsync();

        if (histories.Count > job.RetentionCount)
        {
            var oldHistoriesToDelete = histories.Skip(job.RetentionCount).ToList();
            foreach (var oldHist in oldHistoriesToDelete)
            {
                logAction("Info", $"Saklama politikası gereği eski yedek sürümü v{oldHist.VersionNumber} siliniyor.");
                
                // Yerel dosyayı temizle
                if (!string.IsNullOrEmpty(oldHist.ImagePath) && File.Exists(oldHist.ImagePath))
                {
                    try
                    {
                        File.Delete(oldHist.ImagePath);
                        var integrityPath = GetIntegrityManifestPath(oldHist.ImagePath);
                        if (File.Exists(integrityPath))
                        {
                            File.Delete(integrityPath);
                        }

                        logAction("Info", $"Eski yedek dosyası yerelden silindi: {Path.GetFileName(oldHist.ImagePath)}");
                    }
                    catch (Exception ex)
                    {
                        logAction("Warning", $"Eski yedek dosyası silinirken hata oluştu: {ex.Message}");
                    }
                }

                // Veri tabanından kaydı kaldır
                _dbContext.BackupHistories.Remove(oldHist);
            }
        }
    }

    private string GetCompressionExtension(CompressionType type) => type switch
    {
        CompressionType.Zip => ".zip",
        CompressionType.Gzip => ".gz",
        CompressionType.Deflate => ".deflate",
        _ => ""
    };

    private CompressionType GetCompressionTypeFromExtension(string ext) => ext switch
    {
        ".zip" => CompressionType.Zip,
        ".gz" => CompressionType.Gzip,
        ".deflate" => CompressionType.Deflate,
        _ => CompressionType.None
    };

    private DiscFileSystem OpenFileSystem(Stream volStream)
    {
        if (OperatingSystem.IsWindows())
        {
            volStream.Position = 0;
            if (NtfsFileSystem.Detect(volStream))
            {
                volStream.Position = 0;
                return new NtfsFileSystem(volStream);
            }
        }

        volStream.Position = 0;
        return new FatFileSystem(volStream);
    }

    private void ExtractFolderFromVfs(DiscFileSystem fs, string vfsPath, string localDestPath, Action<string, string>? logCallback)
    {
        if (!Directory.Exists(localDestPath))
        {
            Directory.CreateDirectory(localDestPath);
        }

        // Dosyaları çıkart
        var files = fs.GetFiles(vfsPath);
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            var localFilePath = Path.Combine(localDestPath, fileName);
            logCallback?.Invoke("Info", $"Çıkartılıyor: {file} -> {localFilePath}");

            using var srcStream = fs.OpenFile(file, FileMode.Open, FileAccess.Read);
            using var destStream = File.Create(localFilePath);
            srcStream.CopyTo(destStream);
        }

        // Alt klasörleri recursively çıkart
        var dirs = fs.GetDirectories(vfsPath);
        foreach (var dir in dirs)
        {
            var dirName = Path.GetFileName(dir);
            var localSubPath = Path.Combine(localDestPath, dirName);
            ExtractFolderFromVfs(fs, dir, localSubPath, logCallback);
        }
    }

    private string FormatBytes(long bytes)
    {
        string[] suffix = { "B", "KB", "MB", "GB", "TB" };
        int i;
        double dblBytes = bytes;
        for (i = 0; i < suffix.Length && bytes >= 1024; i++, bytes /= 1024)
        {
            dblBytes = bytes / 1024.0;
        }
        return $"{dblBytes:0.##} {suffix[i]}";
    }

    private async Task VerifyImageReadableAsync(string imagePath, Action<string, string> log)
    {
        var extension = Path.GetExtension(imagePath).ToLowerInvariant();

        if (extension == ".iso")
        {
            using var stream = File.OpenRead(imagePath);
            using var reader = new CDReader(stream, joliet: true);
            _ = reader.VolumeLabel;
            log("Info", "Oluşturulan ISO imaj okunabilirlik doğrulamasından geçti.");
            return;
        }

        await _diskImageService.GetImageContentAsync(imagePath);
        log("Info", "Oluşturulan imaj okunabilirlik doğrulamasından geçti.");
    }

    private static string GetIntegrityManifestPath(string targetFilePath)
    {
        return targetFilePath + ".integrity.json";
    }

    private static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    private static async Task WriteIntegrityManifestAsync(
        string targetFilePath,
        string fileSha256,
        string? originalContentSha256,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(targetFilePath);
        var manifest = new BackupIntegrityManifest
        {
            FileName = fileInfo.Name,
            FileSizeBytes = fileInfo.Length,
            FileSha256 = fileSha256,
            OriginalContentSha256 = originalContentSha256,
            GeneratedAtUtc = DateTime.UtcNow
        };

        var manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        await File.WriteAllTextAsync(GetIntegrityManifestPath(targetFilePath), manifestJson, cancellationToken);
    }

    private static async Task<BackupIntegrityManifest?> TryReadIntegrityManifestAsync(string targetFilePath)
    {
        var manifestPath = GetIntegrityManifestPath(targetFilePath);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var content = await File.ReadAllTextAsync(manifestPath);
        return JsonSerializer.Deserialize<BackupIntegrityManifest>(content);
    }

    private async Task VerifyAgainstIntegrityManifestAsync(
        string targetFilePath,
        Action<string, string>? logCallback,
        CancellationToken cancellationToken)
    {
        var manifest = await TryReadIntegrityManifestAsync(targetFilePath);
        if (manifest == null)
        {
            logCallback?.Invoke("Warning", "Bütünlük manifesti bulunamadı, dosya hash doğrulaması atlandı.");
            return;
        }

        var fileInfo = new FileInfo(targetFilePath);
        if (fileInfo.Length != manifest.FileSizeBytes)
        {
            throw new InvalidDataException("Yedek dosyası bütünlük doğrulamasını geçemedi (dosya boyutu manifestle eşleşmiyor).");
        }

        var actualSha256 = await ComputeSha256Async(targetFilePath, cancellationToken);
        if (!string.Equals(actualSha256, manifest.FileSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Yedek dosyası bütünlük doğrulamasını geçemedi (SHA-256 uyuşmazlığı).");
        }

        logCallback?.Invoke("Info", "Yedek dosyası bütünlük manifesti doğrulandı.");
    }

    private sealed class BackupIntegrityManifest
    {
        public string FileName { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public string FileSha256 { get; set; } = string.Empty;
        public string? OriginalContentSha256 { get; set; }
        public DateTime GeneratedAtUtc { get; set; }
    }

    private async Task PersistIntegrityResultAsync(int historyId, string status, string message)
    {
        var trackedHistory = await _dbContext.BackupHistories.FirstOrDefaultAsync(item => item.Id == historyId);
        if (trackedHistory == null)
        {
            return;
        }

        trackedHistory.IntegrityStatus = status;
        trackedHistory.IntegrityCheckedAtUtc = DateTime.UtcNow;
        trackedHistory.IntegrityMessage = message;
        await _dbContext.SaveChangesAsync();
    }

    #endregion
}
