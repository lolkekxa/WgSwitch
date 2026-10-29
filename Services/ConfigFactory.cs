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

    /// <summary>Пара ключей через wg.exe. Приватный — только в памяти вызывающего.</summary>
    private static async Task<(string Private, string Public)> GenerateKeysAsync()
    {
        // ключи у обоих движков одинаковые: подойдёт wg.exe или awg.exe, что найдётся
        var wg = WireGuardService.WireGuard.CliPath ?? WireGuardService.Amnezia.CliPath
                 ?? throw new InvalidOperationException(WireGuardService.FindWireGuard() is null ? "WireGuard не установлен" : "Не найден wg.exe в папке WireGuard");

        var gen = await Proc.RunAsync(wg, "genkey", 5000).ConfigureAwait(false);
        var privateKey = gen.Output.Trim();
        if (gen.ExitCode != 0 || privateKey.Length != 44) throw new InvalidOperationException("wg.exe не смог создать ключ");

        var pub = await Proc.RunAsync(wg, "pubkey", 5000, stdin: privateKey + "\n").ConfigureAwait(false);
        var publicKey = pub.Output.Trim();
        if (pub.ExitCode != 0 || publicKey.Length != 44) throw new InvalidOperationException("wg.exe не смог вычислить публичный ключ");
        return (privateKey, publicKey);
    }

    public static async Task<Result> CreateAsync(Request r)
    {
        var (privateKey, publicKey) = await GenerateKeysAsync().ConfigureAwait(false);

        var b = r.ClientIp.GetAddressBytes();
        var serverIp = $"{b[0]}.{b[1]}.{b[2]}.1";
        var subnet = $"{b[0]}.{b[1]}.{b[2]}.0/24";

        var conf = new StringBuilder()
            .AppendLine("[Interface]")
            .AppendLine($"PrivateKey = {privateKey}")
            .AppendLine($"Address = {r.ClientIp}/32");
        if (r.FullTunnel) conf.AppendLine(FullDns);
        conf.AppendLine()
            .AppendLine("[Peer]")
            .AppendLine($"PublicKey = {r.ServerPublicKey}")
            .AppendLine($"AllowedIPs = {(r.FullTunnel ? FullRoutes : subnet)}")
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

    // ---------- конфиг для телефона: свой ключ, остальное — как у этого ПК ----------

    /// <param name="Config">Конфиг для QR. Нигде не сохраняется: живёт, пока открыт экран.</param>
    /// <param name="PeerBlock">Что дописать на сервер, чтобы он пустил телефон.</param>
    public sealed record PhoneResult(string Config, string PeerBlock);

    /// <summary>Адрес ПК в туннеле (IPv4 из Address) — от него предлагается адрес телефона.</summary>
    public static IPAddress? ClientAddress(string configPath)
    {
        try
        {
            return InterfaceLines(File.ReadAllLines(configPath.Trim().Trim('"')))
                .Where(kv => kv.Key == "address")
                .SelectMany(kv => kv.Value.Split(','))
                .Select(a => IPAddress.TryParse(a.Trim().Split('/')[0], out var ip) ? ip : null)
                .FirstOrDefault(ip => ip?.AddressFamily == AddressFamily.InterNetwork);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Следующий адрес после ПК: 10.8.0.2 → 10.8.0.3. Свободен ли он на сервере — знает только сервер.</summary>
    public static string SuggestPhoneIp(string configPath)
    {
        var ip = ClientAddress(configPath);
        if (ip is null) return "";
        var b = ip.GetAddressBytes();
        b[3] = (byte)(b[3] >= 254 ? 2 : b[3] + 1);
        return new IPAddress(b).ToString();
    }

    /// <summary>
    /// Конфиг телефона из конфига этого ПК: новый ключ и адрес, а DNS, MTU и все [Peer] — те же.
    /// Приватный ключ ПК не копируется. Первая строка — комментарий для WgSwitch на телефоне
    /// (имя и IP для проверки); официальный WireGuard его пропускает.
    /// </summary>
    public static async Task<PhoneResult> CreatePhoneAsync(string configPath, string name, string pingHost, IPAddress phoneIp)
    {
        var (iface, peers, prefix, psk) = PhoneParts(File.ReadAllLines(configPath.Trim().Trim('"')));
        var pcIp = ClientAddress(configPath);
        if (pcIp is not null && pcIp.Equals(phoneIp)) throw new InvalidOperationException("Это адрес самого ПК — у телефона должен быть свой");
        if (peers.Count == 0) throw new InvalidOperationException("В конфиге нет [Peer] — не с чего делать конфиг телефона");

        var (privateKey, publicKey) = await GenerateKeysAsync().ConfigureAwait(false);
        return BuildPhone(name, pingHost, phoneIp, privateKey, publicKey, iface, peers, prefix, psk);
    }

    internal static (List<string> Iface, List<string> Peers, string Prefix, string? Psk) PhoneParts(IEnumerable<string> lines)
    {
        string prefix = "32";
        string? psk = null;
        var iface = new List<string>();
        var peers = new List<string>();
        string section = "";
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith('['))
            {
                section = t.ToLowerInvariant();
                if (section == "[peer]") peers.Add("[Peer]");
                continue;
            }
            if (t.Length == 0 || t.StartsWith('#')) continue;
            var (key, value) = Parse(line);
            if (section == "[interface]")
            {
                if (key == "address")
                {
                    var v4 = value.Split(',').Select(a => a.Trim()).FirstOrDefault(a => a.Contains('.'));
                    if (v4 is not null && v4.Contains('/')) prefix = v4.Split('/')[1];
                }
                else if (key is "dns" or "mtu" || AmneziaKeys.Contains(key)) iface.Add(t);
                // PrivateKey ПК, ListenPort, PreUp/PostUp и прочее — не переносим
            }
            else if (section == "[peer]")
            {
                if (key == "presharedkey") psk = value;
                peers.Add(t);
            }
        }
        return (iface, peers, prefix, psk);
    }

    internal static PhoneResult BuildPhone(string name, string pingHost, IPAddress phoneIp, string privateKey, string publicKey,
        List<string> iface, List<string> peers, string prefix, string? psk)
    {
        var conf = new StringBuilder()
            .Append($"# WgSwitch name={name} ping={pingHost}\n")
            .Append("[Interface]\n")
            .Append($"PrivateKey = {privateKey}\n")
            .Append($"Address = {phoneIp}/{prefix}\n");
        foreach (var l in iface) conf.Append(l).Append('\n');
        foreach (var l in peers)
        {
            if (l == "[Peer]") conf.Append('\n');
            conf.Append(l).Append('\n');
        }

        var peer = $"[Peer]\n# {name} · телефон\nPublicKey = {publicKey}\n"
                   + (psk is null ? "" : $"PresharedKey = {psk}\n")
                   + $"AllowedIPs = {phoneIp}/32";
        return new PhoneResult(conf.ToString(), peer);
    }

    private static IEnumerable<(string Key, string Value)> InterfaceLines(IEnumerable<string> lines)
    {
        string section = "";
        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.StartsWith('[')) { section = t.ToLowerInvariant(); continue; }
            if (section == "[interface]") yield return Parse(line);
        }
    }

    // ---------- весь трафик через VPN: переключение в готовом конфиге ----------

    private const string FullRoutes = "0.0.0.0/0, ::/0";
    private const string FullDns = "DNS = 1.1.1.1, 1.0.0.1";

    /// <summary>Идёт ли весь трафик через туннель. null — конфиг не прочитать. Смотрим только на AllowedIPs.</summary>
    public static bool? IsFullTunnel(string configPath)
    {
        try
        {
            return File.ReadLines(configPath.Trim().Trim('"'))
                .Select(Parse)
                .Any(kv => kv.Key == "allowedips" && kv.Value.Split(',').Any(v => v.Trim() == "0.0.0.0/0"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Переключить режим в конфиге: AllowedIPs у [Peer] — «весь трафик» или подсеть сервера (/24 из Address),
    /// плюс наш DNS для полного режима. Остальные строки (ключи, Endpoint, свой DNS) не трогаем.
    /// </summary>
    public static void SetFullTunnel(string configPath, bool full)
    {
        var path = configPath.Trim().Trim('"');
        var lines = File.ReadAllLines(path).ToList();

        string? subnet = null;
        int addressAt = -1, ourDnsAt = -1;
        bool hasDns = false;
        string section = "";
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith('[')) { section = t.ToLowerInvariant(); continue; }
            var (key, value) = Parse(lines[i]);
            if (section != "[interface]") continue;
            if (key == "address")
            {
                addressAt = i;
                var v4 = value.Split(',').Select(a => a.Trim().Split('/')[0])
                    .FirstOrDefault(a => IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork);
                if (v4 is not null)
                {
                    var b = IPAddress.Parse(v4).GetAddressBytes();
                    subnet = $"{b[0]}.{b[1]}.{b[2]}.0/24";
                }
            }
            else if (key == "dns")
            {
                hasDns = true;
                if (Normalize(lines[i]) == Normalize(FullDns)) ourDnsAt = i;
            }
        }
        if (!full && subnet is null) throw new InvalidOperationException("В конфиге нет IPv4-адреса в Address");

        section = "";
        bool changed = false;
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith('[')) { section = t.ToLowerInvariant(); continue; }
            if (section == "[peer]" && Parse(lines[i]).Key == "allowedips")
            {
                lines[i] = $"AllowedIPs = {(full ? FullRoutes : subnet)}";
                changed = true;
            }
        }
        if (!changed) throw new InvalidOperationException("В конфиге нет AllowedIPs у [Peer]");

        // DNS: в полном режиме без своего DNS резолвер нужен внутри туннеля; при выходе убираем только свой
        if (full && !hasDns && addressAt >= 0) lines.Insert(addressAt + 1, FullDns);
        if (!full && ourDnsAt >= 0) lines.RemoveAt(ourDnsAt);

        var tmp = path + ".tmp";
        File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);

        static string Normalize(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
    }

    /// <summary>Параметры маскировки AmneziaWG в [Interface]: у устройства и сервера они должны совпадать.</summary>
    private static readonly HashSet<string> AmneziaKeys =
        ["jc", "jmin", "jmax", "s1", "s2", "s3", "s4", "h1", "h2", "h3", "h4", "i1", "i2", "i3", "i4", "i5"];

    /// <summary>Конфиг AmneziaWG: в [Interface] есть параметры маскировки. Ключи не читаются — только имена строк.</summary>
    public static bool IsAmnezia(string? configPath)
    {
        try
        {
            var path = configPath?.Trim().Trim('"');
            return !string.IsNullOrEmpty(path) && File.Exists(path) && IsAmneziaText(File.ReadLines(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    internal static bool IsAmneziaText(IEnumerable<string> lines) =>
        InterfaceLines(lines).Any(kv => AmneziaKeys.Contains(kv.Key));

    private static (string Key, string Value) Parse(string line)
    {
        int eq = line.IndexOf('=');
        return eq < 0 ? ("", "") : (line[..eq].Trim().ToLowerInvariant(), line[(eq + 1)..].Trim());
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
