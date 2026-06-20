using KoruDisk.Core.Entities;
using KoruDisk.Core.Enums;
using KoruDisk.Core.Interfaces;
using KoruDisk.Infrastructure.Data;
using KoruDisk.Infrastructure.Services;
using KoruDisk.Infrastructure.Compression;
using KoruDisk.Infrastructure.Targets;
using KoruDisk.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.IO;
using System.Text;
using Xunit;

namespace KoruDisk.Tests;

public class KoruDiskTests : IDisposable
{
    private readonly string _testRoot;
    private readonly string _sourceFolder;
    private readonly string _outputFolder;

    public KoruDiskTests()
    {
        // Testlerin çalışacağı izole geçici klasörleri hazırlıyoruz
        _testRoot = Path.Combine(Path.GetTempPath(), $"KoruDiskTest_{Guid.NewGuid():N}");
        _sourceFolder = Path.Combine(_testRoot, "Source");
        _outputFolder = Path.Combine(_testRoot, "Output");

        Directory.CreateDirectory(_sourceFolder);
        Directory.CreateDirectory(_outputFolder);
    }

    [Fact]
    public async Task CreateVhdAndVerifyContent_ShouldCreateValidVhd()
    {
        // 1. Arrange: Test dosyaları ekle
        var file1Path = Path.Combine(_sourceFolder, "test1.txt");
        var file2Path = Path.Combine(_sourceFolder, "test2.txt");
        var subFolderPath = Path.Combine(_sourceFolder, "Sub");
        var subFilePath = Path.Combine(subFolderPath, "subt.txt");

        await File.WriteAllTextAsync(file1Path, "KoruDisk Test Dosyası 1 Content");
        await File.WriteAllTextAsync(file2Path, "KoruDisk Test Dosyası 2 Content");
        Directory.CreateDirectory(subFolderPath);
        await File.WriteAllTextAsync(subFilePath, "Subfolder Dosya İçeriği");

        var imageService = new DiscUtilsImageService();
        var vhdPath = Path.Combine(_outputFolder, "backup.vhd");

        // 2. Act: VHD oluştur
        await imageService.CreateImageFromFolderAsync(_sourceFolder, vhdPath, useNtfs: false, fileFilters: "*.*");

        // 3. Assert: VHD dosyasının oluştuğunu ve içeriğinin doğru olduğunu doğrula
        Assert.True(File.Exists(vhdPath));
        
        var nodes = await imageService.GetImageContentAsync(vhdPath);
        Assert.NotEmpty(nodes);

        // Kök dizindeki test1.txt ve test2.txt kontrolü
        Assert.Contains(nodes, n => string.Equals(n.Name, "test1.txt", StringComparison.OrdinalIgnoreCase) && !n.IsDirectory && n.Size > 0);
        Assert.Contains(nodes, n => string.Equals(n.Name, "test2.txt", StringComparison.OrdinalIgnoreCase) && !n.IsDirectory && n.Size > 0);
        
        // Sub klasör kontrolü
        var subFolderNode = nodes.FirstOrDefault(n => string.Equals(n.Name, "Sub", StringComparison.OrdinalIgnoreCase) && n.IsDirectory);
        Assert.NotNull(subFolderNode);
        Assert.Contains(subFolderNode.Children, n => string.Equals(n.Name, "subt.txt", StringComparison.OrdinalIgnoreCase) && !n.IsDirectory);
    }

