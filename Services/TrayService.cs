using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using WgSwitch.Models;

namespace WgSwitch.Services;

/// <summary>Иконка в трее: фирменный знак (тёмный квадрат с кругом), круг окрашен по состоянию.</summary>
public sealed class TrayService : IDisposable
{
    private static readonly Color Ink = Color.FromArgb(0x0A, 0x0A, 0x0A);
    private static readonly Color Paper = Color.FromArgb(0xED, 0xED, 0xED);
    private static readonly Color Line = Color.FromArgb(0x1F, 0x1F, 0x1F);
    private static readonly Color Muted = Color.FromArgb(0x7A, 0x7A, 0x7A);
    private static readonly Color Hover = Color.FromArgb(0x1A, 0x1A, 0x1A);

    private readonly WireGuardService _wg;
    private readonly SettingsService _settings;
    private readonly HashSet<string> _silent = new(StringComparer.OrdinalIgnoreCase); // о ком уже сказали «не отвечает»
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly Action _open, _exit;
    private readonly Dictionary<TunnelState, Icon> _icons = [];

    public TrayService(WireGuardService wg, SettingsService settings, Action open, Action exit)
    {
        _wg = wg;
        _settings = settings;
        _open = open;
        _exit = exit;

        _menu = new ContextMenuStrip
        {
            Renderer = new DarkRenderer(),
            ShowImageMargin = false,
            Font = new Font("Segoe UI", 9f),
            Padding = new Padding(4),
        };
        // список серверов и их состояние меняются — собираем меню в момент открытия
        _menu.Opening += (_, _) => BuildMenu();
        BuildMenu();

        _icon = new NotifyIcon { ContextMenuStrip = _menu, Visible = true };
        _icon.DoubleClick += (_, _) => open();

        Apply(null);
        wg.StatusChanged += Apply;
        wg.StatusChanged += NotifyChange;
        wg.Reconnecting += name => Toast(name, "Сервер молчит уже 2 минуты — переподключаю");
    }

    /// <summary>
    /// «Перестал отвечать» — только для включённого туннеля (NoResponse уже означает два потерянных пинга подряд).
    /// «Снова на связи» — только если перед этим было «не отвечает». Ручное выключение молча сбрасывает всё.
    /// </summary>
    private void NotifyChange(TunnelStatus s)
    {
        var name = s.TunnelName;
        switch (s.State)
        {
            case TunnelState.NoResponse when _silent.Add(name):
                Toast(name, $"Сервер {_settings.Find(name)?.PingHost} не отвечает");
                break;
            case TunnelState.Connected when _silent.Remove(name):
                Toast(name, "Снова на связи" + (s.PingMs is { } ms ? $" · {ms} мс" : ""));
                break;
            case TunnelState.Off:
                _silent.Remove(name);
                break;
        }
    }

