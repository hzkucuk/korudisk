using DiscUtils;
using DiscUtils.Fat;
using DiscUtils.Iso9660;
using DiscUtils.Ntfs;
using DiscUtils.Partitions;
using DiscUtils.Vhd;
using DiscUtils.Streams;
using KoruDisk.Core.Interfaces;
using KoruDisk.Core.Models;
using System.Text.RegularExpressions;

namespace KoruDisk.Infrastructure.Services;

public class DiscUtilsImageService : IDiskImageService
{
    private const long DefaultVhdCapacity = 64 * 1024 * 1024 * 1024L; // 64 GB varsayılan kapasite (Dinamik disk olduğu için yer kaplamaz)

    public async Task CreateImageFromFolderAsync(
        string sourceFolderPath,
        string destinationImagePath,
        bool useNtfs,
        string fileFilters,
        Action<string, string>? logCallback = null,
        Action<double>? progressCallback = null)
    {
        await Task.Run(() =>
        {
            var extension = Path.GetExtension(destinationImagePath).ToLower();
            if (extension == ".iso")
            {
                CreateIso(sourceFolderPath, destinationImagePath, fileFilters, logCallback, progressCallback);
            }
            else if (extension == ".vhd")
            {
                CreateVhd(sourceFolderPath, destinationImagePath, useNtfs, fileFilters, logCallback, progressCallback);
            }
            else
            {
                throw new NotSupportedException($"'{extension}' uzantılı imaj formatı desteklenmiyor. Yalnızca .vhd ve .iso desteklenir.");
            }
        });
    }

    public async Task CreateIncrementalVhdAsync(
        string sourceFolderPath,
        string parentVhdPath,
        string childVhdPath,
        string fileFilters,
        Action<string, string>? logCallback = null,
        Action<double>? progressCallback = null)
    {
        await Task.Run(() =>
        {
            if (!File.Exists(parentVhdPath))
            {
                throw new FileNotFoundException("Artımlı yedekleme için kaynak ebeveyn (parent) VHD bulunamadı.", parentVhdPath);
            }

            var sourceMappings = BuildSourceMappings(sourceFolderPath);

            logCallback?.Invoke("Info", $"Artımlı VHD oluşturuluyor. Ebeveyn VHD: {Path.GetFileName(parentVhdPath)}");
            
            // 1. Child VHD'yi oluştur ve parent'a bağla
            using (var parentFile = new DiskImageFile(parentVhdPath, FileAccess.Read))
            using (var childStream = File.Create(childVhdPath))
            using (var disk = Disk.InitializeDifferencing(childStream, Ownership.Dispose, parentFile, Ownership.Dispose, parentVhdPath, Path.GetFileName(parentVhdPath), File.GetLastWriteTimeUtc(parentVhdPath)))
            {
                if (disk.Partitions.Count == 0)
                {
                    throw new InvalidOperationException("Ebeveyn VHD diskinde herhangi bir bölüm (partition) bulunamadı.");
                }

                // 2. Child VHD'nin dosya sistemini aç
                var partition = disk.Partitions[0];
                using (var volStream = partition.Open())
                using (var fs = OpenFileSystem(volStream))
                {
                    logCallback?.Invoke("Info", "Ebeveyn dosya sistemi child VHD üzerinden başarıyla bağlandı. Değişiklikler işleniyor...");

                    // 3. Dosyaları kopyala
                    var totalFiles = CountFiles(sourceMappings);
                    int processedFiles = 0;
                    
                    CopySourcesToVfs(sourceMappings, fs, fileFilters, logCallback, progressCallback, ref processedFiles, totalFiles, true);
                }
            }
            
            logCallback?.Invoke("Info", "Artımlı VHD yedeklemesi başarıyla tamamlandı.");
        });
    }