    [Fact]
    public async Task CreateIsoAndVerifyContent_ShouldCreateReadableIso()
    {
        var file1Path = Path.Combine(_sourceFolder, "test1.txt");
        var subFolderPath = Path.Combine(_sourceFolder, "Sub");
        var subFilePath = Path.Combine(subFolderPath, "subt.txt");

        await File.WriteAllTextAsync(file1Path, "ISO Test Dosyasi 1");
        Directory.CreateDirectory(subFolderPath);
        await File.WriteAllTextAsync(subFilePath, "ISO Alt Klasor Dosyasi");

        var imageService = new DiscUtilsImageService();
        var isoPath = Path.Combine(_outputFolder, "backup.iso");

        await imageService.CreateImageFromFolderAsync(_sourceFolder, isoPath, useNtfs: false, fileFilters: "*.*");

        Assert.True(File.Exists(isoPath));

        var nodes = await imageService.GetImageContentAsync(isoPath);
        Assert.NotEmpty(nodes);
        Assert.Contains(nodes, n => string.Equals(n.Name, "test1.txt", StringComparison.OrdinalIgnoreCase) && !n.IsDirectory);

        var subFolderNode = nodes.FirstOrDefault(n => string.Equals(n.Name, "Sub", StringComparison.OrdinalIgnoreCase) && n.IsDirectory);
        Assert.NotNull(subFolderNode);
        Assert.Contains(subFolderNode.Children, n => string.Equals(n.Name, "subt.txt", StringComparison.OrdinalIgnoreCase) && !n.IsDirectory);

        await using var extracted = await imageService.ExtractFileToStreamAsync(isoPath, "Sub/subt.txt");
        using var reader = new StreamReader(extracted, Encoding.UTF8, leaveOpen: false);
        var content = await reader.ReadToEndAsync();
        Assert.Equal("ISO Alt Klasor Dosyasi", content);
    }

    [Fact]
    public async Task CreateIncrementalVhd_ShouldLinkToParentAndStoreDeltas()
    {
        var imageService = new DiscUtilsImageService();
        
        // 1. Ebeveyn (Parent) VHD için ilk dosyayı ekle ve imajı oluştur
        var firstFile = Path.Combine(_sourceFolder, "first.txt");
        await File.WriteAllTextAsync(firstFile, "İlk Yedek İçeriği");

        var parentVhd = Path.Combine(_outputFolder, "parent.vhd");
        await imageService.CreateImageFromFolderAsync(_sourceFolder, parentVhd, useNtfs: false, fileFilters: "*.*");

        // 2. Alt (Child) VHD için kaynak klasöre yeni bir dosya ekle ve artımlı yedek oluştur
        var secondFile = Path.Combine(_sourceFolder, "second.txt");
        await File.WriteAllTextAsync(secondFile, "İkinci Artımlı Yedek İçeriği");

        var childVhd = Path.Combine(_outputFolder, "child.vhd");
        await imageService.CreateIncrementalVhdAsync(_sourceFolder, parentVhd, childVhd, fileFilters: "*.*");

        // 3. Doğrulama: Child VHD'yi okuduğumuzda hem first.txt hem de second.txt gelmeli
        Assert.True(File.Exists(childVhd));

        var nodes = await imageService.GetImageContentAsync(childVhd);
        Assert.NotEmpty(nodes);
        
        // Child VHD'yi açtığımızda parent VHD'deki dosyayı da birleşik olarak görmeliyiz (VHD chaining)
        Assert.Contains(nodes, n => string.Equals(n.Name, "first.txt", StringComparison.OrdinalIgnoreCase) && !n.IsDirectory);
        Assert.Contains(nodes, n => string.Equals(n.Name, "second.txt", StringComparison.OrdinalIgnoreCase) && !n.IsDirectory);
    }

    [Fact]
    public async Task CompressionServices_ShouldCompressAndDecompressCorrectly()
    {
        // 1. Arrange: Test dosyası oluştur
        var sourceFile = Path.Combine(_sourceFolder, "data.txt");
        var originalText = "KoruDisk Sıkıştırma Motoru Test Verisi. 1234567890.";
        await File.WriteAllTextAsync(sourceFile, originalText);

        var compressedFile = Path.Combine(_outputFolder, "data.zip");
        var decompressedFile = Path.Combine(_outputFolder, "data_out.txt");

        var zipService = new ZipCompressionService();

        // 2. Act: Sıkıştır ve geri aç
        await zipService.CompressFileAsync(sourceFile, compressedFile);
        Assert.True(File.Exists(compressedFile));

        await zipService.DecompressFileAsync(compressedFile, decompressedFile);
        Assert.True(File.Exists(decompressedFile));

        // 3. Assert: İçerik bütünlüğünü doğrula
        var decompressedText = await File.ReadAllTextAsync(decompressedFile);
        Assert.Equal(originalText, decompressedText);
    }

