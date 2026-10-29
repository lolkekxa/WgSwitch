using System.ComponentModel;
using System.Diagnostics;
using System.Net.NetworkInformation;
using Microsoft.Win32;
using System.Text.RegularExpressions;
using WgSwitch.Models;

namespace WgSwitch.Services;

/// <summary>
/// Управление туннелями через официальный wireguard.exe и опрос их состояния. Туннелей может быть несколько
/// (по одному на сервер), включёнными одновременно — если у серверов разные подсети.
/// Опрос адаптивный: окно открыто — 2.5 с, для выбранного сервера ещё handshake/трафик; что-то включено, окно скрыто —
/// 3 с (служба + пинг); всё выключено, окно скрыто — 15 с (только службы, без пинга и без процессов).
/// </summary>
public sealed partial class WireGuardService : IDisposable
{
    public const string InstallUrl = "https://www.wireguard.com/install/";

    private const int DetailedInterval = 2500, ActiveInterval = 3000, IdleInterval = 15000;
    private const int PingTimeoutMs = 1500;
    private static readonly TimeSpan HandshakeGrace = TimeSpan.FromSeconds(12);
    // автопереподключение: сервер молчит дольше этого — перезапускаем туннель, но не чаще раза в ReconnectCooldown
    private static readonly TimeSpan SilentBeforeReconnect = TimeSpan.FromMinutes(2), ReconnectCooldown = TimeSpan.FromMinutes(5);
    // после сна или смены сети ждать 2 минуты незачем: молчит дольше 10 с — перезапускаем сразу (не чаще раза в 30 с)
    private static readonly TimeSpan AfterChangeWindow = TimeSpan.FromSeconds(90), FastSilent = TimeSpan.FromSeconds(10), FastCooldown = TimeSpan.FromSeconds(30);
    private DateTime _networkChangedAt = DateTime.MinValue;
    private string _netSignature = NetSignature();

    private readonly SettingsService _settings;
    private readonly SynchronizationContext _ui;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, Tunnel> _tunnels = new(StringComparer.OrdinalIgnoreCase);

    private volatile bool _detailed;

    /// <summary>Состояние одного туннеля между опросами.</summary>
    private sealed class Tunnel(string name) : IDisposable
    {
        public readonly string Name = name;
        public readonly Ping Ping = new();
        public TunnelStatus Status = TunnelStatus.Initial(name);
        public volatile bool Busy;
        /// <summary>Идёт подключение — его можно отменить вторым нажатием.</summary>
        public CancellationTokenSource? Connecting;
        public string? Error;
        public DateTime? StartedAt;
        public int StartedPid;
        public int PingFails;
        public DateTime? SilentSince;
        public DateTime LastReconnect;

        /// <summary>Движок по конфигу: обновляется при Sync и перед подключением.</summary>
        public EngineInfo Engine = WireGuard;

        public string ServiceName => Engine.ServicePrefix + Name;

        public void Dispose() => Ping.Dispose();
    }

    /// <summary>Вызывается в UI-потоке.</summary>
    public event Action<TunnelStatus>? StatusChanged;

    /// <summary>Сервер молчал слишком долго, туннель перезапускается сам. Вызывается в UI-потоке.</summary>
    public event Action<string>? Reconnecting;

    /// <summary>Успешный пинг сервера (имя туннеля) — для «пульса» в окне. Вызывается в UI-потоке.</summary>
    public event Action<string>? Heartbeat;

