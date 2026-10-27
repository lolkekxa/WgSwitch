using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using WgSwitch.Models;
using WgSwitch.Services;

namespace WgSwitch;

public partial class MainWindow : Window
{
    private static readonly Color Green = Rgb(0x22C55E), Red = Rgb(0xEF4444), Amber = Rgb(0xF59E0B),
        Muted = Rgb(0x7A7A7A), Faint = Rgb(0x3A3A3A), Fg = Rgb(0xEDEDED), Bg = Rgb(0x0A0A0A);

    private static readonly IEasingFunction Expo = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 6 };

    private readonly WireGuardService _wg;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _clock;

    private TunnelStatus _status;
    private string? _shownText;
    private bool _pending;
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

        // как всплывающие окна трея: правый нижний угол рабочей области (над панелью задач, где бы она ни была)
        var area = SystemParameters.WorkArea;
        Left = area.Right - Width - 12;
        Top = area.Bottom - Height - 12;

        // раз в секунду только «В сети» и «Handshake N с назад», и только пока туннель поднят и окно видно
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RenderTimes(), Dispatcher) { IsEnabled = false };

        SourceInitialized += (_, _) => Native.StyleWindow(new WindowInteropHelper(this).Handle);
        Loaded += OnLoaded;
        Closed += OnClosed;
        StateChanged += (_, _) => SyncActivity();
        PreviewKeyDown += OnKey;
        // вернулись из браузера после установки WireGuard — статус на шаге 1 обновится сам
        Activated += (_, _) => { if (_onboarding) RefreshWireGuardCheck(); };
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
        _wg.SetDetailed(false);
        _clock.Stop();
        SpinnerTurn.BeginAnimation(RotateTransform.AngleProperty, null);
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

        bool disconnecting = _pending && s.State is TunnelState.Connected or TunnelState.NoResponse;
        bool spinning = s.State == TunnelState.Connecting || _pending;

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
        Animate(FillShift, TranslateTransform.YProperty, filled ? 0 : 148, 700 * ms);
        if (filled) AnimateColor(FillBrush, s.State == TunnelState.NoResponse ? Red : Green, 300 * ms);
        AnimateColor(IconBrush, filled ? Bg : accent, 300 * ms);
        Animate(Glow, OpacityProperty, s.State == TunnelState.Connected ? 1 : s.State == TunnelState.NoResponse ? 0.6 : 0, 900 * ms);
        var glow = s.State == TunnelState.NoResponse ? Red : Green;
        GlowStop.Color = Color.FromArgb(0x40, glow.R, glow.G, glow.B);
        GlowEdge.Color = Color.FromArgb(0x00, glow.R, glow.G, glow.B);
        RippleBrush.Color = glow;

        Animate(Spinner, OpacityProperty, spinning ? 1 : 0, 250 * ms);
        if (spinning && !SpinnerTurn.HasAnimatedProperties)
            SpinnerTurn.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
        else if (!spinning)
            SpinnerTurn.BeginAnimation(RotateTransform.AngleProperty, null);

        // точка состояния (и знак в шапке)
        AnimateColor(DotBrush, accent, 300 * ms);
        DotGlow.Color = accent;
        DotGlow.Opacity = accent == Muted ? 0 : 0.9;
        MarkDot.Fill = new SolidColorBrush(accent);
        if (spinning) StartDotPing(accent); else StopDotPing();

        if (text != _shownText)
        {
            _shownText = text;
            StateText.Text = text;
            if (animate) Roll(StateText, StateShift);
        }

        SubText.Text = $"{s.TunnelName} · {PingHost}";
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

        Set(ServerValue, PingHost, true);
        Set(PingValue, s.PingMs is { } p ? $"{p} мс" : up ? "нет ответа" : "—", s.PingMs is not null);
        if (up && s.PingMs is null) PingValue.Foreground = new SolidColorBrush(Red);
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

    private static void Set(TextBlock tb, string text, bool known)
    {
        tb.Text = text;
        tb.Foreground = new SolidColorBrush(known ? Fg : Faint);
    }

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

    // ---------- «пульс»: на каждый успешный пинг — расходящееся кольцо ----------

    private void OnHeartbeat(string name)
    {
        if (!Same(name, _selected) || WindowState == WindowState.Minimized || _settingsOpen || _onboarding) return;
        Ripple.BeginAnimation(OpacityProperty, new DoubleAnimation(0.5, 0, TimeSpan.FromMilliseconds(1100)));
        var grow = new DoubleAnimation(1, 1.2, TimeSpan.FromMilliseconds(1100)) { EasingFunction = Expo };
        RippleScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        RippleScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
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
        if (Same(name, _selected) || _pending) return;
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
        if (_pending) return;
        _pending = true;
        var name = _selected;
        Press();
        Render(_status, animate: true);
        try { await _wg.ToggleAsync(name); }
        finally
        {
            _pending = false;
            if (Same(name, _selected)) Render(_wg.StatusOf(name), animate: true);
        }
    }

    private void Press()
    {
        var a = new DoubleAnimation(0.96, TimeSpan.FromMilliseconds(90)) { AutoReverse = true };
        PowerScale.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        PowerScale.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    /// <summary>Крестик не завершает приложение: окно уничтожается, остаётся иконка в трее.</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (_backupFrom is not null) BackupBack();
        else if (_settingsOpen) ShowSettings(false);
        else if (_onboarding) CancelSetup();
        else return;
        e.Handled = true;
    }

    // ---------- настройки ----------

    private void Gear_Click(object sender, RoutedEventArgs e) => ShowSettings(!_settingsOpen);

    private void ShowSettings(bool open)
    {
        _settingsOpen = open;
        if (open)
        {
            var c = _settings.Current;
            var server = _settings.Find(_selected);
            NameBox.Text = server?.Name ?? "";
            ConfigBox.Text = server?.ConfigPath ?? "";
            PingBox.Text = server?.PingHost ?? "";
            AutoStartBox.IsChecked = c.AutoStart;
            MinimizedBox.IsChecked = c.StartMinimized;
            AutoConnectBox.IsChecked = c.AutoConnect;
            MinimizedBox.IsEnabled = c.AutoStart;
            SettingsError.Text = "";
            SettingsPanel.Visibility = Visibility.Visible;
            SettingsPanel.IsHitTestVisible = true;
        }

        MainPanel.IsHitTestVisible = !open;
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

    private void AutoStart_Changed(object sender, RoutedEventArgs e)
    {
        MinimizedBox.IsEnabled = AutoStartBox.IsChecked == true;
        if (!MinimizedBox.IsEnabled) MinimizedBox.IsChecked = false;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        if (PickConfig(ConfigBox.Text) is { } path)
        {
            ConfigBox.Text = path;
            NameBox.Text = SettingsService.TunnelNameOf(path); // другой файл — другое имя, переименовывать его не нужно
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
        var path = ConfigBox.Text.Trim().Trim('"');
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
        next.AutoConnect = AutoConnectBox.IsChecked == true;

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
        Render(_wg.StatusOf(name), animate: true);
        ShowSettings(false);
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

    /// <summary>Убирает сервер из списка. Файл конфига не трогаем: он может быть нужен.</summary>
    private async void RemoveServer_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.Find(_selected) is null) return;
        if (_wg.StatusOf(_selected).State != TunnelState.Off)
        {
            ShowSettingsError("Сначала выключите туннель");
            return;
        }

        var next = _settings.Current.Clone();
        next.Servers.RemoveAll(x => Same(x.Name, _selected));
        next.Selected = next.Servers.FirstOrDefault()?.Name;
        try { await _settings.SaveAsync(next); }
        catch (Exception ex)
        {
            ShowSettingsError(ex.Message);
            return;
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
