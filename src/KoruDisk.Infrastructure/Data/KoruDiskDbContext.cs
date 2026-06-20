using KoruDisk.Core.Entities;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace KoruDisk.Infrastructure.Data;

public class KoruDiskDbContext : DbContext
{
    private readonly SensitiveDataProtector _sensitiveDataProtector;

    public KoruDiskDbContext(DbContextOptions<KoruDiskDbContext> options, IDataProtectionProvider dataProtectionProvider) : base(options)
    {
        _sensitiveDataProtector = new SensitiveDataProtector(dataProtectionProvider);
    }

    public DbSet<BackupJob> BackupJobs => Set<BackupJob>();
    public DbSet<BackupHistory> BackupHistories => Set<BackupHistory>();
    public DbSet<StorageDestination> StorageDestinations => Set<StorageDestination>();
    public DbSet<BackupLog> BackupLogs => Set<BackupLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        var secretConverter = new ValueConverter<string, string>(
            value => _sensitiveDataProtector.Protect(value),
            value => _sensitiveDataProtector.Unprotect(value));

        modelBuilder.Entity<StorageDestination>()
            .Property(destination => destination.Password)
            .HasConversion(secretConverter);

        modelBuilder.Entity<StorageDestination>()
            .Property(destination => destination.GoogleCredentialJson)
            .HasConversion(secretConverter);

        // BackupJob ile StorageDestination arasında Çoktan-Çoğa (Many-to-Many) ilişki
        modelBuilder.Entity<BackupJob>()
            .HasMany(j => j.Destinations)
            .WithMany();

        // BackupHistory ile BackupLog arasında Bire-Çok (One-to-Many) ilişki (Silindiğinde loglar da silinir)
        modelBuilder.Entity<BackupHistory>()
            .HasMany(h => h.Logs)
            .WithOne()
            .HasForeignKey(l => l.BackupHistoryId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