    public async Task<List<VirtualFileNode>> GetImageContentAsync(string imagePath)
    {
        return await Task.Run(() =>
        {
            if (!File.Exists(imagePath)) return new List<VirtualFileNode>();

            var extension = Path.GetExtension(imagePath).ToLower();
            if (extension == ".iso")
            {
                using var stream = File.OpenRead(imagePath);
                using var reader = new CDReader(stream, joliet: true);
                return GetNodesRecursive(reader, "/");
            }
            else if (extension == ".vhd")
            {
                // VHD + differencing parent zinciri için kendi klasöründe arar
                using var disk = OpenVhd(imagePath, FileAccess.Read);
                if (disk.Partitions.Count > 0)
                {
                    var partition = disk.Partitions[0];
                    using var volStream = partition.Open();
                    using var fs = OpenFileSystem(volStream);
                    return GetNodesRecursive(fs, string.Empty);
                }
            }

            return new List<VirtualFileNode>();
        });
    }

    public async Task ExtractFileFromImageAsync(string imagePath, string virtualFilePath, string destinationLocalPath)
    {
        using var vfsStream = await ExtractFileToStreamAsync(imagePath, virtualFilePath);
        using var localStream = File.Create(destinationLocalPath);
        await vfsStream.CopyToAsync(localStream);
    }

    public async Task<Stream> ExtractFileToStreamAsync(string imagePath, string virtualFilePath)
    {
        return await Task.Run<Stream>(() =>
        {
            var extension = Path.GetExtension(imagePath).ToLower();
            if (extension == ".iso")
            {
                var stream = File.OpenRead(imagePath);
                var reader = new CDReader(stream, joliet: true);
                if (reader.FileExists(virtualFilePath))
                {
                    // reader nesnesinin kapanmaması için akışı sarmalıyoruz
                    return new CDFileStreamWrapper(reader.OpenFile(virtualFilePath, FileMode.Open, FileAccess.Read), reader, stream);
                }
                reader.Dispose();
                stream.Dispose();
            }
            else if (extension == ".vhd")
            {
                var disk = OpenVhd(imagePath, FileAccess.Read);
                if (disk.Partitions.Count > 0)
                {
                    var partition = disk.Partitions[0];
                    var volStream = partition.Open();
                    var fs = OpenFileSystem(volStream);
                    var normalizedPath = NormalizeVfsLookupPath(virtualFilePath);
                    if (fs.FileExists(normalizedPath))
                    {
                        return new VhdFileStreamWrapper(fs, fs.OpenFile(normalizedPath, FileMode.Open, FileAccess.Read), volStream, disk, null);
                    }
                    fs.Dispose();
                    volStream.Dispose();
                }
                disk.Dispose();
            }

            throw new FileNotFoundException($"İmaj içinde belirtilen dosya bulunamadı: {virtualFilePath}");
        });
    }

    #region Helper Methods

