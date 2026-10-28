using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using WgSwitch.Models;
using WgSwitch.Services;

namespace WgSwitch;

public partial class App : Application
{
    private const string MutexName = @"Local\WgSwitch.Instance";
    private const string ShowEventName = @"Local\WgSwitch.Show";

    private Mutex? _mutex;
    private EventWaitHandle? _showSignal;
    private RegisteredWaitHandle? _showWait;

    private SettingsService _settings = null!;
    private WireGuardService _wg = null!;
    private TrayService _tray = null!;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _mutex = new Mutex(true, MutexName, out bool first);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!first)
        {
            // вторая копия: просим первую показать окно и уходим
            Native.AllowSetForegroundWindow(-1 /* ASFW_ANY */);
            _showSignal.Set();
            Shutdown();
            return;
        }
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showSignal,
            (_, _) => Dispatcher.BeginInvoke(ShowMain), null, Timeout.Infinite, executeOnlyOnce: false);

        _settings = new SettingsService();
        _settings.Load();
        _wg = new WireGuardService(_settings);
        _tray = new TrayService(_wg, _settings, ShowMain, () => _ = ExitAppAsync());
        _wg.Start();

        bool autostart = e.Args.Contains("--autostart", StringComparer.OrdinalIgnoreCase);
        // без серверов прятаться в трей бессмысленно: сразу показываем мастер первого запуска
        bool needsSetup = WgSwitch.MainWindow.NeedsSetup(_settings);
        if (needsSetup || !(autostart && _settings.Current.StartMinimized))
            ShowMain();
        else
            ScheduleTrim();

        if (!needsSetup && _settings.Current.Servers.Any(s => s.AutoConnect))
            _ = AutoConnectAsync();

        // обновления: через несколько секунд после запуска (не мешать старту) и раз в 12 часов
        var updates = new DispatcherTimer { Interval = TimeSpan.FromHours(12) };
        updates.Tick += (_, _) => _ = CheckUpdatesAsync();
        updates.Start();
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ => Dispatcher.BeginInvoke(() => _ = CheckUpdatesAsync()));
    }

    private async Task CheckUpdatesAsync()
    {
        if (!_settings.Current.CheckUpdates) return;
        try { await UpdateService.CheckAsync(); }
        catch { /* нет сети или GitHub недоступен — проверим в следующий раз */ }
    }

    /// <summary>Обновление установленной копии: запустить установщик после выхода. Туннели при этом не трогаем.</summary>
    public void ExitForUpdate(string setupPath)
    {
        UpdateService.RunInstallerAfterExit(setupPath);
        _ = ExitAppAsync(keepTunnels: true);
    }

    private async Task AutoConnectAsync()
    {
        // дать первому опросу увидеть уже поднятый туннель, чтобы не переустанавливать его
        await Task.Delay(400);
        foreach (var server in _settings.Current.Servers.Where(s => s.AutoConnect))
            if (_wg.StatusOf(server.Name).State == TunnelState.Off)
                await _wg.ConnectAsync(server.Name);
    }

    public void ShowMain()
    {
        if (_window is null)
        {
            _window = new MainWindow(_wg, _settings);
            _window.Closed += (_, _) =>
            {
                _window = null;
                ScheduleTrim();
            };
            _window.Show();
        }

        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(_window).Handle);
    }

    /// <summary>Окно закрыто — в трее остаются только иконка и таймер опроса, остальное отдаём системе.</summary>
    private void ScheduleTrim()
    {
        var t = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(1.5) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (_window is null) Native.TrimWorkingSet();
        };
        t.Start();
    }

    private bool _exiting;

    private async Task ExitAppAsync(bool keepTunnels = false)
    {
        if (_exiting) return;
        _exiting = true;
        _window?.Close();

        // по умолчанию туннели не трогаем: служба WireGuard живёт сама по себе
        if (_settings.Current.DisconnectOnExit && !keepTunnels)
        {
            var up = _wg.All.Where(s => s.State != TunnelState.Off).Select(s => _wg.DisconnectAsync(s.TunnelName));
            await Task.WhenAny(Task.WhenAll(up), Task.Delay(TimeSpan.FromSeconds(15)));
        }

        _tray.Dispose();
        _wg.Dispose();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showWait?.Unregister(null);
        _showSignal?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Через explorer.exe — браузер откроется в обычной сессии пользователя, а не с правами администратора.</summary>
    public static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{url}\"") { UseShellExecute = false });
}
