using KoruDisk.Core.Interfaces;
using KoruDisk.Infrastructure.Data;
using KoruDisk.Infrastructure.Services;
using KoruDisk.Infrastructure.Targets;
using KoruDisk.Infrastructure.Compression;
using KoruDisk.Web.Components;
using KoruDisk.Web.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Net;
using System.Net.Sockets;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Tercih edilen port doluysa yakın bir boş porta otomatik düşerek başlatır.
var preferredPort = builder.Configuration.GetValue<int?>("KoruDisk:PreferredPort") ?? 5000;
var selectedPort = FindAvailablePort(preferredPort, preferredPort + 20);
builder.WebHost.ConfigureKestrel(options => options.ListenLocalhost(selectedPort));

// SQLite Veritabanı Yolunu Uygulama Klasörü Olarak Belirliyoruz
var dbPath = Path.Combine(AppContext.BaseDirectory, "korudisk.db");
var dataProtectionKeysPath = ResolveConfiguredPath(
    builder.Configuration["DataProtection:KeyPath"],
    builder.Environment.ContentRootPath,
    Path.Combine(AppContext.BaseDirectory, "DataProtectionKeys"));
var dataProtectionApplicationName = builder.Configuration["DataProtection:ApplicationName"];
Directory.CreateDirectory(dataProtectionKeysPath);

var dataProtectionBuilder = builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath))
    .SetApplicationName(string.IsNullOrWhiteSpace(dataProtectionApplicationName) ? "KoruDisk" : dataProtectionApplicationName);

var dataProtectionCertificateConfigured = ConfigureDataProtectionCertificate(dataProtectionBuilder, builder.Configuration, builder.Environment.ContentRootPath);

builder.Services.AddDbContext<KoruDiskDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

// Razor ve Blazor Server Bileşenleri
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Servislerimizin DI Kayıtları
builder.Services.AddSingleton<BackupCoordinator>();
builder.Services.AddHostedService<ScheduledBackupService>();
builder.Services.AddScoped<IDiskImageService, DiscUtilsImageService>();
builder.Services.AddScoped<IBackupService, BackupService>();

// Hedef Depolar Adaptör Kayıtları
builder.Services.AddScoped<IStorageTarget, LocalNetworkTarget>();
builder.Services.AddScoped<IStorageTarget, FtpTarget>();
builder.Services.AddScoped<IStorageTarget, SftpTarget>();
builder.Services.AddScoped<IStorageTarget, GoogleDriveTarget>();

// Sıkıştırma Servisleri Kayıtları
builder.Services.AddScoped<ICompressionService, ZipCompressionService>();
builder.Services.AddScoped<ICompressionService, GzipCompressionService>();
builder.Services.AddScoped<ICompressionService, DeflateCompressionService>();

var app = builder.Build();

// Veritabanı ve Tabloların İlk Açılışta Otomatik Oluşturulması
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<KoruDiskDbContext>();
    dbContext.Database.EnsureCreated();
    await EnsureBackupHistorySchemaAsync(dbContext);
}

if (selectedPort != preferredPort)
{
    app.Logger.LogWarning("Configured port {PreferredPort} was busy. KoruDisk is listening on fallback port {SelectedPort}.", preferredPort, selectedPort);
}

// HTTP Boru Hattı Ayarları
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();

    if (!dataProtectionCertificateConfigured)
    {
        app.Logger.LogWarning("Data Protection key encryption is not configured. Set DataProtection:CertificateThumbprint or DataProtection:CertificatePath before production deployment.");
    }
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapStaticAssets();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// İmaj İçinden Dosya İndirme Uç Noktası (Minimal API)
app.MapGet("/api/download-from-image", async (string imagePath, string virtualPath, IDiskImageService diskService) =>
{
    if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(virtualPath))
    {
        return Results.BadRequest("imagePath ve virtualPath parametreleri zorunludur.");
    }

    try
    {
        var stream = await diskService.ExtractFileToStreamAsync(imagePath, virtualPath);
        var fileName = Path.GetFileName(virtualPath);
        return Results.File(stream, "application/octet-stream", fileName);
    }
    catch (FileNotFoundException ex)
    {
        return Results.NotFound(ex.Message);
    }
    catch (DirectoryNotFoundException ex)
    {
        return Results.NotFound(ex.Message);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(ex.Message);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Sanal diskten dosya ayıklanırken hata oluştu: {ex.Message}");
    }
});