    private void CreateIso(string sourceFolder, string destPath, string filters, Action<string, string>? log, Action<double>? progress)
    {
        var sourceMappings = BuildSourceMappings(sourceFolder);
        log?.Invoke("Info", $"ISO imajı oluşturuluyor. Kaynak sayısı: {sourceMappings.Count}");
        var builder = new CDBuilder
        {
            UseJoliet = true,
            VolumeIdentifier = "KORUDISK"
        };

        var matcher = new FileMatcher(filters);
        var files = sourceMappings
            .SelectMany(mapping => Directory.GetFiles(mapping.SourcePath, "*", SearchOption.AllDirectories)
                .Select(file => (mapping, file)))
            .ToList();
        int totalFiles = files.Count;
        int processed = 0;

        foreach (var file in files)
        {
            var relativePath = Path.GetRelativePath(file.mapping.SourcePath, file.file);
            var vfsPath = CombineIsoPath(file.mapping.RootPath, relativePath);
            var fileName = Path.GetFileName(file.file);

            if (!matcher.IsMatch(fileName))
            {
                log?.Invoke("Info", $"Dosya filtrelendi: {fileName}");
                processed++;
                continue;
            }

            if (fileName.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
            {
                log?.Invoke("Warning", $"Kilit dosyası atlandı: {vfsPath}");
                processed++;
                ReportProgress(progress, processed, totalFiles);
                continue;
            }

            if (!CanReadFileForBackup(file.file, out var readError))
            {
                log?.Invoke("Warning", $"Dosya okunamadığı için atlandı: {vfsPath} ({readError})");
                processed++;
                ReportProgress(progress, processed, totalFiles);
                continue;
            }

            log?.Invoke("Info", $"ISO'ya Ekleniyor: {vfsPath}");
            builder.AddFile(vfsPath, file.file);
            processed++;
            ReportProgress(progress, processed, totalFiles);
        }

        using var isoStream = File.Create(destPath);
        builder.Build(isoStream);
        log?.Invoke("Info", "ISO imajı başarıyla yazıldı.");
    }

    private static bool CanReadFileForBackup(string filePath, out string? error)
    {
        try
        {
            using var _ = File.Open(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void CreateVhd(string sourceFolder, string destPath, bool useNtfs, string filters, Action<string, string>? log, Action<double>? progress)
    {
        var sourceMappings = BuildSourceMappings(sourceFolder);
        if (useNtfs && !OperatingSystem.IsWindows())
        {
            useNtfs = false;
            log?.Invoke("Warning", "Bu platformda NTFS desteklenmediği için VHD FAT32 olarak oluşturulacak.");
        }

        log?.Invoke("Info", $"VHD oluşturuluyor (Dosya Sistemi: {(useNtfs ? "NTFS" : "FAT32")})");
        
        // Klasör boyutunu hesapla
        long folderSize = GetDirectorySize(sourceMappings);
        long capacity = Math.Max(DefaultVhdCapacity, folderSize * 2 + 500 * 1024 * 1024L); // Kaynak boyutundan büyük disk kapasitesi

        using (var vhdStream = File.Create(destPath))
        using (var disk = Disk.InitializeDynamic(vhdStream, Ownership.Dispose, capacity))
        {
            log?.Invoke("Info", "Disk partition tablosu yapılandırılıyor...");
            var partitionType = useNtfs ? WellKnownPartitionType.WindowsNtfs : WellKnownPartitionType.WindowsFat;
            
            // BiosPartitionTable'da CreatePrimaryPartition yerine doğrudan Create veya CreatePartition kullanılır.
            var partitionTable = BiosPartitionTable.Initialize(disk);
            partitionTable.Create(partitionType, active: true);

            log?.Invoke("Info", "Bölüm biçimlendiriliyor (Formatlanıyor)...");
            var partition = disk.Partitions[0];
            using (var volStream = partition.Open())
            {
                DiscFileSystem fs;
                if (useNtfs && OperatingSystem.IsWindows())
                {
                    fs = NtfsFileSystem.Format(volStream, "KoruDisk", disk.Geometry, 63, disk.Geometry.TotalSectorsLong - 63);
                }
                else
                {
                    fs = FatFileSystem.FormatPartition(volStream, "KoruDisk", disk.Geometry, 0, (int)partition.SectorCount, 0);
                }

                using (fs)
                {
                    log?.Invoke("Info", "Dosyalar VHD içerisine kopyalanıyor...");
                    var totalFiles = CountFiles(sourceMappings);
                    int processedFiles = 0;

                    CopySourcesToVfs(sourceMappings, fs, filters, log, progress, ref processedFiles, totalFiles, false);
                }
            }
        }
        
        log?.Invoke("Info", "VHD disk imajı başarıyla tamamlandı.");
    }

    private void CopyFolderToVfs(
        string localFolder, 
        IFileSystem fs, 
        string vfsDestPath, 
        string filters, 
        Action<string, string>? log, 
        Action<double>? progress, 
        ref int processedFiles, 
        int totalFiles,
        bool isIncremental)
    {
        var matcher = new FileMatcher(filters);
        
        // Klasördeki dosyaları kopyala
        var localFiles = Directory.GetFiles(localFolder);
        foreach (var file in localFiles)
        {
            var fileName = Path.GetFileName(file);

            // FAT32, macOS gizli dosyalarındaki (ör. .DS_Store) baştaki nokta yolunu kabul etmez.
            if (fs is FatFileSystem && fileName.StartsWith(".", StringComparison.Ordinal))
            {
                log?.Invoke("Info", $"FAT32 uyumluluğu için gizli dosya atlandı: {fileName}");
                processedFiles++;
                ReportProgress(progress, processedFiles, totalFiles);
                continue;
            }

            var targetFileName = fs is FatFileSystem
                ? ToFatCompatibleName(fileName)
                : fileName;

            if (fs is FatFileSystem && !string.Equals(targetFileName, fileName, StringComparison.Ordinal))
            {
                log?.Invoke("Info", $"FAT32 uyumluluğu için ad dönüştürüldü: {fileName} -> {targetFileName}");
            }

            var vfsFilePath = CombineVfsPath(vfsDestPath, targetFileName);

            if (!matcher.IsMatch(fileName))
            {
                processedFiles++;
                continue;
            }

            // Artımlı yedeklemede dosya zaten varsa ve boyutu/tarihi değişmediyse kopyalama yapma
            if (isIncremental && fs.FileExists(vfsFilePath))
            {
                var localFileInfo = new FileInfo(file);
                var vfsFileLength = fs.GetFileLength(vfsFilePath);
                var vfsFileTime = fs.GetLastWriteTime(vfsFilePath);
                
                // Zaman karşılaştırması (yaklaşık fark, FAT32/NTFS hassasiyetinden dolayı 2 sn tolerans)
                if (localFileInfo.Length == vfsFileLength && Math.Abs((localFileInfo.LastWriteTimeUtc - vfsFileTime.ToUniversalTime()).TotalSeconds) < 2)
                {
                    processedFiles++;
                    ReportProgress(progress, processedFiles, totalFiles);
                    continue; // Dosya değişmemiş, kopyalamayı atla
                }
            }

            log?.Invoke("Info", $"Yazılıyor: {vfsFilePath}");

            try
            {
                using var srcStream = File.OpenRead(file);
                using var destStream = fs.OpenFile(vfsFilePath, FileMode.Create, FileAccess.Write);
                srcStream.CopyTo(destStream);
            }
            catch (IOException ex)
            {
                log?.Invoke("Warning", $"Dosya okunamadığı için atlandı: {file} ({ex.Message})");
            }
            catch (UnauthorizedAccessException ex)
            {
                log?.Invoke("Warning", $"Dosyaya erişim olmadığı için atlandı: {file} ({ex.Message})");
            }
            
            processedFiles++;
            ReportProgress(progress, processedFiles, totalFiles);
        }

        // Klasördeki alt klasörleri dolaş
        var localDirs = Directory.GetDirectories(localFolder);
        foreach (var dir in localDirs)
        {
            var dirName = Path.GetFileName(dir);

            if (fs is FatFileSystem && dirName.StartsWith(".", StringComparison.Ordinal))
            {
                log?.Invoke("Info", $"FAT32 uyumluluğu için gizli klasör atlandı: {dirName}");
                continue;
            }

            var targetDirName = fs is FatFileSystem
                ? ToFatCompatibleName(dirName)
                : dirName;

            if (fs is FatFileSystem && !string.Equals(targetDirName, dirName, StringComparison.Ordinal))
            {
                log?.Invoke("Info", $"FAT32 uyumluluğu için klasör adı dönüştürüldü: {dirName} -> {targetDirName}");
            }

            var vfsDirPath = CombineVfsPath(vfsDestPath, targetDirName);

            if (!fs.DirectoryExists(vfsDirPath))
            {
                fs.CreateDirectory(vfsDirPath);
            }

            CopyFolderToVfs(dir, fs, vfsDirPath, filters, log, progress, ref processedFiles, totalFiles, isIncremental);
        }
    }

    private List<VirtualFileNode> GetNodesRecursive(IFileSystem fs, string path)
    {
        var list = new List<VirtualFileNode>();

        // Alt dizinleri listele
        var directories = fs.GetDirectories(path);
        foreach (var dir in directories)
        {
            var cleanDirName = GetVirtualLeafName(dir);
            if (string.IsNullOrEmpty(cleanDirName)) cleanDirName = dir.Trim('/', '\\');

            var node = new VirtualFileNode
            {
                Name = cleanDirName,
                FullPath = dir.Replace("\\", "/"),
                IsDirectory = true,
                LastWriteTime = SafeGetLastWriteTime(fs, dir)
            };
            node.Children = GetNodesRecursive(fs, dir);
            list.Add(node);
        }

        // Klasördeki dosyaları listele
        var files = fs.GetFiles(path);
        foreach (var file in files)
        {
            list.Add(new VirtualFileNode
            {
                Name = GetVirtualLeafName(file),
                FullPath = file.Replace("\\", "/"),
                IsDirectory = false,
                Size = fs.GetFileLength(file),
                LastWriteTime = SafeGetLastWriteTime(fs, file)
            });
        }

        return list;
    }

    private DateTime? SafeGetLastWriteTime(IFileSystem fs, string path)
    {
        try
        {
            return fs.GetLastWriteTime(path);
        }
        catch
        {
            return null;
        }
    }

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

    /// <summary>
    /// Differencing VHD zinciri için parent VHD'yi aynı klasörde arayarak disk açar.
    /// Kendi KoruDiskFileLocator sınıfımızı kullanır.
    /// </summary>
    private Disk OpenVhd(string imagePath, FileAccess access)
    {
        return new Disk(imagePath, access);
    }

    private static string CombineVfsPath(string parentPath, string childName)
    {
        if (string.IsNullOrEmpty(parentPath))
        {
            return childName;
        }

        return $"{parentPath.TrimEnd('\\', '/') }\\{childName}";
    }

    private static string NormalizeVfsLookupPath(string path)
    {
        return path.Replace('/', '\\').TrimStart('\\');
    }

    private static string GetVirtualLeafName(string path)
    {
        var normalizedPath = path.TrimEnd('\\', '/').Replace('\\', '/');
        var lastSeparator = normalizedPath.LastIndexOf('/');
        return lastSeparator >= 0 ? normalizedPath[(lastSeparator + 1)..] : normalizedPath;
    }

    private static string ToFatCompatibleName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "unnamed";
        }

        var invalidChars = new HashSet<char>(new[] { '"', '*', '/', ':', '<', '>', '?', '\\', '|', '+' });
        var sanitized = new string(name
            .Select(ch => invalidChars.Contains(ch) || char.IsControl(ch) ? '_' : ch)
            .ToArray());

        sanitized = sanitized.TrimEnd('.', ' ');
        if (sanitized.StartsWith(".", StringComparison.Ordinal))
        {
            sanitized = "_" + sanitized.TrimStart('.');
        }

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = "unnamed";
        }

        if (IsConservativeFatName(sanitized))
        {
            return sanitized;
        }

        var rawExt = Path.GetExtension(sanitized).TrimStart('.');
        var safeExt = new string(rawExt
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
        if (safeExt.Length > 3)
        {
            safeExt = safeExt[..3];
        }

        var rawBase = Path.GetFileNameWithoutExtension(sanitized);
        var safeBase = new string(rawBase
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToUpperInvariant(ch) : '_')
            .ToArray())
            .Trim('_');

        if (string.IsNullOrWhiteSpace(safeBase))
        {
            safeBase = "FILE";
        }

        var hash = ComputeDeterministicNameHash(name);
        var basePrefix = safeBase.Length > 3 ? safeBase[..3] : safeBase;
        var fatBase = $"{basePrefix}_{hash}";

        return string.IsNullOrWhiteSpace(safeExt)
            ? fatBase
            : $"{fatBase}.{safeExt}";
    }

    private static string ComputeDeterministicNameHash(string input)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in input)
            {
                hash ^= ch;
                hash *= 16777619;
            }