    private void Toast(string title, string text)
    {
        if (_settings.Current.Notify)
            _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.None);
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var all = _wg.All;
        if (all.Count == 1)
        {
            var s = all[0];
            _menu.Items.Add(new ToolStripMenuItem(s.State == TunnelState.Off ? "Включить" : "Выключить", null, (_, _) => _ = _wg.ToggleAsync(s.TunnelName)));
        }
        else
        {
            foreach (var s in all)
            {
                var verb = s.State == TunnelState.Off ? "Включить" : "Выключить";
                _menu.Items.Add(new ToolStripMenuItem($"{verb} {s.TunnelName}", null, (_, _) => _ = _wg.ToggleAsync(s.TunnelName)));
            }
        }
        if (all.Count > 0) _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Открыть", null, (_, _) => _open()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => _exit()));
        foreach (ToolStripItem item in _menu.Items)
        {
            item.ForeColor = Paper;
            item.Padding = new Padding(6, 4, 18, 4);
        }
    }

    /// <summary>
    /// Цвет иконки — по всем серверам: красный, если хоть один включён и молчит; зелёный, если что-то подключено;
    /// жёлтый — что-то подключается; серый — всё выключено.
    /// </summary>
    private void Apply(TunnelStatus? _)
    {
        var all = _wg.All;
        var state =
            all.Any(s => s.State == TunnelState.NoResponse) ? TunnelState.NoResponse
            : all.Any(s => s.State == TunnelState.Connecting) ? TunnelState.Connecting
            : all.Any(s => s.State == TunnelState.Connected) ? TunnelState.Connected
            : TunnelState.Off;
        _icon.Icon = IconFor(state);

        var lines = all.Select(s => (all.Count > 1 ? s.TunnelName + ": " : "") + s.State switch
        {
            TunnelState.Connected => s.PingMs is { } ms ? $"подключено · {ms} мс" : "подключено",
            TunnelState.Connecting => "подключение…",
            TunnelState.NoResponse => "нет ответа",
            _ => s.Error is { } e && all.Count == 1 ? e : "выключено",
        });
        var tip = SettingsService.AppName + "\n" + (all.Count == 0 ? "нет серверов" : string.Join("\n", lines));
        _icon.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    private Icon IconFor(TunnelState state)
    {
        if (_icons.TryGetValue(state, out var cached)) return cached;
        var color = state switch
        {
            TunnelState.Connected => Color.FromArgb(0x22, 0xC5, 0x5E),
            TunnelState.NoResponse => Color.FromArgb(0xEF, 0x44, 0x44),
            TunnelState.Connecting => Color.FromArgb(0xF5, 0x9E, 0x0B),
            _ => Muted,
        };
        return _icons[state] = Draw(color, state != TunnelState.Off);
    }

    /// <summary>Рисуем иконку под текущий DPI: квадрат 14/64 скругления, круг по центру — как favicon.</summary>
    private static Icon Draw(Color dot, bool glow)
    {
        int s = SystemInformation.SmallIconSize.Width;
        using var bmp = new Bitmap(s, s);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            float r = s * 14f / 64f, d = r * 2;
            using var path = new GraphicsPath();
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(s - d - 0.5f, 0, d, d, 270, 90);
            path.AddArc(s - d - 0.5f, s - d - 0.5f, d, d, 0, 90);
            path.AddArc(0, s - d - 0.5f, d, d, 90, 90);
            path.CloseFigure();
            using (var bg = new SolidBrush(Ink)) g.FillPath(bg, path);
            using (var border = new Pen(Line, Math.Max(1f, s / 32f))) g.DrawPath(border, path);

            float c = s * 0.2f;
            if (glow)
                using (var halo = new SolidBrush(Color.FromArgb(70, dot)))
                    g.FillEllipse(halo, s / 2f - c * 1.6f, s / 2f - c * 1.6f, c * 3.2f, c * 3.2f);
            using var fill = new SolidBrush(dot);
            g.FillEllipse(fill, s / 2f - c, s / 2f - c, c * 2, c * 2);
        }

        var h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { Native.DestroyIcon(h); }
    }

    public void Dispose()
    {
        _wg.StatusChanged -= Apply;
        _wg.StatusChanged -= NotifyChange;
        _icon.Visible = false;
        _menu.Dispose();
        _icon.Dispose();
        foreach (var i in _icons.Values) i.Dispose();
    }

    /// <summary>Меню в стиле окна: #0a0a0a, тонкая рамка, мягкая подсветка пункта.</summary>
    private sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new Colors()) => RoundedEdges = false;

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(Ink);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var p = new Pen(Line);
            e.Graphics.DrawRectangle(p, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
            using var path = Rounded(r, 6);
            using var b = new SolidBrush(Hover);
            e.Graphics.FillPath(b, path);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Selected ? Paper : Color.FromArgb(0xC8, 0xC8, 0xC8);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var p = new Pen(Line);
            int y = e.Item.Height / 2;
            e.Graphics.DrawLine(p, 8, y, e.Item.Width - 8, y);
        }

        private static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        private sealed class Colors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Ink;
            public override Color MenuBorder => Line;
            public override Color MenuItemBorder => Color.Transparent;
            public override Color MenuItemSelected => Hover;
            public override Color SeparatorDark => Line;
            public override Color SeparatorLight => Line;
        }
    }
}
