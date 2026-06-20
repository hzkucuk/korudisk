using Microsoft.AspNetCore.DataProtection;

namespace KoruDisk.Infrastructure.Data;

internal sealed class SensitiveDataProtector
{
    private const string EncryptedPrefix = "enc::";

    private readonly IDataProtector _protector;

    public SensitiveDataProtector(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector("KoruDisk.StorageDestinationSecrets.v1");
    }

    public string Protect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        if (value.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            return value;
        }

        return EncryptedPrefix + _protector.Protect(value);
    }

    public string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        if (!value.StartsWith(EncryptedPrefix, StringComparison.Ordinal))
        {
            return value;
        }

        var payload = value[EncryptedPrefix.Length..];

        try
        {
            return _protector.Unprotect(payload);
        }
        catch
        {
            return value;
        }
    }
}