    [Fact]
    public async Task CreateVhdFromMultipleSources_ShouldCreateSeparateRootFolders()
    {
        var secondSourceFolder = Path.Combine(_testRoot, "SourceTwo");
        Directory.CreateDirectory(secondSourceFolder);

        await File.WriteAllTextAsync(Path.Combine(_sourceFolder, "first.txt"), "Birinci klasor icerigi");
        await File.WriteAllTextAsync(Path.Combine(secondSourceFolder, "second.txt"), "Ikinci klasor icerigi");

        var imageService = new DiscUtilsImageService();
        var vhdPath = Path.Combine(_outputFolder, "multi-source.vhd");

        await imageService.CreateImageFromFolderAsync(
            $"{_sourceFolder};{secondSourceFolder}",
            vhdPath,
            useNtfs: false,
            fileFilters: "*.*");

        var nodes = await imageService.GetImageContentAsync(vhdPath);

        var firstRoot = nodes.FirstOrDefault(n => string.Equals(n.Name, "Source", StringComparison.OrdinalIgnoreCase) && n.IsDirectory);
        var secondRoot = nodes.FirstOrDefault(n => string.Equals(n.Name, "SourceTw", StringComparison.OrdinalIgnoreCase) && n.IsDirectory);

        Assert.NotNull(firstRoot);
        Assert.NotNull(secondRoot);
        Assert.Contains(firstRoot.Children, n => string.Equals(n.Name, "first.txt", StringComparison.OrdinalIgnoreCase) && !n.IsDirectory);
        Assert.Contains(secondRoot.Children, n => string.Equals(n.Name, "second.txt", StringComparison.OrdinalIgnoreCase) && !n.IsDirectory);
    }

    [Fact]
    public async Task ExecuteBackupJobAsync_OnNonWindows_ShouldFallbackFromNtfsAndSucceed()
    {
        var sourceFile = Path.Combine(_sourceFolder, "photo.txt");
        await File.WriteAllTextAsync(sourceFile, "platform fallback test");

        var destinationFolder = Path.Combine(_outputFolder, "RemoteCopy");
        var dbPath = Path.Combine(_testRoot, "backup-service.db");
        var provider = new TestDataProtectionProvider();
        var options = new DbContextOptionsBuilder<KoruDiskDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        await using var dbContext = new KoruDiskDbContext(options, provider);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        var destination = new StorageDestination
        {
            Name = "Local Target",
            Type = DestinationType.LocalOrNetwork,
            PathOrFolder = destinationFolder,
            IsActive = true
        };

        var job = new BackupJob
        {
            Name = "Mac Backup",
            SourcePaths = _sourceFolder,
            FileFilters = "*.*",
            Compression = CompressionType.None,
            Versioning = VersioningType.Full,
            RetentionCount = 5,
            IsActive = true,
            Destinations = new List<StorageDestination> { destination }
        };

        dbContext.BackupJobs.Add(job);
        await dbContext.SaveChangesAsync();

        var backupService = new BackupService(
            dbContext,
            new DiscUtilsImageService(),
            new IStorageTarget[] { new LocalNetworkTarget() },
            Array.Empty<ICompressionService>());

        var history = await backupService.ExecuteBackupJobAsync(job);

        Assert.True(string.Equals("Success", history.Status, StringComparison.Ordinal), history.ErrorMessage);
        Assert.True(File.Exists(history.ImagePath));

        var expectedImageExtension = OperatingSystem.IsWindows() ? "*.vhd" : "*.iso";
        var copiedFiles = Directory.GetFiles(destinationFolder, expectedImageExtension);
        Assert.Single(copiedFiles);
    }

