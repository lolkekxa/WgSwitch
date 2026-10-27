using System.Net;
using System.Net.Sockets;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace WgSwitch.Services;

/// <summary>
/// Мастер первого запуска: новый wg0.conf со своей парой ключей.
/// Ключи генерирует wg.exe; приватный живёт только в памяти до записи в файл и больше нигде не появляется.
/// </summary>
public static class ConfigFactory
{
    public const int DefaultPort = 51820;

    public sealed record Request(string Name, string Endpoint, string ServerPublicKey, IPAddress ClientIp, bool FullTunnel);

    /// <param name="PeerBlock">Что добавить в конфиг сервера: публичный ключ клиента и его адрес.</param>
    /// <param name="ServerCidr">Адрес сервера в туннеле для его [Interface], например 10.8.0.1/24.</param>
    public sealed record Result(string Name, string Path, string ServerIp, string ServerCidr, string PeerBlock);

    public static string PathFor(string name) => System.IO.Path.Combine(SettingsService.ConfigFolder, name + ".conf");

    /// <summary>Первое свободное имя туннеля: wg0, wg1, …</summary>
    public static string SuggestName(IEnumerable<string> taken)
    {
        var set = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        int i = 0;
        while (set.Contains($"wg{i}")) i++;
        return $"wg{i}";
    }

    /// <summary>
    /// Свой IP в подсети, которую ещё не занял другой сервер: 10.8.0.2, затем 10.9.0.2, …
    /// Одинаковые подсети у двух включённых туннелей конфликтуют, поэтому каждому серверу — своя.
    /// </summary>
    public static string SuggestClientIp(IEnumerable<string> serverIps)
    {
        var taken = serverIps.Select(h => h.Trim()).ToHashSet();
        int n = 8;
        while (taken.Contains($"10.{n}.0.1") && n < 254) n++;
        return $"10.{n}.0.2";
    }

    public static (Request? Request, string? Error) Validate(string name, string endpoint, string serverKey, string clientIp, bool fullTunnel,
        IEnumerable<string> takenNames, IEnumerable<string> takenServerIps)
    {
        name = name.Trim();
        if (!WireGuardService.IsValidName(name)) return (null, "Имя: латиница, цифры и _=+.- (до 32 символов)");
        if (takenNames.Contains(name, StringComparer.OrdinalIgnoreCase)) return (null, $"Сервер «{name}» уже есть, выберите другое имя");

        endpoint = endpoint.Trim();
        serverKey = serverKey.Trim();
        clientIp = clientIp.Trim();

        if (endpoint.Length == 0) return (null, "Укажите адрес сервера");
        if (endpoint.Any(char.IsWhiteSpace) || endpoint.Contains('/')) return (null, "Адрес сервера: IP или домен, без http://");
        if (endpoint.StartsWith('['))
        {
            // [ipv6]:port
            int close = endpoint.IndexOf(']');
            if (close < 0) return (null, "Неверный IPv6-адрес сервера");
            if (close == endpoint.Length - 1) endpoint += $":{DefaultPort}";
            else if (!ValidPort(endpoint[(close + 1)..].TrimStart(':'))) return (null, "Порт: число от 1 до 65535");
        }
        else
        {
            var parts = endpoint.Split(':');
            if (parts.Length == 1) endpoint += $":{DefaultPort}";
            else if (parts.Length != 2 || parts[0].Length == 0 || !ValidPort(parts[1])) return (null, "Адрес вида 1.2.3.4 или 1.2.3.4:51820");
        }

        var buf = new byte[32];
        if (serverKey.Length != 44 || !Convert.TryFromBase64String(serverKey, buf, out int n) || n != 32)
            return (null, "Ключ сервера: 44 символа base64, кончается на «=»");

        if (!IPAddress.TryParse(clientIp, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork)
            return (null, "Ваш IP в VPN: например 10.8.0.2");
        var b = ip.GetAddressBytes();
        if (b[3] is 0 or 255) return (null, "Последнее число IP — от 2 до 254");
        if (b[3] == 1) return (null, ".1 обычно у сервера, возьмите .2");
        if (takenServerIps.Contains($"{b[0]}.{b[1]}.{b[2]}.1"))
            return (null, $"Подсеть {b[0]}.{b[1]}.{b[2]}.x уже у другого сервера, возьмите {SuggestClientIp(takenServerIps)}");

        return (new Request(name, endpoint, serverKey, ip, fullTunnel), null);

        static bool ValidPort(string s) => int.TryParse(s, out int p) && p is > 0 and < 65536;
    }

    public static async Task<Result> CreateAsync(Request r)
    {
        var wgui = WireGuardService.FindWireGuard() ?? throw new InvalidOperationException("WireGuard не установлен");
        var wg = Path.Combine(Path.GetDirectoryName(wgui)!, "wg.exe");
        if (!File.Exists(wg)) throw new InvalidOperationException("Не найден wg.exe в папке WireGuard");

        var gen = await Proc.RunAsync(wg, "genkey", 5000).ConfigureAwait(false);
        var privateKey = gen.Output.Trim();
        if (gen.ExitCode != 0 || privateKey.Length != 44) throw new InvalidOperationException("wg.exe не смог создать ключ");

        var pub = await Proc.RunAsync(wg, "pubkey", 5000, stdin: privateKey + "\n").ConfigureAwait(false);
        var publicKey = pub.Output.Trim();
        if (pub.ExitCode != 0 || publicKey.Length != 44) throw new InvalidOperationException("wg.exe не смог вычислить публичный ключ");

        var b = r.ClientIp.GetAddressBytes();
        var serverIp = $"{b[0]}.{b[1]}.{b[2]}.1";
        var subnet = $"{b[0]}.{b[1]}.{b[2]}.0/24";

        var conf = new StringBuilder()
            .AppendLine("[Interface]")
            .AppendLine($"PrivateKey = {privateKey}")
            .AppendLine($"Address = {r.ClientIp}/32");
        if (r.FullTunnel) conf.AppendLine("DNS = 1.1.1.1, 1.0.0.1");
        conf.AppendLine()
            .AppendLine("[Peer]")
            .AppendLine($"PublicKey = {r.ServerPublicKey}")
            .AppendLine($"AllowedIPs = {(r.FullTunnel ? "0.0.0.0/0, ::/0" : subnet)}")
            .AppendLine($"Endpoint = {r.Endpoint}")
            .AppendLine("PersistentKeepalive = 25");

        SecureFolder(SettingsService.ConfigFolder);
        var path = PathFor(r.Name);

        // старый конфиг не затираем молча: в нём приватный ключ, который ещё может быть нужен
        if (File.Exists(path))
            File.Move(path, $"{path}.{DateTime.Now:yyyyMMdd-HHmmss}.bak");

        await using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        await using (var w = new StreamWriter(fs, new UTF8Encoding(false)))
            await w.WriteAsync(conf.ToString()).ConfigureAwait(false);

        var peer = $"[Peer]\nPublicKey = {publicKey}\nAllowedIPs = {r.ClientIp}/32";
        return new Result(r.Name, path, serverIp, $"{serverIp}/24", peer);
    }

    /// <summary>Папка с конфигом: только Администраторы и SYSTEM (служба WireGuard работает от SYSTEM).</summary>
    internal static void SecureFolder(string dir)
    {
        var sec = new DirectorySecurity();
        sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.BuiltinAdministratorsSid, WellKnownSidType.LocalSystemSid })
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));

        Directory.CreateDirectory(dir).SetAccessControl(sec);
    }
}
