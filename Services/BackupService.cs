using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WgSwitch.Services;

public sealed class BackupServer
{
    public string Name { get; set; } = "";
    public string PingHost { get; set; } = "10.8.0.1";
    /// <summary>Содержимое .conf целиком, с приватным ключом: без него после потери ПК не войти в туннель.</summary>
    public string Config { get; set; } = "";
}

public sealed class BackupPayload
{
    public int Version { get; set; } = 1;
    public DateTime Created { get; set; }
    public string? Selected { get; set; }
    public List<BackupServer> Servers { get; set; } = [];
}

[JsonSerializable(typeof(BackupPayload))]
internal partial class BackupJson : JsonSerializerContext;

/// <summary>
/// Резервная копия серверов: имена, IP для пинга и сами конфиги. Файл зашифрован паролем
/// (AES-256-GCM, ключ из пароля через PBKDF2-SHA256), его можно держать в облаке.
/// Формат: "WGSB1" | соль 16 | nonce 12 | tag 16 | шифротекст JSON.
/// </summary>
public static class BackupService
{
    public const string Extension = ".wgsb";
    public const int MinPasswordLength = 8;

    private static readonly byte[] Magic = "WGSB1"u8.ToArray();
    private const int Iterations = 600_000, SaltSize = 16, NonceSize = 12, TagSize = 16;

    public static string DefaultFileName => $"WgSwitch-{DateTime.Now:yyyy-MM-dd}{Extension}";

    /// <summary>Собрать и зашифровать копию. Читает конфиги только ради этого и держит их лишь в памяти.</summary>
    public static Task ExportAsync(SettingsService settings, string path, string password) => Task.Run(() =>
    {
        var payload = new BackupPayload { Created = DateTime.Now, Selected = settings.SelectedName };
        foreach (var s in settings.Current.Servers)
        {
            var conf = s.ConfigPath.Trim().Trim('"');
            if (!File.Exists(conf)) throw new InvalidOperationException($"Нет конфига сервера «{s.Name}»: {Path.GetFileName(conf)}");
            payload.Servers.Add(new BackupServer { Name = s.Name, PingHost = s.PingHost, Config = File.ReadAllText(conf) });
        }

        var plain = JsonSerializer.SerializeToUtf8Bytes(payload, BackupJson.Default.BackupPayload);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = DeriveKey(password, salt);
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plain, cipher, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }

        var tmp = path + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            fs.Write(Magic);
            fs.Write(salt);
            fs.Write(nonce);
            fs.Write(tag);
            fs.Write(cipher);
        }
        File.Move(tmp, path, overwrite: true);
    });

    /// <summary>Расшифровать копию. Неверный пароль и испорченный файл неразличимы — это свойство GCM.</summary>
    public static Task<BackupPayload> ReadAsync(string path, string password) => Task.Run(() =>
    {
        var data = File.ReadAllBytes(path);
        int header = Magic.Length + SaltSize + NonceSize + TagSize;
        if (data.Length <= header || !data.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidOperationException("Это не резервная копия WgSwitch");

        var span = data.AsSpan();
        var salt = span.Slice(Magic.Length, SaltSize);
        var nonce = span.Slice(Magic.Length + SaltSize, NonceSize);
        var tag = span.Slice(Magic.Length + SaltSize + NonceSize, TagSize);
        var cipher = span[header..];

        var key = DeriveKey(password, salt.ToArray());
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return JsonSerializer.Deserialize(plain, BackupJson.Default.BackupPayload)
                   ?? throw new InvalidOperationException("Копия пустая");
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidOperationException("Неверный пароль или файл повреждён");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            CryptographicOperations.ZeroMemory(key);
        }
    });

    /// <summary>
    /// Разложить конфиги в закрытую папку и добавить серверы в список. Серверы с тем же именем заменяются,
    /// остальные остаются. Отличающийся конфиг на диске не затирается, а переименовывается в .bak.
    /// </summary>
    public static async Task<int> RestoreAsync(SettingsService settings, BackupPayload payload)
    {
        var next = settings.Current.Clone();
        await Task.Run(() =>
        {
            ConfigFactory.SecureFolder(SettingsService.ConfigFolder);
            foreach (var s in payload.Servers)
            {
                if (!WireGuardService.IsValidName(s.Name) || string.IsNullOrWhiteSpace(s.Config))
                    throw new InvalidOperationException($"В копии повреждён сервер «{s.Name}»");

                var path = ConfigFactory.PathFor(s.Name);
                if (File.Exists(path) && File.ReadAllText(path) != s.Config)
                    File.Move(path, $"{path}.{DateTime.Now:yyyyMMdd-HHmmss}.bak");
                if (!File.Exists(path))
                    File.WriteAllText(path, s.Config, new UTF8Encoding(false));

                next.Servers.RemoveAll(x => string.Equals(x.Name, s.Name, StringComparison.OrdinalIgnoreCase));
                next.Servers.Add(new ServerEntry { ConfigPath = path, PingHost = s.PingHost });
            }
        });

        if (payload.Selected is { } sel && next.Servers.Any(x => string.Equals(x.Name, sel, StringComparison.OrdinalIgnoreCase)))
            next.Selected = sel;
        await settings.SaveAsync(next);
        return payload.Servers.Count;
    }

    private static byte[] DeriveKey(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
}