    [Fact]
    public async Task ExecuteBackupJobAsync_WhenCancellationRequested_ShouldMarkHistoryAsCancelled()
    {
        var sourceFile = Path.Combine(_sourceFolder, "cancel.txt");
        await File.WriteAllTextAsync(sourceFile, "cancel me");

        var dbPath = Path.Combine(_testRoot, "backup-cancel.db");
        var provider = new TestDataProtectionProvider();
        var options = new DbContextOptionsBuilder<KoruDiskDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        await using var dbContext = new KoruDiskDbContext(options, provider);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        var job = new BackupJob
        {
            Name = "Cancelled Backup",
            SourcePaths = _sourceFolder,
            FileFilters = "*.*",
            Compression = CompressionType.None,
            Versioning = VersioningType.Full,
            RetentionCount = 5,
            IsActive = true
        };

        dbContext.BackupJobs.Add(job);
        await dbContext.SaveChangesAsync();

        var backupService = new BackupService(
            dbContext,
            new DiscUtilsImageService(),
            new IStorageTarget[] { new LocalNetworkTarget() },
            Array.Empty<ICompressionService>());

        using var cancellationTokenSource = new CancellationTokenSource();
        cancellationTokenSource.Cancel();

        var history = await backupService.ExecuteBackupJobAsync(job, cancellationToken: cancellationTokenSource.Token);

        Assert.Equal("Failed", history.Status);
        Assert.Equal("İşlem kullanıcı tarafından iptal edildi.", history.ErrorMessage);
        Assert.Contains(history.Logs, log => log.Level == "Warning" && log.Message.Contains("iptal edildi", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifyBackupIntegrityAsync_ForCompressedBackup_ShouldSucceed()
    {
        var sourceFile = Path.Combine(_sourceFolder, "integrity.txt");
        await File.WriteAllTextAsync(sourceFile, "integrity verification content");

        var dbPath = Path.Combine(_testRoot, "backup-integrity.db");
        var provider = new TestDataProtectionProvider();
        var options = new DbContextOptionsBuilder<KoruDiskDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        await using var dbContext = new KoruDiskDbContext(options, provider);
        await dbContext.Database.EnsureDeletedAsync();
        await dbContext.Database.EnsureCreatedAsync();

        var job = new BackupJob
        {
            Name = "Integrity Backup",
            SourcePaths = _sourceFolder,
            FileFilters = "*.*",
            Compression = CompressionType.Zip,
            Versioning = VersioningType.Full,
            RetentionCount = 3,
            IsActive = true
        };

        dbContext.BackupJobs.Add(job);
        await dbContext.SaveChangesAsync();

        var backupService = new BackupService(
            dbContext,
            new DiscUtilsImageService(),
            new IStorageTarget[] { new LocalNetworkTarget() },
            new ICompressionService[] { new ZipCompressionService() });

        var history = await backupService.ExecuteBackupJobAsync(job);

        Assert.Equal("Success", history.Status);
        Assert.True(File.Exists(history.ImagePath));
        Assert.True(File.Exists(history.ImagePath + ".integrity.json"));

        await backupService.VerifyBackupIntegrityAsync(history);

        var savedHistory = await dbContext.BackupHistories.AsNoTracking().FirstAsync(item => item.Id == history.Id);
        Assert.Equal("Verified", savedHistory.IntegrityStatus);
        Assert.False(string.IsNullOrWhiteSpace(savedHistory.IntegrityMessage));
        Assert.True(savedHistory.IntegrityCheckedAtUtc.HasValue);
    }

    [Fact]
    public void ScheduledBackupPlanner_ShouldMarkDueJobAndAdvanceNextRun()
    {
        var now = new DateTime(2026, 6, 20, 12, 0, 0, DateTimeKind.Local);
        var job = new BackupJob
        {
            Id = 7,
            Name = "Cron Job",
            CronExpression = "*/5 * * * *",
            IsActive = true,
            NextRun = now.AddMinutes(-1)
        };

        var plan = ScheduledBackupPlanner.CreatePlan(new[] { job }, now, _ => false);

        Assert.True(plan.HasChanges);
        Assert.Single(plan.JobsToRun);
        Assert.Equal(job.Id, plan.JobsToRun[0].JobId);
        Assert.Equal(now, job.LastRun);
        Assert.NotNull(job.NextRun);
        Assert.True(job.NextRun > now);
    }

    [Fact]
    public void ScheduledBackupPlanner_ShouldSkipRunningJob()
    {
        var now = new DateTime(2026, 6, 20, 12, 0, 0, DateTimeKind.Local);
        var job = new BackupJob
        {
            Id = 8,
            Name = "Running Cron Job",
            CronExpression = "*/5 * * * *",
            IsActive = true,
            NextRun = now.AddMinutes(-1)
        };

        var plan = ScheduledBackupPlanner.CreatePlan(new[] { job }, now, jobId => jobId == job.Id);

        Assert.False(plan.JobsToRun.Any());
        Assert.Null(job.LastRun);
        Assert.Equal(now.AddMinutes(-1), job.NextRun);
    }

    [Fact]
    public void ScheduledBackupPlanner_ShouldClearNextRunForInvalidCron()
    {
        var job = new BackupJob
        {
            Id = 9,
            Name = "Broken Cron Job",
            CronExpression = "not-a-cron",
            IsActive = true,
            NextRun = new DateTime(2026, 6, 20, 13, 0, 0, DateTimeKind.Local)
        };

        var warnings = new List<(int JobId, string Expression)>();

        var plan = ScheduledBackupPlanner.CreatePlan(
            new[] { job },
            new DateTime(2026, 6, 20, 12, 0, 0, DateTimeKind.Local),
            _ => false,
            (jobId, expression) => warnings.Add((jobId, expression)));

        Assert.True(plan.HasChanges);
        Assert.Empty(plan.JobsToRun);
        Assert.Null(job.NextRun);
        Assert.Single(warnings);
        Assert.Equal(job.Id, warnings[0].JobId);
    }

    [Fact]
    public async Task StorageDestinationSecrets_ShouldBeEncryptedAtRestAndRoundTrip()
    {
        var dbPath = Path.Combine(_testRoot, "secrets.db");
        var provider = new TestDataProtectionProvider();
        var options = new DbContextOptionsBuilder<KoruDiskDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;

        const string plainPassword = "super-secret-password";
        const string plainGoogleJson = "{\"type\":\"service_account\"}";

        await using (var dbContext = new KoruDiskDbContext(options, provider))
        {
            await dbContext.Database.EnsureDeletedAsync();
            await dbContext.Database.EnsureCreatedAsync();

            dbContext.StorageDestinations.Add(new StorageDestination
            {
                Name = "Encrypted Destination",
                Password = plainPassword,
                GoogleCredentialJson = plainGoogleJson,
                PathOrFolder = "/tmp",
                IsActive = true
            });

            await dbContext.SaveChangesAsync();
        }

        await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Password, GoogleCredentialJson FROM StorageDestinations LIMIT 1";

            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());

            var storedPassword = reader.GetString(0);
            var storedGoogleJson = reader.GetString(1);

            Assert.NotEqual(plainPassword, storedPassword);
            Assert.NotEqual(plainGoogleJson, storedGoogleJson);
            Assert.StartsWith("enc::", storedPassword, StringComparison.Ordinal);
            Assert.StartsWith("enc::", storedGoogleJson, StringComparison.Ordinal);
        }

        await using (var dbContext = new KoruDiskDbContext(options, provider))
        {
            var destination = await dbContext.StorageDestinations.SingleAsync();
            Assert.Equal(plainPassword, destination.Password);
            Assert.Equal(plainGoogleJson, destination.GoogleCredentialJson);
        }
    }

    private sealed class TestDataProtectionProvider : IDataProtectionProvider
    {
        public IDataProtector CreateProtector(string purpose)
        {
            return new TestDataProtector(purpose);
        }
    }

    private sealed class TestDataProtector : IDataProtector
    {
        private readonly string _purpose;

        public TestDataProtector(string purpose)
        {
            _purpose = purpose;
        }

        public IDataProtector CreateProtector(string purpose)
        {
            return new TestDataProtector($"{_purpose}|{purpose}");
        }

        public byte[] Protect(byte[] plaintext)
        {
            var payload = Convert.ToBase64String(plaintext);
            return Encoding.UTF8.GetBytes($"{_purpose}:{payload}");
        }

        public byte[] Unprotect(byte[] protectedData)
        {
            var serialized = Encoding.UTF8.GetString(protectedData);
            var separator = serialized.IndexOf(':');
            var payload = separator >= 0 ? serialized[(separator + 1)..] : serialized;
            return Convert.FromBase64String(payload);
        }
    }

    public void Dispose()
    {
        // Test bittiğinde oluşturulan tüm geçici dosyaları temizliyoruz
        if (Directory.Exists(_testRoot))
        {
            try
            {
                Directory.Delete(_testRoot, recursive: true);
            }
            catch
            {
                // İşletim sistemi dosya kilitlemelerinden dolayı silinemezse yoksay
            }
        }
    }
}
