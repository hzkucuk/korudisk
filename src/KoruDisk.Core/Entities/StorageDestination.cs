using KoruDisk.Core.Enums;

namespace KoruDisk.Core.Entities;

public class StorageDestination
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public DestinationType Type { get; set; }
    
    // Ortak Alanlar (FTP, SFTP, Yerel Ağ Paylaşımı veya Google Drive Klasör Adı/ID)
    public string PathOrFolder { get; set; } = string.Empty;
    
    // FTP, SFTP Bağlantı Bilgileri
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 21; // SFTP için 22, FTP için 21 varsayılan
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    
    // Google Drive Yetkilendirme Bilgisi (Service Account JSON içeriği)
    public string GoogleCredentialJson { get; set; } = string.Empty;
    
    public bool IsActive { get; set; } = true;
}