            return (hash & 0xFFFF).ToString("X4");
        }
    }

    private static bool IsConservativeFatName(string name)
    {
        var extension = Path.GetExtension(name).TrimStart('.');
        if (extension.Length > 3 || extension.Any(ch => !char.IsLetterOrDigit(ch)))
        {
            return false;
        }

        var baseName = Path.GetFileNameWithoutExtension(name);
        if (baseName.Length is < 1 or > 8)
        {
            return false;
        }

        return baseName.All(ch => char.IsLetterOrDigit(ch) || ch == '_');
    }

    private long GetDirectorySize(string folderPath)
    {
        return GetDirectorySize(BuildSourceMappings(folderPath));
    }

    private int CountFiles(string folderPath)
    {
        return CountFiles(BuildSourceMappings(folderPath));
    }

    private void CopySourcesToVfs(
        IReadOnlyList<SourceMapping> sourceMappings,
        IFileSystem fs,
        string filters,
        Action<string, string>? log,
        Action<double>? progress,
        ref int processedFiles,
        int totalFiles,
        bool isIncremental)
    {
        foreach (var mapping in sourceMappings)
        {
            if (!string.IsNullOrEmpty(mapping.RootPath) && !fs.DirectoryExists(mapping.RootPath))
            {
                fs.CreateDirectory(mapping.RootPath);
            }

            CopyFolderToVfs(mapping.SourcePath, fs, mapping.RootPath, filters, log, progress, ref processedFiles, totalFiles, isIncremental);
        }
    }

    private static List<SourceMapping> BuildSourceMappings(string sourcePaths)
    {
        var directories = sourcePaths
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (directories.Count == 0)
        {
            throw new DirectoryNotFoundException("En az bir kaynak klasör belirtilmelidir.");
        }

        if (directories.Count == 1)
        {
            return new List<SourceMapping>
            {
                new(directories[0], string.Empty)
            };
        }

        var mappings = new List<SourceMapping>(directories.Count);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            var folderName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(folderName))
            {
                folderName = "Root";
            }

            folderName = SanitizeRootFolderName(folderName);

            var uniqueName = folderName;
            var suffix = 2;
            while (!usedNames.Add(uniqueName))
            {
                uniqueName = SanitizeRootFolderName($"{folderName}_{suffix++}");
            }

            mappings.Add(new SourceMapping(directory, uniqueName));
        }

        return mappings;
    }

    private static void ReportProgress(Action<double>? progress, int processedFiles, int totalFiles)
    {
        if (progress == null)
        {
            return;
        }

        if (totalFiles <= 0)
        {
            progress(100);
            return;
        }

        progress((double)processedFiles / totalFiles * 100);
    }

    private static string CombineIsoPath(string rootPath, string relativePath)
    {
        var normalizedRelativePath = relativePath.Replace("\\", "/");
        return string.IsNullOrEmpty(rootPath)
            ? normalizedRelativePath
            : $"{rootPath.Trim('/').Replace("\\", "/")}/{normalizedRelativePath}";
    }

    private static string SanitizeRootFolderName(string name)
    {
        var cleaned = new string(name
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray())
            .Trim('_');

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "ROOT";
        }

        return cleaned.Length <= 8
            ? cleaned
            : cleaned[..8];
    }

    private static long GetDirectorySize(IEnumerable<SourceMapping> sourceMappings)
    {
        return sourceMappings
            .SelectMany(mapping => Directory.GetFiles(mapping.SourcePath, "*", SearchOption.AllDirectories))
            .Select(file => new FileInfo(file).Length)
            .Sum();
    }

    private static int CountFiles(IEnumerable<SourceMapping> sourceMappings)
    {
        return sourceMappings.Sum(mapping => Directory.GetFiles(mapping.SourcePath, "*", SearchOption.AllDirectories).Length);
    }

    private readonly record struct SourceMapping(string SourcePath, string RootPath);

    #endregion
}

