using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;
using System.Text.Json;

namespace BiliDesktop;

public record StoredCredential(string Cookie, DateTimeOffset? Expires);

[SupportedOSPlatform("windows")]
public static class CredentialStore
{
    private static readonly string PathName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliDesktop", "account.dat");

    private const string Prefix = "BiliDesktopCredentialV1\n";
    public static void Save(string cookie, string? file = null, DateTimeOffset? expires = null)
    {
        var path = file ?? PathName;
        var bytes = Encoding.UTF8.GetBytes(Prefix + JsonSerializer.Serialize(new StoredCredential(cookie, expires)));
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, encrypted); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static string? Load(string? file = null) => LoadCredential(file)?.Cookie;

    public static StoredCredential? LoadCredential(string? file = null)
    {
        var path = file ?? PathName;
        if (!File.Exists(path)) return null;
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try
        {
            var value = Encoding.UTF8.GetString(bytes);
            if (!value.StartsWith(Prefix, StringComparison.Ordinal)) return new StoredCredential(value, null);
            var credential = JsonSerializer.Deserialize<StoredCredential>(value[Prefix.Length..]);
            if (credential is null || string.IsNullOrWhiteSpace(credential.Cookie)) throw new InvalidDataException("保存的登录信息格式无效。");
            return credential;
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static void Delete(string? file = null) { var path = file ?? PathName; if (File.Exists(path)) File.Delete(path); }
}