    public WireGuardService(SettingsService settings)
    {
        _settings = settings;
        _ui = SynchronizationContext.Current ?? new SynchronizationContext();
        Sync();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) NetworkChanged();
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        // событие приходит и когда поднимается сам туннель; считаем только настоящие сети,
        // иначе каждое переподключение лежащего сервера продлевало бы «быстрый» режим бесконечно
        var sig = NetSignature();
        if (sig == _netSignature) return;
        _netSignature = sig;
        NetworkChanged();
    }

    /// <summary>Адреса физических сетей без адаптеров WireGuard: меняется при смене Wi-Fi, кабеля, мобильного интернета.</summary>
    private static string NetSignature()
    {
        try
        {
            return string.Join(";", NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                            && !n.Description.Contains("WireGuard", StringComparison.OrdinalIgnoreCase)
                            && !n.Description.Contains("AmneziaWG", StringComparison.OrdinalIgnoreCase))
                .SelectMany(n => n.GetIPProperties().UnicastAddresses.Select(a => n.Id + "/" + a.Address))
                .Order());
        }
        catch (NetworkInformationException) { return ""; }
    }

    /// <summary>Проснулись или сменилась сеть: сразу опросить туннели и какое-то время переподключать без долгого ожидания.</summary>
    private void NetworkChanged()
    {
        _networkChangedAt = DateTime.Now;
        Wake();
    }

    public void Start() => _ = LoopAsync();

    /// <summary>Список серверов в настройках поменялся — заводим/убираем туннели.</summary>
    public void Sync()
    {
        lock (_tunnels)
        {
            var names = _settings.Current.Servers.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var gone in _tunnels.Keys.Where(k => !names.Contains(k)).ToList())
            {
                _tunnels[gone].Dispose();
                _tunnels.Remove(gone);
            }
            foreach (var n in names)
            {
                if (!_tunnels.TryGetValue(n, out var t)) _tunnels[n] = t = new Tunnel(n);
                // работающий туннель не перекидываем на другой движок посреди опроса — только выключенный
                if (t.Status.State == TunnelState.Off) t.Engine = EngineFor(_settings.Find(n)?.ConfigPath);
            }
        }
        Wake();
    }

    public TunnelStatus StatusOf(string name)
    {
        lock (_tunnels) return _tunnels.TryGetValue(name, out var t) ? t.Status : TunnelStatus.Initial(name);
    }

    /// <summary>Состояния всех серверов в порядке настроек.</summary>
    public IReadOnlyList<TunnelStatus> All => _settings.Current.Servers.Select(s => StatusOf(s.Name)).ToList();

    /// <summary>Окно видно — опрашиваем чаще и подробнее.</summary>
    public void SetDetailed(bool on)
    {
        if (_detailed == on) return;
        _detailed = on;
        if (on) Wake();
    }

    public void Wake()
    {
        if (_wake.CurrentCount == 0)
            try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    // ---------- движки: обычный WireGuard и AmneziaWG (WireGuard с маскировкой от DPI) ----------

    /// <summary>
    /// Клиент движка: GUI-программа ставит и снимает службы туннелей, CLI читает handshake и делает ключи.
    /// У AmneziaWG для Windows те же команды (/installtunnelservice…), другие только имена.
    /// </summary>
    public sealed record EngineInfo(string Name, string Folder, string Gui, string Cli, string ServicePrefix, string InstallUrl)
    {
        public string? GuiPath => Find(Folder, Gui);
        public string? CliPath => Find(Folder, Cli);
    }

    public static readonly EngineInfo WireGuard = new("WireGuard", "WireGuard", "wireguard.exe", "wg.exe", "WireGuardTunnel$", InstallUrl);
    public static readonly EngineInfo Amnezia = new("AmneziaWG", "AmneziaWG", "amneziawg.exe", "awg.exe", "AmneziaWGTunnel$",
        "https://github.com/amnezia-vpn/amneziawg-windows-client/releases/latest");

    /// <summary>Конфиг с Jc/S1/H1… — AmneziaWG, иначе обычный WireGuard.</summary>
    public static EngineInfo EngineFor(string? configPath) => ConfigFactory.IsAmnezia(configPath) ? Amnezia : WireGuard;

    public EngineInfo EngineOf(string name)
    {
        lock (_tunnels) return _tunnels.TryGetValue(name, out var t) ? t.Engine : EngineFor(_settings.Find(name)?.ConfigPath);
    }

    private static string? Find(string folder, string exe)
    {
        foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var path = Path.Combine(root, folder, exe);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static string? FindWireGuard() => WireGuard.GuiPath;

    private Tunnel Get(string name)
    {
        lock (_tunnels)
        {
            if (!_tunnels.TryGetValue(name, out var t)) _tunnels[name] = t = new Tunnel(name);
            return t;
        }
    }

    // ---------- команды ----------

    /// <summary>Перезапуск службы: так WireGuard перечитывает конфиг и заново ищет сервер (после сна, смены сети).</summary>
    public async Task ReconnectAsync(string name)
    {
        await DisconnectAsync(name).ConfigureAwait(false);
        await ConnectAsync(name).ConfigureAwait(false);
    }

    /// <summary>Нажатие во время подключения отменяет его, а не теряется.</summary>
    public Task ToggleAsync(string name) =>
        Get(name).Connecting is not null || StatusOf(name).State != TunnelState.Off ? DisconnectAsync(name) : ConnectAsync(name);

    public async Task ConnectAsync(string name)
    {
        var t = Get(name);
        if (t.Busy) return;
        t.Busy = true;
        t.Error = null;
        using var cancel = new CancellationTokenSource();
        t.Connecting = cancel;
        var ct = cancel.Token;
        Publish(t, t.Status with { State = TunnelState.Connecting, Error = null, WireGuardMissing = false });

        try
        {
            var path = ValidateConfig(_settings.Find(name)?.ConfigPath);
            t.Engine = EngineFor(path);
            var exe = t.Engine.GuiPath ?? throw new UserError($"{t.Engine.Name} не установлен", missing: true);

            var svc = Native.QueryService(t.ServiceName);
            if (svc is { State: Native.ServiceState.Running })
                return; // уже поднят (например, из официального клиента) — просто подхватываем

            if (svc is not null)
            {
                // служба осталась от прошлого раза, но не работает — переустанавливаем начисто
                await Run(exe, $"/uninstalltunnelservice {name}");
                await WaitServiceAsync(t, gone: true, TimeSpan.FromSeconds(10), ct);
            }

            ct.ThrowIfCancellationRequested();
            var r = await Run(exe, $"/installtunnelservice \"{path}\"");
            if (r.ExitCode != 0 && Native.QueryService(t.ServiceName) is null)
                throw new UserError("WireGuard не принял конфиг: " + Short(r));

            if (!await WaitServiceAsync(t, gone: false, TimeSpan.FromSeconds(15), ct))
                throw new UserError("Служба туннеля не запустилась. Проверьте конфиг");

            t.StartedAt = DateTime.Now;
            t.PingFails = 0;
        }
        catch (OperationCanceledException)
        {
            // отменили вторым нажатием — службу снимет DisconnectAsync, который и отменил
        }
        catch (Exception ex)
        {
            Fail(t, ex);
        }
        finally
        {
            t.Connecting = null;
            t.Busy = false;
            Wake();
        }
    }

    public async Task DisconnectAsync(string name)
    {
        var t = Get(name);
        // идёт подключение — прерываем ожидание и ждём, пока оно отпустит туннель (до пары секунд)
        if (t.Connecting is { } connecting)
        {
            connecting.Cancel();
            for (int i = 0; i < 40 && t.Busy; i++) await Task.Delay(100).ConfigureAwait(false);
        }
        if (t.Busy) return;
        t.Busy = true;
        t.Error = null;
        Publish(t, t.Status with { Error = null });

        try
        {
            if (Native.QueryService(t.ServiceName) is not null)
            {
                var exe = t.Engine.GuiPath ?? throw new UserError($"{t.Engine.Name} не установлен", missing: true);
                var r = await Run(exe, $"/uninstalltunnelservice {name}");
                if (!await WaitServiceAsync(t, gone: true, TimeSpan.FromSeconds(10)))
                    throw new UserError("Не удалось остановить туннель: " + Short(r));
            }
            t.StartedAt = null;
        }
        catch (Exception ex)
        {
            Fail(t, ex);
        }
        finally
        {
            t.Busy = false;
            Wake();
        }
    }

    private static string ValidateConfig(string? raw)
    {
        var path = raw?.Trim().Trim('"') ?? "";
        if (path.Length == 0) throw new UserError("Укажите путь к конфигу в настройках");
        if (!path.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)) throw new UserError("Нужен файл с расширением .conf");
        if (!File.Exists(path)) throw new UserError("Конфиг не найден: " + Path.GetFileName(path));
        if (!IsValidTunnelName(path))
            throw new UserError("Имя файла: латиница, цифры и _=+.- (до 32 символов)");
        return Path.GetFullPath(path);
    }

    [GeneratedRegex(@"^[a-zA-Z0-9_=+.-]{1,32}$")]
    private static partial Regex TunnelNameRx();

    public static bool IsValidName(string name) => TunnelNameRx().IsMatch(name);

    public static bool IsValidTunnelName(string confPath) => IsValidName(Path.GetFileNameWithoutExtension(confPath));

    private void Fail(Tunnel t, Exception ex)
    {
        t.Error = ex switch
        {
            UserError u => u.Message,
            Win32Exception { NativeErrorCode: 5 or 740 } => "Отказано в доступе: запустите от администратора",
            UnauthorizedAccessException => "Отказано в доступе: запустите от администратора",
            TimeoutException te => te.Message,
            _ => ex.Message,
        };
        Publish(t, t.Status with { State = TunnelState.Off, Error = t.Error, WireGuardMissing = ex is UserError { Missing: true } });
    }

    private static Task<Proc.Result> Run(string exe, string args) => Proc.RunAsync(exe, args);

    private static string Short(Proc.Result r)
    {
        var text = string.IsNullOrWhiteSpace(r.Error) ? r.Output : r.Error;
        text = string.IsNullOrWhiteSpace(text) ? $"код {r.ExitCode}" : text.Split('\n')[0].Trim();
        return text.Length > 90 ? text[..90] + "…" : text;
    }

    private static async Task<bool> WaitServiceAsync(Tunnel t, bool gone, TimeSpan timeout, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var s = Native.QueryService(t.ServiceName);
            if (gone ? s is null : s is { State: Native.ServiceState.Running }) return true;
            if (!gone && s is { State: Native.ServiceState.Stopped } && sw.Elapsed > TimeSpan.FromSeconds(3)) return false;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return false;
    }

    // ---------- опрос ----------

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            Tunnel[] snapshot;
            lock (_tunnels) snapshot = [.. _tunnels.Values];

            var selected = _settings.SelectedName;
            try
            {
                await Task.WhenAll(snapshot.Select(t =>
                    PollAsync(t, t.Engine.GuiPath is null, _detailed && string.Equals(t.Name, selected, StringComparison.OrdinalIgnoreCase)))).ConfigureAwait(false);
            }
            catch { /* опрос не должен ронять приложение; следующая итерация попробует снова */ }

            bool anyUp = snapshot.Any(t => t.Status.State != TunnelState.Off);
            int delay = _detailed ? DetailedInterval : anyUp ? ActiveInterval : IdleInterval;
            try { await _wake.WaitAsync(delay, _stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PollAsync(Tunnel t, bool missing, bool detailed)
    {
        // во время подключения/отключения состояние ведёт сама команда
        if (t.Busy) return;

        var svc = missing ? null : Native.QueryService(t.ServiceName);
        if (svc is null || svc.Value.State is Native.ServiceState.Stopped or Native.ServiceState.StopPending)
        {
            t.PingFails = 0;
            t.StartedAt = null;
            t.StartedPid = 0;
            Publish(t, new TunnelStatus(TunnelState.Off, t.Name, Error: t.Error ?? (missing ? $"{t.Engine.Name} не установлен" : null), WireGuardMissing: missing));
            return;
        }

        if (svc.Value.State != Native.ServiceState.Running)
        {
            Publish(t, new TunnelStatus(TunnelState.Connecting, t.Name));
            return;
        }

        var since = ServiceStartTime(t, svc.Value.ProcessId);
        long? rtt = await PingAsync(t).ConfigureAwait(false);

        TunnelState state;
        if (rtt is not null)
        {
            t.PingFails = 0;
            t.Error = null;
            state = TunnelState.Connected;
            _ui.Post(_ => Heartbeat?.Invoke(t.Name), null);
        }
        else
        {
            t.PingFails++;
            bool fresh = since is not null && DateTime.Now - since < HandshakeGrace;
            state = fresh ? TunnelState.Connecting
                : t.PingFails >= 2 || t.Status.State != TunnelState.Connected ? TunnelState.NoResponse
                : TunnelState.Connected; // один потерянный пакет — ещё не повод краснеть
        }

        if (state == TunnelState.NoResponse)
        {
            t.SilentSince ??= DateTime.Now;
            var now = DateTime.Now;
            bool afterChange = now - _networkChangedAt < AfterChangeWindow;
            var wait = afterChange ? FastSilent : SilentBeforeReconnect;
            var cooldown = afterChange ? FastCooldown : ReconnectCooldown;
            if (_settings.Current.AutoReconnect && now - t.SilentSince > wait && now - t.LastReconnect > cooldown)
            {
                t.LastReconnect = now;
                t.SilentSince = null;
                _ui.Post(_ => Reconnecting?.Invoke(t.Name), null);
                _ = ReconnectAsync(t.Name);
            }
        }
        else t.SilentSince = null;

        DateTime? handshake = null;
        long? rx = null, tx = null;
        // handshake и трафик — два запуска wg.exe; нужны, только если их показывают на главной
        if (detailed && _settings.Current.ShowDetails)
            (handshake, rx, tx) = await ReadWgStatsAsync(t).ConfigureAwait(false);

        Publish(t, new TunnelStatus(state, t.Name, rtt, since, handshake, rx, tx, t.Error));
    }

    private static DateTime? ServiceStartTime(Tunnel t, int pid)
    {
        if (pid == t.StartedPid && t.StartedAt is not null) return t.StartedAt;
        try
        {
            using var p = Process.GetProcessById(pid);
            t.StartedAt = p.StartTime;
        }
        catch
        {
            t.StartedAt ??= DateTime.Now;
        }
        t.StartedPid = pid;
        return t.StartedAt;
    }

    private async Task<long?> PingAsync(Tunnel t)
    {
        var host = _settings.Find(t.Name)?.PingHost?.Trim();
        if (string.IsNullOrEmpty(host)) return null;
        try
        {
            var reply = await t.Ping.SendPingAsync(host, PingTimeoutMs).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
        }
        catch (PingException) { return null; }
        catch (InvalidOperationException) { return null; } // в т.ч. ObjectDisposedException: сервер удалили посреди опроса
    }

    /// <summary>
    /// Handshake и трафик через «wg show &lt;tunnel&gt; latest-handshakes / transfer».
    /// В отличие от «dump», эти команды не выводят приватный ключ интерфейса.
    /// </summary>
    private static async Task<(DateTime?, long?, long?)> ReadWgStatsAsync(Tunnel t)
    {
        var exe = t.Engine.CliPath;
        if (exe is null) return (null, null, null);
        var tunnel = t.Name;

        try
        {
            var hs = await Proc.RunAsync(exe, $"show {tunnel} latest-handshakes", 3000).ConfigureAwait(false);
            var tr = await Proc.RunAsync(exe, $"show {tunnel} transfer", 3000).ConfigureAwait(false);

            DateTime? handshake = null;
            foreach (var cols in Rows(hs))
                if (cols.Length >= 2 && long.TryParse(cols[1], out var unix) && unix > 0)
                {
                    var time = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
                    if (handshake is null || time > handshake) handshake = time;
                }

            long? rx = null, tx = null;
            foreach (var cols in Rows(tr))
                if (cols.Length >= 3 && long.TryParse(cols[1], out var r) && long.TryParse(cols[2], out var s))
                {
                    rx = (rx ?? 0) + r;
                    tx = (tx ?? 0) + s;
                }

            return (handshake, rx, tx);
        }
        catch
        {
            return (null, null, null);
        }

        static IEnumerable<string[]> Rows(Proc.Result r) =>
            r.ExitCode != 0 ? [] : r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Split('\t'));
    }

    private void Publish(Tunnel t, TunnelStatus next)
    {
        if (next == t.Status) return;
        t.Status = next;
        _ui.Post(_ => StatusChanged?.Invoke(next), null);
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        _stop.Cancel();
        lock (_tunnels)
            foreach (var t in _tunnels.Values) t.Dispose();
    }

    private sealed class UserError(string message, bool missing = false) : Exception(message)
    {
        public bool Missing { get; } = missing;
    }
}