public class KoruDiskFileLocator : FileLocator
{
    private readonly string _directory;

    public KoruDiskFileLocator(string directory)
    {
        _directory = directory;
    }

    public override bool Exists(string fileName)
    {
        return File.Exists(Path.Combine(_directory, fileName));
    }

    protected override Stream OpenFile(string fileName, FileMode mode, FileAccess access, FileShare share)
    {
        return new FileStream(Path.Combine(_directory, fileName), mode, access, share);
    }

    public override string GetFileFromPath(string path)
    {
        return Path.GetFileName(path);
    }

    public override string GetDirectoryFromPath(string path)
    {
        return Path.GetDirectoryName(path) ?? string.Empty;
    }

    public override string ResolveRelativePath(string path)
    {
        return Path.GetFullPath(Path.Combine(_directory, path));
    }

    public override DateTime GetLastWriteTimeUtc(string path)
    {
        return File.GetLastWriteTimeUtc(Path.Combine(_directory, path));
    }

    public override bool HasCommonRoot(FileLocator other)
    {
        return other is KoruDiskFileLocator;
    }

    public override FileLocator GetRelativeLocator(string path)
    {
        return new KoruDiskFileLocator(Path.Combine(_directory, path));
    }

    public override string GetFullPath(string path)
    {
        return Path.GetFullPath(Path.Combine(_directory, path));
    }
}