app.MapGet("/api/export-backup-logs", async (int historyId, string? format, KoruDiskDbContext dbContext) =>
{
    var history = await dbContext.BackupHistories
        .AsNoTracking()
        .FirstOrDefaultAsync(item => item.Id == historyId);

    if (history == null)
    {
        return Results.NotFound("Yedekleme geçmişi bulunamadı.");
    }

    var logs = await dbContext.BackupLogs
        .AsNoTracking()
        .Where(item => item.BackupHistoryId == historyId)
        .OrderBy(item => item.Timestamp)
        .ToListAsync();

    var normalizedFormat = string.IsNullOrWhiteSpace(format) ? "txt" : format.Trim().ToLowerInvariant();
    var safeJobName = string.Concat(history.JobName.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
    var baseFileName = $"{safeJobName}_v{history.VersionNumber}_logs";

    if (normalizedFormat == "json")
    {
        var payload = JsonSerializer.Serialize(new
        {
            history.Id,
            history.JobName,
            history.VersionNumber,
            history.Timestamp,
            history.Status,
            history.ImagePath,
            history.ErrorMessage,
            Logs = logs.Select(log => new
            {
                log.Timestamp,
                log.Level,
                log.Message
            })
        }, new JsonSerializerOptions { WriteIndented = true });

        return Results.File(Encoding.UTF8.GetBytes(payload), "application/json; charset=utf-8", $"{baseFileName}.json");
    }

    var builder = new StringBuilder();
    builder.AppendLine($"Görev: {history.JobName}");
    builder.AppendLine($"Sürüm: v{history.VersionNumber}");
    builder.AppendLine($"Tarih: {history.Timestamp.ToLocalTime():dd.MM.yyyy HH:mm:ss}");
    builder.AppendLine($"Durum: {history.Status}");
    builder.AppendLine($"İmaj: {history.ImagePath}");
    if (!string.IsNullOrWhiteSpace(history.ErrorMessage))
    {
        builder.AppendLine($"Hata: {history.ErrorMessage}");
    }
    builder.AppendLine();
    builder.AppendLine("Loglar:");

    foreach (var log in logs)
    {
        builder.AppendLine($"[{log.Timestamp.ToLocalTime():HH:mm:ss}] [{log.Level}] {log.Message}");
    }

    return Results.File(Encoding.UTF8.GetBytes(builder.ToString()), "text/plain; charset=utf-8", $"{baseFileName}.txt");
});

app.MapPost("/api/verify-backup-integrity", async (
    int historyId,
    KoruDiskDbContext dbContext,
    IBackupService backupService,
    CancellationToken cancellationToken) =>
{
    var history = await dbContext.BackupHistories
        .AsNoTracking()
        .FirstOrDefaultAsync(item => item.Id == historyId, cancellationToken);

    if (history == null)
    {
        return Results.NotFound(new
        {
            Success = false,
            Message = "Yedekleme geçmişi bulunamadı."
        });
    }

    if (string.IsNullOrWhiteSpace(history.ImagePath) || !File.Exists(history.ImagePath))
    {
        return Results.BadRequest(new
        {
            Success = false,
            Message = "Doğrulanacak yedek dosyası bulunamadı."
        });
    }

    var logs = new List<object>();

    try
    {
        await backupService.VerifyBackupIntegrityAsync(
            history,
            (level, message) => logs.Add(new
            {
                Timestamp = DateTime.UtcNow,
                Level = level,
                Message = message
            }),
            cancellationToken);

        return Results.Ok(new
        {
            Success = true,
            Message = "Bütünlük doğrulaması başarılı.",
            HistoryId = history.Id,
            Logs = logs
        });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new
        {
            Success = false,
            Message = ex.Message,
            HistoryId = history.Id,
            Logs = logs
        });
    }
});

