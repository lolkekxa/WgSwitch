using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using WgSwitch.Models;
using WgSwitch.Services;

namespace WgSwitch;

public partial class MainWindow : Window
{
    private static readonly Color Green = Rgb(0x22C55E), Red = Rgb(0xEF4444), Amber = Rgb(0xF59E0B),
        Muted = Rgb(0x7A7A7A), Faint = Rgb(0x3A3A3A), Fg = Rgb(0xEDEDED);

    private static readonly IEasingFunction Expo = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };

    private readonly WireGuardService _wg;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _clock;

    private TunnelStatus _status;
    private string? _shownText;
    /// <summary>Серверы, у которых сейчас идёт команда: true — подключение (второе нажатие его отменит), false — отключение.</summary>
    private readonly Dictionary<string, bool> _busy = new(StringComparer.OrdinalIgnoreCase);
    private bool _settingsOpen;
    private bool _onboarding;
    private bool _adding;          // мастер открыт ради ещё одного сервера — его можно отменить
    private string _selected;      // имя туннеля выбранного сервера
    private ConfigFactory.Result? _created;

    public MainWindow(WireGuardService wg, SettingsService settings)
    {
        _wg = wg;
        _settings = settings;
        _selected = settings.SelectedName;
        _status = wg.StatusOf(_selected);
        InitializeComponent();
        WatchSettings();
        VersionText.Text = $"WgSwitch {SettingsService.Version} ↗";
        ApplyDetails();

        // как всплывающие окна трея: правый нижний угол рабочей области (над панелью задач, где бы она ни была)
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - Height - 12;

        // раз в секунду только «В сети» и «Handshake N с назад», и только пока туннель поднят и окно видно
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RenderTimes(), Dispatcher) { IsEnabled = false };

        SourceInitialized += (_, _) => Native.StyleWindow(new WindowInteropHelper(this).Handle);
        Loaded += OnLoaded;
        Closed += OnClosed;
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized) Dispatcher.BeginInvoke(Close);
            else SyncActivity();
        };
        PreviewKeyDown += OnKey;
        // вернулись из браузера после установки WireGuard — статус на шаге 1 обновится сам
        Activated += (_, _) => { if (_onboarding) RefreshWireGuardCheck(); };
        Deactivated += (_, _) => HidePhoneSecrets();
    }

    /// <summary>Серверов ещё нет — вместо пульта показываем мастер первого запуска.</summary>
    public static bool NeedsSetup(SettingsService s) => s.Current.Servers.Count == 0;

    private string PingHost => _settings.Find(_selected)?.PingHost ?? "—";

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Render(_wg.StatusOf(_selected), animate: false);
        RebuildChips();
        _wg.StatusChanged += OnStatus;
        _wg.Heartbeat += OnHeartbeat;
        UpdateService.AvailableChanged += ShowUpdate;
        ShowUpdate(UpdateService.Available);
        SyncActivity();

        if (NeedsSetup(_settings))
            StartOnboarding(MainPanel, instant: true);

        // появление: как rise на сайте — снизу вверх с проявлением
        Animate(Root, OpacityProperty, 1, 450);
        Animate(RootShift, TranslateTransform.YProperty, 0, 700);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _wg.StatusChanged -= OnStatus;
        _wg.Heartbeat -= OnHeartbeat;
        UpdateService.AvailableChanged -= ShowUpdate;
        _wg.SetDetailed(false);
        _clock.Stop();
        foreach (var t in _copied.Values) t.Stop();
        StopSpin();
        StopDotPing();
    }

    private void SyncActivity()
    {
        bool visible = WindowState != WindowState.Minimized;
        _wg.SetDetailed(visible);
        _clock.IsEnabled = visible && _status.State is TunnelState.Connected or TunnelState.NoResponse;
    }

    private void OnStatus(TunnelStatus s)
    {
        RefreshChips();
        if (Same(s.TunnelName, _selected)) Render(s, animate: true);
    }

    // ---------- отрисовка состояния ----------

    private void Render(TunnelStatus s, bool animate)
    {
        var prev = _status.State;
        _status = s;
        int ms = animate ? 1 : 0;

        bool pending = _busy.TryGetValue(s.TunnelName, out var connecting);
        bool disconnecting = pending && !connecting;
        bool spinning = s.State == TunnelState.Connecting || pending;

        var (accent, text) = s.State switch
        {
            TunnelState.Connected => (Green, "Подключено"),
            TunnelState.NoResponse => (Red, "Нет ответа от сервера"),
            TunnelState.Connecting => (Amber, "Подключение"),
            _ => (Muted, "Выключено"),
        };
        if (disconnecting) (accent, text) = (Amber, "Отключение");

        bool filled = s.State is TunnelState.Connected or TunnelState.NoResponse;

        // кнопка: заливка снизу вверх, цвет иконки, свечение
        Animate(Tint, OpacityProperty, filled ? 0.08 : 0, 700 * ms);
        if (filled) AnimateColor(FillBrush, s.State == TunnelState.NoResponse ? Red : Green, 300 * ms);
        AnimateColor(IconBrush, accent, 300 * ms);
        // при отключении кольцо желтеет и стирается — и уже потом из его конца появляется дуга ожидания
        AnimateColor(RingBrush, disconnecting ? Amber : s.State == TunnelState.NoResponse ? Red : Green, 300 * ms);
        bool ring = filled && !disconnecting;
        bool erasing = ms > 0 && !ring && StateRing.Tag is true;
        if (ms == 0 || ring != (StateRing.Tag is true)) DrawRing(ring, animate: ms > 0);
        var glow = s.State == TunnelState.NoResponse ? Red : Green;
        RippleBrush.Color = glow;

        if (spinning && erasing)
        {
            // дуга появляется там, где кольцо досчиталось до нуля: у верхней точки, короткой
            Spinner.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)) { BeginTime = TimeSpan.FromMilliseconds(RingEraseMs - 60) });
            StartSpin(RingEraseMs - 60);
        }
        else
        {
            Animate(Spinner, OpacityProperty, spinning ? 1 : 0, 250 * ms);
            if (spinning) StartSpin(); else StopSpin();
        }

        // точка состояния (и знак в шапке)
        AnimateColor(DotBrush, accent, 300 * ms);
        DotGlow.Color = accent;
        DotGlow.Opacity = accent == Muted ? 0 : 0.9;
        MarkDot.Fill = new SolidColorBrush(accent);
        MarkArch.Stroke = MarkDot.Fill;
        if (spinning) StartDotPing(accent); else StopDotPing();

        if (text != _shownText)
        {
            _shownText = text;
            StateText.Text = text;
            if (animate) Roll(StateText, StateShift);
        }

        if (!IsCopied(SubText)) SubText.Text = $"{s.TunnelName} · {PingHost}";
        RenderMessage(s);
        RenderStats(s);

        if (prev != s.State) SyncActivity();
    }

    private void RenderMessage(TunnelStatus s)
    {
        MessageText.Inlines.Clear();
        if (s.WireGuardMissing)
        {
            MessageText.Foreground = new SolidColorBrush(Muted);
            MessageText.Inlines.Add(new Run("WireGuard для Windows не найден. "));
            var link = new Hyperlink(new Run("Установить ↗")) { Foreground = new SolidColorBrush(Fg), Cursor = Cursors.Hand };
            link.Click += (_, _) => App.OpenUrl(WireGuardService.InstallUrl);
            MessageText.Inlines.Add(link);
        }
        else if (s.Error is { } err)
        {
            MessageText.Foreground = new SolidColorBrush(Red);
            MessageText.Inlines.Add(new Run(err));
        }
        else if (s.State == TunnelState.NoResponse)
        {
            MessageText.Foreground = new SolidColorBrush(Muted);
            MessageText.Inlines.Add(new Run($"Туннель поднят, но {PingHost} не отвечает"));
        }

        Animate(MessageText, OpacityProperty, MessageText.Inlines.Count > 0 ? 1 : 0, 300);
    }

    private void RenderStats(TunnelStatus s)
    {
        bool up = s.State is TunnelState.Connected or TunnelState.NoResponse;

        Set(PingValue, s.PingMs is { } p ? $"{p} мс" : up ? "нет ответа" : "—", s.PingMs is not null);
        if (up && s.PingMs is null && !IsCopied(PingValue)) PingValue.Foreground = new SolidColorBrush(Red);
        Set(TrafficValue, s.RxBytes is { } rx && s.TxBytes is { } tx ? $"↓ {Bytes(rx)}  ↑ {Bytes(tx)}" : "—", s.RxBytes is not null);
        RenderTimes();
    }

    private void RenderTimes()
    {
        var s = _status;
        bool up = s.State is TunnelState.Connected or TunnelState.NoResponse;
        Set(UptimeValue, up && s.ConnectedSince is { } since ? Duration(DateTime.Now - since) : "—", up && s.ConnectedSince is not null);
        Set(HandshakeValue, s.LastHandshake is { } hs ? Ago(DateTime.Now - hs) : "—", s.LastHandshake is not null);
    }

    private void Set(TextBlock tb, string text, bool known)
    {
        if (IsCopied(tb)) return; // пока висит «скопировано», значение не перерисовываем
        tb.Text = text;
        tb.Foreground = new SolidColorBrush(known ? Fg : Faint);
    }

    // ---------- клик по значению — копирование ----------

    private readonly Dictionary<TextBlock, DispatcherTimer> _copied = [];

    private bool IsCopied(TextBlock tb) => _copied.ContainsKey(tb);

    /// <summary>IP из строки под статусом копируется чистым, без имени туннеля; остальное — как на экране.</summary>
    private void CopyValue_MouseUp(object sender, MouseButtonEventArgs e)
    {
        var tb = (TextBlock)sender;
        if (IsCopied(tb)) return;
        var text = tb == SubText ? PingHost : tb.Text;
        if (string.IsNullOrWhiteSpace(text) || text == "—") return;
        try { Clipboard.SetText(text); }
        catch (System.Runtime.InteropServices.COMException) { return; } // буфер занят другим приложением

        tb.Text = "скопировано ✓";
        tb.Foreground = new SolidColorBrush(Green);
        Blink(tb);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _copied.Remove(tb);
            if (tb == SubText)
            {
                SubText.Text = $"{_status.TunnelName} · {PingHost}";
                SubText.Foreground = new SolidColorBrush(Muted);
            }
            else RenderStats(_status);
            Blink(tb);
        };
        _copied[tb] = timer;
        timer.Start();
    }

    /// <summary>Мягкое проявление при смене текста. FillBehavior.Stop — чтобы не перебить затухание при наведении.</summary>
    private static void Blink(UIElement el) =>
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(250)) { FillBehavior = FillBehavior.Stop });

    private static string Duration(TimeSpan t) =>
        t.TotalHours >= 24 ? $"{(int)t.TotalDays} д {t.Hours:00}:{t.Minutes:00}" : $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}";

    private static string Ago(TimeSpan t) =>
        t.TotalSeconds < 5 ? "только что"
        : t.TotalSeconds < 60 ? $"{(int)t.TotalSeconds} с назад"
        : t.TotalMinutes < 60 ? $"{(int)t.TotalMinutes} мин назад"
        : $"{(int)t.TotalHours} ч назад";

    private static string Bytes(long b) =>
        b < 1024 ? $"{b} Б"
        : b < 1024 * 1024 ? $"{b / 1024.0:0.#} КБ"
        : b < 1024L * 1024 * 1024 ? $"{b / 1048576.0:0.#} МБ"
        : $"{b / 1073741824.0:0.##} ГБ";

    // ---------- «пульс»: успешный пинг — тонкое расходящееся кольцо, не чаще раза в 6 с ----------

    private DateTime _lastPulse;

    private void OnHeartbeat(string name)
    {
        if (!Same(name, _selected) || WindowState == WindowState.Minimized || _settingsOpen || _onboarding) return;
        if (DateTime.Now - _lastPulse < TimeSpan.FromSeconds(6)) return;
        Pulse(0);
    }

    private void Pulse(int delayMs)
    {
        _lastPulse = DateTime.Now;
        var begin = TimeSpan.FromMilliseconds(delayMs);
        Ripple.BeginAnimation(OpacityProperty, new DoubleAnimation(0.35, 0, TimeSpan.FromMilliseconds(1400)) { BeginTime = begin });
        var grow = new DoubleAnimation(1, 1.12, TimeSpan.FromMilliseconds(1400)) { EasingFunction = Expo, BeginTime = begin };
        RippleScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        RippleScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    // длина окружности кольца в толщинах штриха: (148 − 2·0,75 − 1,5)·π / 1,5 ≈ 304
    private const double RingLength = 304;
    private const int RingEraseMs = 450;

    // ---------- дуга ожидания: «дышит» на ходу — у верхней точки короткая, к низу вытягивается ----------

    private const double SpinPeriod = 1.4, SpinMinDeg = 18, SpinMaxDeg = 150;
    private System.Diagnostics.Stopwatch? _spin;
    private double _spinDelay;   // с, пока дуга ещё не видна: отсчёт оборота начинается после неё, с верхней точки

    private void StartSpin(int delayMs = 0)
    {
        if (_spin is not null) return;
        _spinDelay = delayMs / 1000.0;
        _spin = System.Diagnostics.Stopwatch.StartNew();
        CompositionTarget.Rendering += SpinTick;
    }

    private void StopSpin()
    {
        if (_spin is null) return;
        CompositionTarget.Rendering -= SpinTick;
        _spin = null;
    }

    /// <summary>
    /// Каждый кадр, только пока идёт подключение: середина дуги обходит круг за SpinPeriod с лёгким разгоном и торможением,
    /// а длина дуги меняется по косинусу — минимум в верхней точке, максимум внизу. Длину штриха WPF не анимирует, поэтому вручную.
    /// </summary>
    private void SpinTick(object? sender, EventArgs e)
    {
        if (_spin is null) return;
        double t = Math.Max(0, _spin.Elapsed.TotalSeconds - _spinDelay) % SpinPeriod / SpinPeriod;
        double eased = t - Math.Sin(2 * Math.PI * t) / (2 * Math.PI) * 0.35;   // плавнее у верхней точки, быстрее внизу
        double span = SpinMinDeg + (SpinMaxDeg - SpinMinDeg) * (1 - Math.Cos(2 * Math.PI * t)) / 2;
        double center = -90 + 360 * eased;                                      // 0° у эллипса — справа, −90° — верх
        Spinner.StrokeDashArray = [span / 360 * RingLength, RingLength];
        SpinnerTurn.Angle = center - span / 2;
    }

    /// <summary>
    /// Подключилось — кольцо дорисовывается от верхней точки по кругу («цепь замкнулась»), кнопка чуть вдыхает,
    /// следом одно кольцо-пульс. Отключилось — кольцо стирается быстрее, чем рисовалось.
    /// </summary>
    private void DrawRing(bool on, bool animate)
    {
        StateRing.Tag = on;
        double to = on ? 0 : RingLength;
        if (!animate)
        {
            StateRing.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, null);
            StateRing.StrokeDashOffset = to;
            return;
        }

        StateRing.BeginAnimation(System.Windows.Shapes.Shape.StrokeDashOffsetProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(on ? 900 : RingEraseMs))
        {
            EasingFunction = on ? Expo : new CubicEase { EasingMode = EasingMode.EaseIn },
        });
        if (!on) return;

        var breathe = new DoubleAnimation(1.025, TimeSpan.FromMilliseconds(200))
        {
            AutoReverse = true, BeginTime = TimeSpan.FromMilliseconds(250), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        PowerScale.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
        PowerScale.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
        Pulse(600);
    }

    private void StartDotPing(Color c)
    {
        DotPingBrush.Color = c;
        if (DotPingScale.HasAnimatedProperties) return;
        var dur = TimeSpan.FromSeconds(1);
        DotPing.BeginAnimation(OpacityProperty, new DoubleAnimation(0.7, 0, dur) { RepeatBehavior = RepeatBehavior.Forever });
        var grow = new DoubleAnimation(1, 2.4, dur) { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        DotPingScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        DotPingScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void StopDotPing()
    {
        DotPing.BeginAnimation(OpacityProperty, null);
        DotPing.Opacity = 0;
        DotPingScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DotPingScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
    }

    // ---------- серверы: капсулы над кнопкой ----------

    private void RebuildChips()
    {
        ServerChips.Children.Clear();
        var servers = _settings.Current.Servers;
        ChipsHost.Visibility = servers.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (servers.Count < 2) return;

        foreach (var server in servers)
        {
            var name = server.Name;
            var dot = new System.Windows.Shapes.Ellipse { Width = 6, Height = 6, Margin = new Thickness(0, 1, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
            var chip = new Button
            {
                Style = (Style)FindResource("Chip"),
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { dot, label } },
                Tag = name,
            };
            chip.Click += (_, _) => Select(name);
            ServerChips.Children.Add(chip);
        }
        RefreshChips();
    }

    /// <summary>Цвет точки — состояние туннеля; выбранная капсула с заливкой.</summary>
    private void RefreshChips()
    {
        foreach (Button chip in ServerChips.Children)
        {
            var name = (string)chip.Tag;
            var state = _wg.StatusOf(name).State;
            bool selected = Same(name, _selected);
            var dot = (System.Windows.Shapes.Ellipse)((StackPanel)chip.Content).Children[0];
            dot.Fill = new SolidColorBrush(state switch
            {
                TunnelState.Connected => Green,
                TunnelState.NoResponse => Red,
                TunnelState.Connecting => Amber,
                _ => Faint,
            });
            chip.Background = new SolidColorBrush(selected ? Rgb(0x111111) : Colors.Transparent);
            chip.BorderBrush = new SolidColorBrush(selected ? Faint : Rgb(0x1F1F1F));
            chip.Foreground = new SolidColorBrush(selected ? Fg : Muted);
        }
    }

    private void Select(string name)
    {
        if (Same(name, _selected)) return; // команда у другого сервера идёт своим чередом — переключаться можно
        _selected = name;

        var next = _settings.Current.Clone();
        next.Selected = name;
        _ = SaveQuietlyAsync(next);

        _shownText = null;
        Render(_wg.StatusOf(name), animate: true);
        RefreshChips();
        SpinStar();
        _wg.Wake(); // handshake и трафик нового сервера — сразу, а не через 2.5 с
    }

    private void SpinStar() => Roll(StateText, StateShift);

    private async Task SaveQuietlyAsync(AppSettings next)
    {
        try { await _settings.SaveAsync(next); }
        catch { /* выбор сервера — не повод показывать ошибку; в худшем случае при запуске откроется прежний */ }
    }

    // ---------- действия ----------

    private async void Power_Click(object sender, RoutedEventArgs e)
    {
        var name = _selected;
        if (_busy.TryGetValue(name, out var connecting))
        {
            // подключение затянулось — второе нажатие отменяет его; само отключение не прерываем
            if (!connecting) return;
            _busy[name] = false;
            Press();
            Render(_status, animate: true);
            await _wg.DisconnectAsync(name);
            return;
        }
        _busy[name] = _wg.StatusOf(name).State == TunnelState.Off;
        Press();
        Render(_status, animate: true);
        try { await _wg.ToggleAsync(name); }
        finally
        {
            _busy.Remove(name);
            if (Same(name, _selected)) Render(_wg.StatusOf(name), animate: true);
        }
    }

    private void Press()
    {
        var a = new DoubleAnimation(0.96, TimeSpan.FromMilliseconds(90)) { AutoReverse = true };
        PowerScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        PowerScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    /// <summary>Свернуть = убрать в трей: свёрнутое окно не висит в панели задач и в Alt+Tab с пустым превью.</summary>
    private void Minimize_Click(object sender, RoutedEventArgs e) => Close();


    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (_backupFrom is not null) BackupBack();
        else if (_phoneOpen) PhoneBack();
        else if (_toolsOpen) ShowTools(false);
        else if (_settingsOpen) ShowSettings(false);
        else if (_onboarding) CancelSetup();
        else return;
        e.Handled = true;
    }

    // ---------- настройки ----------

    private void Gear_Click(object sender, RoutedEventArgs e)
    {
        if (_toolsOpen) CloseToolsInstant();
        ShowSettings(!_settingsOpen);
    }

    private void ShowSettings(bool open)
    {
        _settingsOpen = open;
        if (open)
        {
            var c = _settings.Current;
            var server = _settings.Find(_selected);
            NameBox.Text = server?.Name ?? "";
            ConfigPath = server?.ConfigPath ?? "";
            PingBox.Text = server?.PingHost ?? "";
            AutoStartBox.IsChecked = c.AutoStart;
            MinimizedBox.IsChecked = c.StartMinimized;
            AutoConnectBox.IsChecked = server?.AutoConnect == true;
            NotifyBox.IsChecked = c.Notify;
            ReconnectBox.IsChecked = c.AutoReconnect;
            ExitOffBox.IsChecked = c.DisconnectOnExit;
            DetailsBox.IsChecked = c.ShowDetails;
            UpdatesBox.IsChecked = c.CheckUpdates;
            MinimizedBox.IsEnabled = c.AutoStart;
            ShowFullTunnel(server?.ConfigPath);
            SettingsError.Text = "";
            _settingsSnapshot = SettingsState();
            SaveButton.BeginAnimation(OpacityProperty, null);
            SaveButton.Opacity = 0;
            SaveButton.IsHitTestVisible = false;
            SettingsPanel.Visibility = Visibility.Visible;
            SettingsPanel.IsHitTestVisible = true;
        }

        if (!open) MainPanel.Visibility = Visibility.Visible;
        MainPanel.IsHitTestVisible = !open;
        // уходящая панель лежит поверх главного экрана до конца анимации — клики ловить ей уже нельзя
        SettingsPanel.IsHitTestVisible = open;
        Animate(MainPanel, OpacityProperty, open ? 0 : 1, 300);
        Animate(MainShift, TranslateTransform.XProperty, open ? -24 : 0, 500);
        Animate(SettingsPanel, OpacityProperty, open ? 1 : 0, open ? 400 : 200);
        Animate(SettingsShift, TranslateTransform.XProperty, open ? 0 : 40, 500, done: open ? null : () =>
        {
            if (!_settingsOpen) SettingsPanel.Visibility = Visibility.Collapsed;
        });
        Animate(GearSpin, RotateTransform.AngleProperty, open ? 90 : 0, 700);
        GearButton.Foreground = new SolidColorBrush(open ? Fg : Muted);

        SetTitle(open ? "Настройки" : "WgSwitch");
    }

    // ---------- «Сохранить» появляется, только когда в настройках что-то поменяли ----------

    private string _settingsSnapshot = "";

    private string SettingsState() => string.Join("\u001f",
        NameBox.Text.Trim(), PingBox.Text.Trim(), ConfigPath,
        AutoStartBox.IsChecked, MinimizedBox.IsChecked, AutoConnectBox.IsChecked, FullTunnelSetting.IsChecked,
        NotifyBox.IsChecked, ReconnectBox.IsChecked, ExitOffBox.IsChecked, DetailsBox.IsChecked, UpdatesBox.IsChecked);

    private void WatchSettings()
    {
        foreach (var box in new[] { NameBox, PingBox })
            box.TextChanged += (_, _) => UpdateDirty();
        foreach (var cb in new[] { AutoStartBox, MinimizedBox, AutoConnectBox, FullTunnelSetting, NotifyBox, ReconnectBox, ExitOffBox, DetailsBox, UpdatesBox })
        {
            cb.Checked += (_, _) => UpdateDirty();
            cb.Unchecked += (_, _) => UpdateDirty();
        }
    }

    private void UpdateDirty()
    {
        if (!_settingsOpen) return;
        bool dirty = SettingsState() != _settingsSnapshot;
        if (dirty == SaveButton.IsHitTestVisible) return;
        SaveButton.IsHitTestVisible = dirty;
        Animate(SaveButton, OpacityProperty, dirty ? 1 : 0, dirty ? 300 : 200);
    }

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        MinimizedBox.IsEnabled = AutoStartBox.IsChecked == true;
        if (!MinimizedBox.IsEnabled) MinimizedBox.IsChecked = false;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (PickConfig(ConfigPath) is { } path)
        {
            ConfigPath = path;
            NameBox.Text = SettingsService.TunnelNameOf(path); // другой файл — другое имя, переименовывать его не нужно
            ShowFullTunnel(path);
        }
    }

    private bool? _fullTunnelWas;   // режим в конфиге на момент открытия; null — конфиг не прочитать

    private void ShowFullTunnel(string? configPath)
    {
        _fullTunnelWas = configPath is null ? null : ConfigFactory.IsFullTunnel(configPath);
        FullTunnelSetting.IsChecked = _fullTunnelWas == true;
        FullTunnelSetting.IsEnabled = _fullTunnelWas is not null;
    }



    private string _configPath = "";

    /// <summary>Путь к конфигу выбранного сервера; в строке настроек — только имя файла, полный путь в подсказке.</summary>
    private string ConfigPath
    {
        get => _configPath;
        set
        {
            _configPath = value.Trim().Trim('"');
            ConfigName.Text = _configPath.Length == 0 ? "не выбран" : Path.GetFileName(_configPath);
            ConfigRow.ToolTip = _configPath.Length == 0 ? null : _configPath;
            UpdateDirty();
        }
    }

    private string? PickConfig(string current)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Конфиг WireGuard",
            Filter = "WireGuard (*.conf)|*.conf",
            CheckFileExists = true,
        };
        try
        {
            var dir = Path.GetDirectoryName(current.Trim());
            if (Directory.Exists(dir)) dlg.InitialDirectory = dir;
        }
        catch (ArgumentException) { }

        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var path = ConfigPath;
        var host = PingBox.Text.Trim();

        string? error =
            path.Length == 0 ? "Укажите путь к конфигу"
            : !path.EndsWith(".conf", StringComparison.OrdinalIgnoreCase) ? "Нужен файл .conf"
            : !File.Exists(path) ? "Файл не найден"
            : host.Length == 0 || host.Contains(' ') ? "Неверный IP"
            : null;
        if (error is not null)
        {
            ShowSettingsError(error);
            return;
        }

        // имя туннеля = имя файла: новое имя в поле — значит, файл нужно переименовать
        var name = NameBox.Text.Trim();
        if (!WireGuardService.IsValidName(name))
        {
            ShowSettingsError("Имя: латиница, цифры и _=+.- (до 32)");
            return;
        }
        var target = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, name + ".conf");
        bool rename = !string.Equals(Path.GetFullPath(path), target, StringComparison.OrdinalIgnoreCase);
        if (rename && File.Exists(target) && !Same(SettingsService.TunnelNameOf(path), name))
        {
            ShowSettingsError($"Файл {name}.conf уже есть");
            return;
        }
        if (!Same(name, _selected) && _settings.Find(name) is not null)
        {
            ShowSettingsError($"Сервер «{name}» уже есть");
            return;
        }
        // туннель называется по файлу: сменить файл у включённого туннеля — потерять над ним управление
        if (!Same(name, _selected) && _wg.StatusOf(_selected).State != TunnelState.Off)
        {
            ShowSettingsError("Сначала выключите туннель");
            return;
        }

        // «весь интернет через VPN» живёт в самом конфиге; меняем его до переименования, пока путь прежний
        bool fullChanged = FullTunnelSetting.IsEnabled && _fullTunnelWas is { } was && (FullTunnelSetting.IsChecked == true) != was;
        if (fullChanged)
        {
            try { ConfigFactory.SetFullTunnel(path, FullTunnelSetting.IsChecked == true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                ShowSettingsError("Не удалось изменить конфиг: " + ex.Message);
                return;
            }
            _fullTunnelWas = FullTunnelSetting.IsChecked == true;
        }

        string? renamedFrom = null;
        if (rename)
        {
            try
            {
                File.Move(path, target);
                renamedFrom = path;
                path = target;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowSettingsError("Не удалось переименовать файл: " + ex.Message);
                return;
            }
        }

        var next = _settings.Current.Clone();
        var entry = next.Servers.FirstOrDefault(x => Same(x.Name, _selected));
        if (entry is null) next.Servers.Add(entry = new ServerEntry());
        entry.ConfigPath = path;
        entry.PingHost = host;
        next.Selected = name;
        next.AutoStart = AutoStartBox.IsChecked == true;
        next.StartMinimized = MinimizedBox.IsChecked == true;
        entry.AutoConnect = AutoConnectBox.IsChecked == true;
        next.Notify = NotifyBox.IsChecked == true;
        next.AutoReconnect = ReconnectBox.IsChecked == true;
        next.DisconnectOnExit = ExitOffBox.IsChecked == true;
        next.ShowDetails = DetailsBox.IsChecked == true;
        next.CheckUpdates = UpdatesBox.IsChecked == true;

        try
        {
            await _settings.SaveAsync(next);
        }
        catch (Exception ex)
        {
            // настройки не сохранились — вернём файлу прежнее имя, чтобы путь в них остался верным
            if (renamedFrom is not null)
                try { File.Move(path, renamedFrom); } catch { /* остаётся новое имя; путь поправят через «Обзор» */ }
            ShowSettingsError(ex.Message);
            return;
        }

        _selected = name;
        _wg.Sync();
        RebuildChips();
        ApplyDetails();
        Render(_wg.StatusOf(name), animate: true);
        ShowSettings(false);

        if (fullChanged && _wg.StatusOf(name).State != TunnelState.Off)
            _ = _wg.ReconnectAsync(name);
    }

    private void ShowSettingsError(string text, bool ok = false)
    {
        SettingsError.Text = text;
        SettingsError.Foreground = new SolidColorBrush(ok ? Green : Red);
        SettingsError.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
    }

    // ---------- резервная копия: серверы и конфиги в один зашифрованный файл ----------

    private bool _backupExport;
    private FrameworkElement? _backupFrom;   // откуда пришли: настройки или шаг 1 мастера

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.Current.Servers.Count == 0)
        {
            ShowSettingsError("Нечего сохранять: серверов нет");
            return;
        }
        OpenBackup(export: true, SettingsPanel);
    }

    private void Import_Click(object sender, RoutedEventArgs e) => OpenBackup(export: false, SettingsPanel);

    private void RestoreFromSetup_Click(object sender, RoutedEventArgs e) => OpenBackup(export: false, Step1);

    private void OpenBackup(bool export, FrameworkElement from)
    {
        _backupExport = export;
        _backupFrom = from;
        if (from == SettingsPanel) _settingsOpen = false;
        GearButton.Visibility = Visibility.Collapsed;

        BackupHeading.Text = export ? "Резервная копия" : "Восстановление";
        BackupIntro.Text = export
            ? "Серверы, IP и конфиги с ключами — в один файл, зашифрованный паролем. Его можно хранить в облаке: без пароля файл бесполезен."
            : "Выберите файл .wgsb и введите пароль от него. Серверы с теми же именами заменятся, остальные останутся.";
        BackupHint.Text = export
            ? $"Не короче {BackupService.MinPasswordLength} символов. Запишите пароль отдельно от файла: восстановить его нельзя."
            : "Конфиги лягут в C:\\ProgramData\\WgSwitch, доступ к папке — только у администраторов.";
        BackupRepeatRow.Visibility = export ? Visibility.Visible : Visibility.Collapsed;
        BackupGo.Content = export ? "Сохранить файл" : "Выбрать файл";
        BackupGo.IsEnabled = true;
        BackupPass.Clear();
        BackupPass2.Clear();
        HideError(BackupError);

        Slide(from, BackupPanel, 1);
        SetTitle(export ? "Экспорт" : "Импорт");
        BackupPass.Focus();
    }

    private void BackupBack_Click(object sender, RoutedEventArgs e) => BackupBack();

    private void BackupBack()
    {
        if (_backupFrom is not { } from) return;
        _backupFrom = null;
        BackupPass.Clear();
        BackupPass2.Clear();

        Slide(BackupPanel, from, -1);
        if (from == SettingsPanel)
        {
            _settingsOpen = true;
            GearButton.Visibility = Visibility.Visible;
            SetTitle("Настройки");
        }
        else SetTitle("Настройка · 1/3");
    }

    private async void BackupGo_Click(object sender, RoutedEventArgs e)
    {
        var password = BackupPass.Password;
        if (_backupExport)
        {
            if (password.Length < BackupService.MinPasswordLength)
            {
                ShowError(BackupError, $"Пароль — не короче {BackupService.MinPasswordLength} символов");
                return;
            }
            if (password != BackupPass2.Password)
            {
                ShowError(BackupError, "Пароли не совпадают");
                return;
            }
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Резервная копия WgSwitch",
                Filter = $"Резервная копия WgSwitch (*{BackupService.Extension})|*{BackupService.Extension}",
                FileName = BackupService.DefaultFileName,
            };
            if (dlg.ShowDialog(this) != true) return;

            await RunBackup("Шифрую…", async () =>
            {
                await BackupService.ExportAsync(_settings, dlg.FileName, password);
                BackupBack();
                ShowSettingsError("Копия сохранена: " + Path.GetFileName(dlg.FileName), ok: true);
            });
        }
        else
        {
            if (password.Length == 0)
            {
                ShowError(BackupError, "Введите пароль от файла");
                return;
            }
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Резервная копия WgSwitch",
                Filter = $"Резервная копия WgSwitch (*{BackupService.Extension})|*{BackupService.Extension}|Все файлы|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(this) != true) return;

            await RunBackup("Расшифровываю…", async () =>
            {
                var payload = await BackupService.ReadAsync(dlg.FileName, password);
                await BackupService.RestoreAsync(_settings, payload);
                FinishRestore();
            });
        }
    }

    /// <summary>PBKDF2 на 600 000 итераций занимает долю секунды — кнопка на это время занята.</summary>
    private async Task RunBackup(string busyText, Func<Task> work)
    {
        var text = BackupGo.Content;
        BackupGo.Content = busyText;
        BackupGo.IsEnabled = false;
        HideError(BackupError);
        try { await work(); }
        catch (Exception ex)
        {
            ShowError(BackupError, ex is UnauthorizedAccessException ? "Нет доступа к файлу или папке конфигов" : ex.Message);
        }
        finally
        {
            BackupGo.Content = text;
            BackupGo.IsEnabled = true;
        }
    }

    /// <summary>Восстановили — сразу на главный экран с новыми серверами (и из настроек, и из мастера).</summary>
    private void FinishRestore()
    {
        _backupFrom = null;
        _settingsOpen = false;
        _onboarding = false;
        _adding = false;
        BackupPass.Clear();
        GearButton.Visibility = Visibility.Visible;
        Animate(GearSpin, RotateTransform.AngleProperty, 0, 700);
        GearButton.Foreground = new SolidColorBrush(Muted);

        _wg.Sync();
        _selected = _settings.SelectedName;
        RebuildChips();
        _shownText = null;
        MainPanel.Opacity = 0;
        Render(_wg.StatusOf(_selected), animate: false);
        Slide(BackupPanel, MainPanel, 1);
        SetTitle("WgSwitch");
    }

    // ---------- первый запуск: 1 WireGuard → 2 сервер → 3 ключ на сервер ----------

    private void StartOnboarding(FrameworkElement from, bool instant)
    {
        _onboarding = true;
        _adding = _settings.Current.Servers.Count > 0;
        GearButton.Visibility = Visibility.Collapsed;
        CancelSetupLink.Visibility = _adding ? Visibility.Visible : Visibility.Collapsed;
        Step1Heading.Text = _adding ? "Новый сервер" : "Настроим туннель";
        Step1Intro.Text = _adding
            ? "Ещё один туннель со своим конфигом. Можно держать включёнными оба, если у серверов разные подсети."
            : "Проверим WireGuard, подготовим конфиг и покажем, что добавить на сервер. Займёт минуту.";
        HideError(Step1Error);
        RefreshWireGuardCheck();

        // следующий сервер — следующее свободное имя и своя подсеть, чтобы туннели не конфликтовали
        var servers = _settings.Current.Servers;
        TunnelNameBox.Text = ConfigFactory.SuggestName(servers.Select(x => x.Name));
        ClientIpBox.Text = ConfigFactory.SuggestClientIp(servers.Select(x => x.PingHost));
        EndpointBox.Text = "";
        ServerKeyBox.Text = "";
        FullTunnelBox.IsChecked = false;

        if (instant)
        {
            from.Visibility = Visibility.Collapsed;
            from.IsHitTestVisible = false;
            Step1.Visibility = Visibility.Visible;
            Step1.Opacity = 1;
            TitleText.Text = "Настройка · 1/3";
        }
        else
        {
            Slide(from, Step1, 1);
            SetTitle("Настройка · 1/3");
        }
    }

    private void DisarmRemove()
    {
        _removeArmed?.Stop();
        _removeArmed = null;
        RemoveText.Text = "Удалить";
        RemoveLink.Foreground = new SolidColorBrush(Muted);
    }

    /// <summary>Из настроек: «+ Добавить» — тот же мастер, но с отменой.</summary>
    private void AddServer_Click(object sender, RoutedEventArgs e) => LeaveSettingsTo(() => StartOnboarding(SettingsPanel, instant: false));

    private void LeaveSettingsTo(Action next)
    {
        _settingsOpen = false;
        Animate(GearSpin, RotateTransform.AngleProperty, 0, 700);
        GearButton.Foreground = new SolidColorBrush(Muted);
        MainPanel.Visibility = Visibility.Collapsed;
        next();
    }

    private DispatcherTimer? _removeArmed;

    /// <summary>
    /// Убирает сервер из списка и удаляет его конфиг. В конфиге приватный ключ, вернуть его нельзя —
    /// поэтому первое нажатие только переспрашивает, второе в течение 3 секунд удаляет.
    /// </summary>
    private async void RemoveServer_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.Find(_selected) is not { } server) return;
        if (_wg.StatusOf(_selected).State != TunnelState.Off)
        {
            ShowSettingsError("Сначала выключите туннель");
            return;
        }
        if (_removeArmed is null)
        {
            RemoveText.Text = "Точно? Конфиг удалится";
            RemoveLink.Foreground = new SolidColorBrush(Red);
            _removeArmed = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _removeArmed.Tick += (_, _) => DisarmRemove();
            _removeArmed.Start();
            return;
        }
        DisarmRemove();

        var next = _settings.Current.Clone();
        next.Servers.RemoveAll(x => Same(x.Name, _selected));
        next.Selected = next.Servers.FirstOrDefault()?.Name;
        try { await _settings.SaveAsync(next); }
        catch (Exception ex)
        {
            ShowSettingsError(ex.Message);
            return;
        }

        // файл — только если на него не ссылается другой сервер из списка
        var conf = server.ConfigPath.Trim().Trim('"');
        if (!next.Servers.Any(x => string.Equals(x.ConfigPath.Trim().Trim('"'), conf, StringComparison.OrdinalIgnoreCase)))
        {
            try { File.Delete(conf); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowSettingsError($"Сервер убран, но конфиг не удалился: {ex.Message}");
            }
        }

        _wg.Sync();
        _selected = _settings.SelectedName;
        RebuildChips();

        if (NeedsSetup(_settings))
        {
            LeaveSettingsTo(() => StartOnboarding(SettingsPanel, instant: false));
            return;
        }
        _shownText = null;
        Render(_wg.StatusOf(_selected), animate: true);
        ShowSettings(false);
    }

    private void CancelSetup_Click(object sender, RoutedEventArgs e) => CancelSetup();

    private void CancelSetup()
    {
        if (!_adding) return;
        var from = Step1.IsHitTestVisible ? Step1 : Step2.IsHitTestVisible ? Step2 : null;
        if (from is null) return; // на шаге 3 конфиг уже создан — только «Готово»

        _onboarding = false;
        _adding = false;
        GearButton.Visibility = Visibility.Visible;
        MainPanel.Opacity = 0;
        Slide(from, MainPanel, -1);
        SetTitle("WgSwitch");
    }

    private void RefreshWireGuardCheck()
    {
        bool ok = WireGuardService.FindWireGuard() is not null;
        var c = ok ? Green : Red;
        WgDot.Fill = new SolidColorBrush(c);
        WgDotGlow.Color = c;
        WgStatusText.Text = ok ? "Установлен" : "Не найден";
        InstallButton.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        WgHint.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        CreateNextButton.IsEnabled = ok;
        CreateNextButton.Opacity = ok ? 1 : 0.35;
    }

    private void Install_Click(object sender, RoutedEventArgs e) => App.OpenUrl(WireGuardService.InstallUrl);

    private void Repo_Click(object sender, RoutedEventArgs e) => App.OpenUrl(SettingsService.RepoUrl);

    // ---------- панель сервера: SSH, SFTP, панель и доступность их портов через туннель ----------

    private bool _toolsOpen;
    private DispatcherTimer? _probeTimer;

    private void Tools_Click(object sender, RoutedEventArgs e) => ShowTools(!_toolsOpen);

    private void ShowTools(bool open)
    {
        if (open == _toolsOpen) return;
        _toolsOpen = open;
        if (open)
        {
            var from = MainPanel;
            if (_settingsOpen) CloseSettingsInstant();
            FillTools();
            Slide(from, ToolsPanel, 1);
            SetTitle(_selected);
            ToolsButton.Foreground = new SolidColorBrush(Fg);
            _probeTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, (_, _) => _ = ProbeAsync(), Dispatcher);
            _probeTimer.Start();
            _ = ProbeAsync();
        }
        else
        {
            SaveToolsFields();
            _probeTimer?.Stop();
            MainPanel.Visibility = Visibility.Visible;
            Slide(ToolsPanel, MainPanel, -1);
            SetTitle("WgSwitch");
            ToolsButton.Foreground = new SolidColorBrush(Muted);
        }
    }

    /// <summary>Без анимации: шестерёнка нажата при открытой панели сервера.</summary>
    private void CloseToolsInstant()
    {
        SaveToolsFields();
        _toolsOpen = false;
        _probeTimer?.Stop();
        ToolsPanel.Visibility = Visibility.Collapsed;
        ToolsPanel.IsHitTestVisible = false;
        MainPanel.Visibility = Visibility.Visible;
        MainPanel.BeginAnimation(OpacityProperty, null);
        MainPanel.Opacity = 1;
        MainShift.BeginAnimation(TranslateTransform.XProperty, null);
        MainShift.X = 0;
        ToolsButton.Foreground = new SolidColorBrush(Muted);
    }

    /// <summary>Без анимации: кнопка сервера нажата при открытых настройках.</summary>
    private void CloseSettingsInstant()
    {
        _settingsOpen = false;
        SettingsPanel.Visibility = Visibility.Collapsed;
        SettingsPanel.IsHitTestVisible = false;
        GearSpin.BeginAnimation(RotateTransform.AngleProperty, null);
        GearSpin.Angle = 0;
        GearButton.Foreground = new SolidColorBrush(Muted);
        MainPanel.Visibility = Visibility.Visible;
        MainPanel.BeginAnimation(OpacityProperty, null);
        MainPanel.Opacity = 1;
        MainShift.BeginAnimation(TranslateTransform.XProperty, null);
        MainShift.X = 0;
        MainPanel.IsHitTestVisible = true;
    }

    private void FillTools()
    {
        var s = _settings.Find(_selected);
        SshUserBox.Text = s?.SshUser ?? "root";
        SshPortBox.Text = (s?.SshPort ?? 22).ToString();
        PanelBox.Text = s?.PanelUrl ?? "";
        RenderTools();
    }

    private void RenderTools()
    {
        var s = _settings.Find(_selected);
        var host = s?.PingHost ?? "";
        ToolsHost.Text = host;
        SshTarget.Text = s is null ? "" : $"{s.SshUser}@{host}" + (s.SshPort != 22 ? $":{s.SshPort}" : "");
        SftpRow.Visibility = FileZillaPath() is null ? Visibility.Collapsed : Visibility.Visible;
        var panel = PanelUri(s);
        PanelRow.Visibility = panel is null ? Visibility.Collapsed : Visibility.Visible;
        PanelTarget.Text = panel is null ? "" : panel.Authority;
        bool up = _wg.StatusOf(_selected).State is TunnelState.Connected or TunnelState.NoResponse;
        ToolsHint.Text = up
            ? "Точка — открыт ли порт через туннель, проверка раз в 5 секунд."
            : "Туннель выключен: адреса внутри VPN сейчас недоступны.";
    }

    private static Uri? PanelUri(ServerEntry? s) =>
        string.IsNullOrWhiteSpace(s?.PanelUrl) ? null : Uri.TryCreate(s!.PanelUrl, UriKind.Absolute, out var u) ? u : null;

    /// <summary>Раз в 5 секунд, пока панель открыта: TCP-соединение с портом SSH и панели.</summary>
    private async Task ProbeAsync()
    {
        var s = _settings.Find(_selected);
        bool up = _wg.StatusOf(_selected).State is TunnelState.Connected or TunnelState.NoResponse;
        if (s is null || !up)
        {
            SetDot(SshDot, null); SetDot(SftpDot, null); SetDot(PanelDot, null);
            return;
        }
        var panel = PanelUri(s);
        var sshTask = NetProbe.CanConnectAsync(s.PingHost, s.SshPort);
        var panelTask = panel is null ? Task.FromResult<bool?>(null)
            : NetProbe.CanConnectAsync(panel.Host, panel.Port).ContinueWith(t => (bool?)t.Result);
        bool ssh = await sshTask;
        bool? web = await panelTask;
        if (!_toolsOpen) return;
        SetDot(SshDot, ssh); SetDot(SftpDot, ssh); SetDot(PanelDot, web);
    }

    private static void SetDot(System.Windows.Shapes.Ellipse dot, bool? ok) =>
        dot.Fill = new SolidColorBrush(ok is null ? Faint : ok.Value ? Green : Red);

    private void ToolsField_LostFocus(object sender, KeyboardFocusChangedEventArgs e) => SaveToolsFields();

    /// <summary>Поля панели сервера сохраняются сами: при уходе с поля и при закрытии панели.</summary>
    private void SaveToolsFields()
    {
        var s = _settings.Find(_selected);
        if (s is null) return;
        var user = SshUserBox.Text.Trim();
        int port = int.TryParse(SshPortBox.Text.Trim(), out var p) && p is > 0 and < 65536 ? p : 22;
        var panel = PanelBox.Text.Trim();
        if (panel.Length > 0 && !panel.Contains("://")) panel = "http://" + panel;
        string? panelUrl = panel.Length == 0 ? null : panel;
        user = user.Length == 0 ? "root" : user;
        if (user == s.SshUser && port == s.SshPort && panelUrl == s.PanelUrl) return;

        var next = _settings.Current.Clone();
        var entry = next.Servers.First(x => Same(x.Name, _selected));
        entry.SshUser = user;
        entry.SshPort = port;
        entry.PanelUrl = panelUrl;
        _ = SaveQuietlyAsync(next);
        // SaveQuietlyAsync подменит Current после записи; для строк панели берём новые значения сразу
        s.SshUser = user; s.SshPort = port; s.PanelUrl = panelUrl;
        SshPortBox.Text = port.ToString();
        PanelBox.Text = panelUrl ?? "";
        RenderTools();
        _ = ProbeAsync();
    }

    private static string? FileZillaPath()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            var exe = Path.Combine(root, "FileZilla FTP Client", "filezilla.exe");
            if (File.Exists(exe)) return exe;
        }
        return null;
    }

    /// <summary>Терминал с ssh: Windows Terminal, если есть, иначе обычная консоль.</summary>
    // ---------- телефон: конфиг со своим ключом и QR-код; ничего не пишется на диск ----------

    private bool _phoneOpen;
    private string? _phonePeer;

    private void Phone_Click(object sender, RoutedEventArgs e)
    {
        SaveToolsFields();
        if (_settings.Find(_selected) is not { } s) return;
        ClearPhone();
        PhoneIpBox.Text = ConfigFactory.SuggestPhoneIp(s.ConfigPath);
        _phoneOpen = true;
        _probeTimer?.Stop();
        GearButton.Visibility = Visibility.Collapsed; // кнопка сервера привязана к ней и спрячется тоже
        Slide(ToolsPanel, PhonePanel, 1);
        SetTitle("Телефон");
    }

    private void PhoneBack_Click(object sender, RoutedEventArgs e) => PhoneBack();

    private void PhoneBack()
    {
        if (!_phoneOpen) return;
        _phoneOpen = false;
        ClearPhone();
        GearButton.Visibility = Visibility.Visible;
        Slide(PhonePanel, ToolsPanel, -1);
        SetTitle(_selected);
        _probeTimer?.Start();
        _ = ProbeAsync();
    }

    // ---------- размытие: QR и блок показываются только по кнопке и прячутся, когда окно теряет фокус ----------

    private bool _revealed;

    private void Reveal_Click(object sender, RoutedEventArgs e) => SetRevealed(true);

    private void QrCard_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_revealed) SetRevealed(false);
    }

    private void SetRevealed(bool on, bool animate = true)
    {
        _revealed = on;
        int ms = animate ? 350 : 0;
        // эффект с нулевым радиусом всё равно пересэмплирует картинку и мылит модули — после показа снимаем его совсем
        if (!on)
        {
            QrCard.Effect = QrBlur;
            PhonePeerText.Effect = PeerBlur;
        }
        Animate(QrBlur, BlurEffect.RadiusProperty, on ? 0 : 22, ms, done: () => { if (_revealed) QrCard.Effect = null; });
        Animate(PeerBlur, BlurEffect.RadiusProperty, on ? 0 : 8, ms, done: () => { if (_revealed) PhonePeerText.Effect = null; });
        Animate(RevealButton, OpacityProperty, on ? 0 : 1, animate ? 250 : 0);
        RevealButton.IsHitTestVisible = !on;
        QrCard.Cursor = on ? Cursors.Hand : Cursors.Arrow;
    }

    /// <summary>Ушли из окна (демонстрация экрана, другое приложение) — данные снова под размытием.</summary>
    private void HidePhoneSecrets()
    {
        if (_phoneOpen && _revealed) SetRevealed(false);
    }

    /// <summary>QR с приватным ключом телефона не должен висеть в памяти дольше, чем открыт экран.</summary>
    private void ClearPhone()
    {
        SetRevealed(false, animate: false);
        PhoneQr.Source = null;
        PhonePeerText.Text = "";
        _phonePeer = null;
        PhoneSetup.Visibility = Visibility.Visible;
        PhoneResult.Visibility = Visibility.Collapsed;
        PhoneGo.Content = "Создать QR";
        PhoneGo.IsEnabled = true;
        PhoneCopy.Content = "Скопировать";
        HideError(PhoneError);
    }

    private async void PhoneGo_Click(object sender, RoutedEventArgs e)
    {
        if (_phonePeer is not null)
        {
            PhoneBack();
            return;
        }
        if (_settings.Find(_selected) is not { } s) return;
        if (!System.Net.IPAddress.TryParse(PhoneIpBox.Text.Trim(), out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            ShowPhoneError("Адрес телефона: IPv4, например 10.8.0.3");
            return;
        }

        HideError(PhoneError);
        PhoneGo.IsEnabled = false;
        PhoneGo.Content = "Создаю…";
        try
        {
            var r = await ConfigFactory.CreatePhoneAsync(s.ConfigPath, s.Name, s.PingHost, ip);
            if (!_phoneOpen) return;
            ShowQr(QrCode.Encode(r.Config));
            _phonePeer = r.PeerBlock;
            PhonePeerText.Text = r.PeerBlock;
            PhoneSetup.Visibility = Visibility.Collapsed;
            PhoneResult.Visibility = Visibility.Visible;
            PhoneResult.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(400)));
            PhoneGo.Content = "Готово";
        }
        catch (Exception ex)
        {
            ShowPhoneError(ex.Message);
            PhoneGo.Content = "Создать QR";
        }
        finally
        {
            PhoneGo.IsEnabled = true;
        }
    }

    private void ShowPhoneError(string text)
    {
        PhoneError.Text = text;
        PhoneError.Visibility = Visibility.Visible;
    }

    private void PhoneCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_phonePeer is null) return;
        try
        {
            Clipboard.SetText(_phonePeer.Replace("\n", Environment.NewLine));
            PhoneCopy.Content = "Скопировано ✓";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            PhoneCopy.Content = "Буфер занят, ещё раз";
        }
    }

    /// <summary>
    /// QR — растр «пиксель на модуль», увеличенный в целое число раз экранных пикселей без сглаживания:
    /// все модули строго одинаковые, иначе при дробном масштабе камера читает код хуже.
    /// </summary>
    private void ShowQr(QrCode qr)
    {
        int n = qr.Size;
        var pixels = new byte[n * n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                pixels[y * n + x] = qr[x, y] ? (byte)0x0A : (byte)0xFF;
        var bitmap = BitmapSource.Create(n, n, 96, 96, PixelFormats.Gray8, null, pixels, n);
        bitmap.Freeze();

        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        int k = Math.Max(2, (int)(216 * dpi / n));
        PhoneQr.Width = PhoneQr.Height = k * n / dpi;
        PhoneQr.Source = bitmap;
    }

    private void Ssh_Click(object sender, RoutedEventArgs e)
    {
        SaveToolsFields();
        if (_settings.Find(_selected) is not { } s) return;
        var args = $"{s.SshUser}@{s.PingHost}" + (s.SshPort != 22 ? $" -p {s.SshPort}" : "");
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("wt.exe", $"ssh {args}") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/k ssh {args}") { UseShellExecute = true });
        }
    }

    private void Sftp_Click(object sender, RoutedEventArgs e)
    {
        SaveToolsFields();
        if (_settings.Find(_selected) is not { } s || FileZillaPath() is not { } fz) return;
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fz, $"sftp://{s.SshUser}@{s.PingHost}:{s.SshPort}") { UseShellExecute = false });
    }

    private void Panel_Click(object sender, RoutedEventArgs e)
    {
        SaveToolsFields();
        if (PanelUri(_settings.Find(_selected)) is { } u) App.OpenUrl(u.ToString());
    }

    // ---------- обновления ----------

    private bool _updating;

    private void ShowUpdate(UpdateInfo? update)
    {
        if (_updating) return;
        UpdateLink.Visibility = update is null ? Visibility.Collapsed : Visibility.Visible;
        if (update is null) return;
        UpdateText.Text = $"Вышла версия {update.Version} · " + (UpdateService.IsInstalled && update.SetupUrl is not null ? "Обновить ›" : "Скачать ›");
        UpdateLink.ToolTip = UpdateService.IsInstalled ? "Скачает установщик, сверит SHA-256 и обновит программу" : update.PageUrl;
    }

    /// <summary>Установленная копия: скачать, сверить, поставить поверх и перезапуститься. Иначе — страница релиза.</summary>
    private async void UpdateLink_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateService.Available is not { } update || _updating) return;
        if (!UpdateService.IsInstalled || update.SetupUrl is null)
        {
            App.OpenUrl(update.PageUrl);
            return;
        }

        _updating = true;
        UpdateLink.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(p => UpdateText.Text = $"Загружаю {update.Version} · {p:P0}");
            var setup = await UpdateService.DownloadSetupAsync(update, progress);
            UpdateText.Text = "Устанавливаю, программа перезапустится";
            ((App)Application.Current).ExitForUpdate(setup);
        }
        catch (Exception ex)
        {
            _updating = false;
            UpdateLink.IsEnabled = true;
            UpdateText.Text = "Не получилось: " + ex.Message + " · ещё раз ›";
        }
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = false;
        CheckUpdatesButton.Content = "Проверяю…";
        try
        {
            var update = await UpdateService.CheckAsync();
            ShowSettingsError(update is null ? $"Установлена последняя версия {SettingsService.Version}" : $"Вышла версия {update.Version} — ссылка на главной", ok: true);
        }
        catch (Exception ex)
        {
            ShowSettingsError("Не удалось проверить: " + (ex is TaskCanceledException ? "нет ответа от GitHub" : ex.Message));
        }
        finally
        {
            CheckUpdatesButton.Content = "Проверить обновления";
            CheckUpdatesButton.IsEnabled = true;
        }
    }

    /// <summary>Handshake и трафик на главной — только если включено «Подробно на главной».</summary>
    private void ApplyDetails() =>
        DetailRows.Visibility = _settings.Current.ShowDetails ? Visibility.Visible : Visibility.Collapsed;

    private void MadeBy_Click(object sender, RoutedEventArgs e) => App.OpenUrl("https://lkxa.ru/?ref=wgswitch");

    private void CreateNext_Click(object sender, RoutedEventArgs e)
    {
        HideError(Step2Error);
        Slide(Step1, Step2, 1);
        SetTitle("Настройка · 2/3");
        EndpointBox.Focus();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        Slide(Step2, Step1, -1);
        SetTitle("Настройка · 1/3");
    }

    private async void HaveConf_Click(object sender, RoutedEventArgs e)
    {
        if (PickConfig(_settings.SelectedServer?.ConfigPath ?? SettingsService.ConfigFolder) is not { } path) return;
        if (!WireGuardService.IsValidTunnelName(path))
        {
            ShowError(Step1Error, "Имя файла: латиница, цифры и _=+.- (до 32 символов)");
            return;
        }
        if (_settings.Find(SettingsService.TunnelNameOf(path)) is not null)
        {
            ShowError(Step1Error, $"Сервер «{SettingsService.TunnelNameOf(path)}» уже есть. Переименуйте файл");
            return;
        }
        // конфиг не читаем, поэтому адрес сервера в туннеле угадываем; поправить можно в настройках
        await FinishAsync(Step1, path, "10.8.0.1", Step1Error);
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        var servers = _settings.Current.Servers;
        var (req, error) = ConfigFactory.Validate(TunnelNameBox.Text, EndpointBox.Text, ServerKeyBox.Text, ClientIpBox.Text, FullTunnelBox.IsChecked == true,
            servers.Select(x => x.Name), servers.Select(x => x.PingHost));
        if (req is null)
        {
            ShowError(Step2Error, error!);
            return;
        }

        HideError(Step2Error);
        CreateButton.IsEnabled = false;
        try
        {
            _created = await ConfigFactory.CreateAsync(req);
        }
        catch (Exception ex)
        {
            ShowError(Step2Error, ex is UnauthorizedAccessException ? "Нет доступа к папке конфига: запустите от администратора" : ex.Message);
            return;
        }
        finally
        {
            CreateButton.IsEnabled = true;
        }

        PeerText.Text = _created.PeerBlock;
        ServerCidrText.Text = $"Адрес сервера в туннеле, для его [Interface]: Address = {_created.ServerCidr}";
        SavedPathText.Text = "Сохранено: " + _created.Path;
        CopyButton.Content = "Скопировать";
        HideError(Step3Error);
        Slide(Step2, Step3, 1);
        SetTitle("Настройка · 3/3");
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_created is null) return;
        try
        {
            Clipboard.SetText(_created.PeerBlock.Replace("\n", Environment.NewLine));
            CopyButton.Content = "Скопировано ✓";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            CopyButton.Content = "Буфер занят, ещё раз";
        }
    }

    private async void Done_Click(object sender, RoutedEventArgs e)
    {
        if (_created is not null)
            await FinishAsync(Step3, _created.Path, _created.ServerIp, Step3Error);
    }

    private async Task FinishAsync(FrameworkElement from, string configPath, string pingHost, TextBlock errorText)
    {
        var name = SettingsService.TunnelNameOf(configPath);
        var next = _settings.Current.Clone();
        next.Servers.RemoveAll(x => Same(x.Name, name));
        next.Servers.Add(new ServerEntry { ConfigPath = configPath, PingHost = pingHost });
        next.Selected = name;
        try
        {
            await _settings.SaveAsync(next);
        }
        catch (Exception ex)
        {
            ShowError(errorText, ex.Message);
            return;
        }

        _onboarding = false;
        _adding = false;
        _created = null;
        GearButton.Visibility = Visibility.Visible;
        MainPanel.Opacity = 0;
        _selected = name;
        _wg.Sync();
        RebuildChips();
        _shownText = null;
        Render(_wg.StatusOf(name), animate: false);
        Slide(from, MainPanel, 1);
        SetTitle("WgSwitch");
    }

    private static void ShowError(TextBlock tb, string text)
    {
        tb.Text = text;
        tb.Visibility = Visibility.Visible;
        tb.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
    }

    private static void HideError(TextBlock tb) => tb.Visibility = Visibility.Collapsed;

    // ---------- анимации ----------

    /// <summary>Смена экрана: старый уезжает влево и гаснет, новый въезжает справа (dir = -1 — наоборот).</summary>
    private static void Slide(FrameworkElement from, FrameworkElement to, int dir)
    {
        var fromShift = (TranslateTransform)from.RenderTransform;
        var toShift = (TranslateTransform)to.RenderTransform;

        from.IsHitTestVisible = false;
        Animate(from, OpacityProperty, 0, 250, done: () =>
        {
            if (!from.IsHitTestVisible) from.Visibility = Visibility.Collapsed;
        });
        Animate(fromShift, TranslateTransform.XProperty, -24 * dir, 500);

        to.Visibility = Visibility.Visible;
        to.IsHitTestVisible = true;
        toShift.BeginAnimation(TranslateTransform.XProperty, null);
        toShift.X = 40 * dir;
        Animate(to, OpacityProperty, 1, 400);
        Animate(toShift, TranslateTransform.XProperty, 0, 500);
    }

    private void SetTitle(string text)
    {
        if (TitleText.Text == text) return;
        TitleText.Text = text;
        Roll(TitleText, TitleShift);
    }

    /// <summary>Как RollText на сайте: новый текст въезжает снизу с проявлением.</summary>
    private static void Roll(UIElement el, TranslateTransform shift)
    {
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(350)));
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(600)) { EasingFunction = Expo });
    }

    private static void Animate(IAnimatable target, DependencyProperty prop, double to, int ms, Action? done = null)
    {
        if (ms <= 0)
        {
            target.BeginAnimation(prop, null);
            ((DependencyObject)target).SetValue(prop, to);
            done?.Invoke();
            return;
        }
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = Expo };
        if (done is not null) a.Completed += (_, _) => done();
        target.BeginAnimation(prop, a);
    }

    private static void AnimateColor(SolidColorBrush brush, Color to, int ms)
    {
        if (ms <= 0)
        {
            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = to;
            return;
        }
        brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(to, TimeSpan.FromMilliseconds(ms)));
    }

    private static Color Rgb(int hex) => Color.FromRgb((byte)(hex >> 16), (byte)(hex >> 8), (byte)hex);
}