#region Stream Wrappers for Cleanup on Dispose

/// <summary>
/// ISO akışını okurken arka plandaki tüm bağımlı nesneleri temizleyen sarmalayıcı.
/// </summary>
public class CDFileStreamWrapper : Stream
{
    private readonly Stream _fileStream;
    private readonly CDReader _reader;
    private readonly Stream _baseStream;

    public CDFileStreamWrapper(Stream fileStream, CDReader reader, Stream baseStream)
    {
        _fileStream = fileStream;
        _reader = reader;
        _baseStream = baseStream;
    }

    public override bool CanRead => _fileStream.CanRead;
    public override bool CanSeek => _fileStream.CanSeek;
    public override bool CanWrite => _fileStream.CanWrite;
    public override long Length => _fileStream.Length;
    public override long Position { get => _fileStream.Position; set => _fileStream.Position = value; }

    public override void Flush() => _fileStream.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _fileStream.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _fileStream.Seek(offset, origin);
    public override void SetLength(long value) => _fileStream.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _fileStream.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fileStream.Dispose();
            _reader.Dispose();
            _baseStream.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// VHD akışını okurken arka plandaki tüm bağımlı nesneleri (Disk, DiscFileSystem) temizleyen sarmalayıcı.
/// </summary>
public class VhdFileStreamWrapper : Stream
{
    private readonly DiscFileSystem _fs;
    private readonly Stream _fileStream;
    private readonly Stream _volStream;
    private readonly Disk _disk;
    private readonly Stream? _baseStream;