// Otomatik Tarayıcı Başlatma Mekanizması
_ = Task.Run(async () =>
{
    await Task.Delay(1500); // Sunucunun ayağa kalkmasını bekliyoruz
    var url = $"http://localhost:{selectedPort}";
    try
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Process.Start(new ProcessStartInfo("cmd", $"/c start {url}") { CreateNoWindow = true });
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Process.Start("open", url);
        }
        else
        {
            Process.Start("xdg-open", url);
        }
    }
    catch
    {
        // Tarayıcı otomatik açılamazsa sessizce devam et (Kullanıcı manuel açabilir)
    }
});

app.Run();

static int FindAvailablePort(int startInclusive, int endInclusive)
{
    for (var port = startInclusive; port <= endInclusive; port++)
    {
        if (IsPortAvailable(port))
        {
            return port;
        }
    }

    throw new InvalidOperationException($"No available port found in range {startInclusive}-{endInclusive}.");
}

static bool IsPortAvailable(int port)
{
    try
    {
        using var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return true;
    }
    catch
    {
        return false;
    }
}

static async Task EnsureBackupHistorySchemaAsync(KoruDiskDbContext dbContext)
{
    if (!dbContext.Database.IsSqlite())
    {
        return;
    }

    var connection = dbContext.Database.GetDbConnection();
    var shouldCloseConnection = false;
    if (connection.State != ConnectionState.Open)
    {
        await connection.OpenAsync();
        shouldCloseConnection = true;
    }

    try
    {
        var existingColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA table_info('BackupHistories');";
            await using var reader = await pragmaCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (reader.GetValue(1) is string name && !string.IsNullOrWhiteSpace(name))
                {
                    existingColumns.Add(name);
                }
            }
        }

        if (!existingColumns.Contains("IntegrityStatus"))
        {
            await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE BackupHistories ADD COLUMN IntegrityStatus TEXT NOT NULL DEFAULT 'Pending';");
        }

        if (!existingColumns.Contains("IntegrityCheckedAtUtc"))
        {
            await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE BackupHistories ADD COLUMN IntegrityCheckedAtUtc TEXT NULL;");
        }

        if (!existingColumns.Contains("IntegrityMessage"))
        {
            await dbContext.Database.ExecuteSqlRawAsync("ALTER TABLE BackupHistories ADD COLUMN IntegrityMessage TEXT NOT NULL DEFAULT ''; ");
        }
    }
    finally
    {
        if (shouldCloseConnection)
        {
            await connection.CloseAsync();
        }
    }
}

static bool ConfigureDataProtectionCertificate(IDataProtectionBuilder dataProtectionBuilder, IConfiguration configuration, string contentRootPath)
{
    var certificateThumbprint = configuration["DataProtection:CertificateThumbprint"];
    if (!string.IsNullOrWhiteSpace(certificateThumbprint))
    {
        dataProtectionBuilder.ProtectKeysWithCertificate(certificateThumbprint);
        return true;
    }

    var certificatePath = configuration["DataProtection:CertificatePath"];
    if (string.IsNullOrWhiteSpace(certificatePath))
    {
        return false;
    }

    var resolvedCertificatePath = ResolveConfiguredPath(certificatePath, contentRootPath, certificatePath);
    if (!File.Exists(resolvedCertificatePath))
    {
        throw new FileNotFoundException("Configured Data Protection certificate file was not found.", resolvedCertificatePath);
    }

    var certificatePassword = configuration["DataProtection:CertificatePassword"];
    var certificateExtension = Path.GetExtension(resolvedCertificatePath);
    var certificate = string.Equals(certificateExtension, ".pfx", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(certificateExtension, ".p12", StringComparison.OrdinalIgnoreCase)
        ? X509CertificateLoader.LoadPkcs12FromFile(
            resolvedCertificatePath,
            certificatePassword ?? string.Empty,
            X509KeyStorageFlags.DefaultKeySet,
            Pkcs12LoaderLimits.Defaults)
        : X509CertificateLoader.LoadCertificateFromFile(resolvedCertificatePath);

    dataProtectionBuilder.ProtectKeysWithCertificate(certificate);
    return true;
}

static string ResolveConfiguredPath(string? configuredPath, string contentRootPath, string fallbackPath)
{
    if (string.IsNullOrWhiteSpace(configuredPath))
    {
        return fallbackPath;
    }

    return Path.IsPathRooted(configuredPath)
        ? configuredPath
        : Path.GetFullPath(Path.Combine(contentRootPath, configuredPath));
}
