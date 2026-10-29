using System.Globalization;

namespace WgSwitch.Services;

/// <summary>Состояние сервера: аптайм, нагрузка, память и диск — то, из-за чего сервер «тихо» умирает.</summary>
public sealed record ServerHealth(TimeSpan Uptime, double Load, int Cpus, long MemUsed, long MemTotal, long DiskUsed, long DiskTotal)
{
    public double MemShare => MemTotal > 0 ? (double)MemUsed / MemTotal : 0;
    public double DiskShare => DiskTotal > 0 ? (double)DiskUsed / DiskTotal : 0;
}

/// <summary>
/// Читает состояние по SSH вашим ключом: одна команда только для чтения (/proc и df), без пароля —
/// BatchMode не даёт ssh зависнуть на запросе пароля. Ключи и вывод никуда не сохраняются.
/// </summary>
public static class ServerHealthProbe
{
    // только чтение; вывод — строки ключ=значение, чтобы не зависеть от локали и формата утилит
    internal const string Command =
        "cat /proc/uptime | awk '{print \"up=\" $1}'; " +
        "awk '{print \"load=\" $1}' /proc/loadavg; " +
        "echo cpus=$(nproc); " +
        "awk '/^MemTotal:/{t=$2} /^MemAvailable:/{a=$2} END{print \"mem=\" (t-a)*1024 \"/\" t*1024}' /proc/meminfo; " +
        "df -P -B1 / | awk 'NR==2{print \"disk=\" $3 \"/\" $2}'";

    public static string? FindSsh()
    {
        var system = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
        return File.Exists(system) ? system : null;
    }

    public static async Task<(ServerHealth? Health, string? Error)> ReadAsync(string user, string host, int port)
    {
        var ssh = FindSsh();
        if (ssh is null) return (null, "нет ssh.exe — включите «Клиент OpenSSH» в компонентах Windows");
        try
        {
            var args = $"-o BatchMode=yes -o ConnectTimeout=5 -o StrictHostKeyChecking=accept-new -p {port} {user}@{host} \"{Command.Replace("\"", "\\\"")}\"";
            var r = await Proc.RunAsync(ssh, args, 12_000).ConfigureAwait(false);
            if (r.ExitCode == 0 && Parse(r.Output) is { } h) return (h, null);
            var err = r.Error;
            if (err.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
                return (null, "нужен вход по SSH-ключу: ssh-copy-id " + user + "@" + host);
            if (err.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase))
                return (null, "у сервера сменился ключ SSH — проверьте known_hosts");
            if (err.Contains("timed out", StringComparison.OrdinalIgnoreCase) || err.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
                return (null, "SSH не отвечает");
            return (null, "не удалось прочитать состояние");
        }
        catch (TimeoutException)
        {
            return (null, "SSH не отвечает");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return (null, "не удалось запустить ssh");
        }
    }

    internal static ServerHealth? Parse(string output)
    {
        var kv = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split('=', 2)).Where(p => p.Length == 2)
            .GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.First()[1]);
        if (!kv.TryGetValue("up", out var up) || !double.TryParse(up, NumberStyles.Float, CultureInfo.InvariantCulture, out var upSec)) return null;
        double.TryParse(kv.GetValueOrDefault("load"), NumberStyles.Float, CultureInfo.InvariantCulture, out var load);
        int.TryParse(kv.GetValueOrDefault("cpus"), out var cpus);
        var (memUsed, memTotal) = Pair(kv.GetValueOrDefault("mem"));
        var (diskUsed, diskTotal) = Pair(kv.GetValueOrDefault("disk"));
        return new ServerHealth(TimeSpan.FromSeconds(upSec), load, Math.Max(cpus, 1), memUsed, memTotal, diskUsed, diskTotal);

        static (long, long) Pair(string? s)
        {
            var p = s?.Split('/');
            return p is { Length: 2 } && long.TryParse(p[0], out var a) && long.TryParse(p[1], out var b) ? (a, b) : (0, 0);
        }
    }
}