    public VhdFileStreamWrapper(DiscFileSystem fs, Stream fileStream, Stream volStream, Disk disk, Stream? baseStream)
    {
        _fs = fs;
        _fileStream = fileStream;
        _volStream = volStream;
        _disk = disk;
        _baseStream = baseStream;
    }

    public override bool CanRead => _fileStream.CanRead;
    public override bool CanSeek => _fileStream.CanSeek;
    public override bool CanWrite => _fileStream.CanWrite;
    public override long Length => _fileStream.Length;
    public override long Position { get => _fileStream.Position; set => _fileStream.Position = value; }

    public override void Flush() => _fileStream.Flush();
    public override int Read(byte[] buffer, int offset, int count) => _fileStream.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => _fileStream.Seek(offset, origin);
    public override void SetLength(long value) => _fileStream.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _fileStream.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fileStream.Dispose();
            _fs.Dispose();
            _volStream.Dispose();
            _disk.Dispose();
            _baseStream?.Dispose();
        }
        base.Dispose(disposing);
    }
}

#endregion

#region FileMatcher Utility

public class FileMatcher
{
    private readonly List<string> _inclusions = new();
    private readonly List<string> _exclusions = new();

    public FileMatcher(string filterString)
    {
        if (string.IsNullOrWhiteSpace(filterString))
        {
            _inclusions.Add("*");
            return;
        }

        var parts = filterString.Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.StartsWith('!'))
            {
                _exclusions.Add(trimmed[1..]);
            }
            else
            {
                _inclusions.Add(trimmed);
            }
        }

        if (_inclusions.Count == 0)
        {
            _inclusions.Add("*");
        }
    }

    public bool IsMatch(string fileName)
    {
        foreach (var excl in _exclusions)
        {
            if (FitsMask(fileName, excl))
                return false;
        }

        foreach (var incl in _inclusions)
        {
            if (FitsMask(fileName, incl))
                return true;
        }

        return false;
    }

    private static bool FitsMask(string fileName, string mask)
    {
        if (mask == "*" || mask == "*.*") return true;
        var pattern = "^" + Regex.Escape(mask)
            .Replace(@"\*", ".*")
            .Replace(@"\?", ".") + "$";
        return Regex.IsMatch(fileName, pattern, RegexOptions.IgnoreCase);
    }
}

#endregion
