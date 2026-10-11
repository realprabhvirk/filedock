using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace FileDock.Services;

/// <summary>Commands the tray menu can run. Supplied by the app so the tray holds no state of its own.</summary>
public sealed class TrayCommands
{
    public required Action Show { get; init; }
    public required Action Hide { get; init; }
    public required Action TogglePin { get; init; }
    public required Action Clear { get; init; }
    public required Action OpenSettings { get; init; }
    public required Action Exit { get; init; }
    public required Func<bool> IsVisible { get; init; }
    public required Func<bool> IsPinned { get; init; }
    public required Func<int> ItemCount { get; init; }
}

/// <summary>The notification-area icon and its menu (Windows Forms NotifyIcon).</summary>
public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu;
    private readonly TrayCommands _commands;
    private readonly ToolStripMenuItem _show;
    private readonly ToolStripMenuItem _hide;
    private readonly ToolStripMenuItem _pin;
    private readonly ToolStripMenuItem _clear;

    public TrayService(TrayCommands commands, Stream iconStream)
    {
        _commands = commands;

        _show = new ToolStripMenuItem("Show FileDock", null, (_, _) => _commands.Show());
        _hide = new ToolStripMenuItem("Hide FileDock", null, (_, _) => _commands.Hide());
        _pin = new ToolStripMenuItem("Pin open", null, (_, _) => _commands.TogglePin());
        _clear = new ToolStripMenuItem("Clear shelf", null, (_, _) => _commands.Clear());
        var settings = new ToolStripMenuItem("Settings", null, (_, _) => _commands.OpenSettings());
        var exit = new ToolStripMenuItem("Exit", null, (_, _) => _commands.Exit());
        _show.Font = new Font(_show.Font, System.Drawing.FontStyle.Bold);

        _menu = new ContextMenuStrip { ShowImageMargin = false, Padding = new Padding(2, 4, 2, 4) };
        _menu.Items.AddRange([_show, _hide, new ToolStripSeparator(), _pin, _clear, new ToolStripSeparator(), settings, exit]);
        _menu.Opening += (_, _) => RefreshMenu();
        ApplyTheme(dark: false);

        _icon = new NotifyIcon
        {
            Icon = new Icon(iconStream, SystemInformation.SmallIconSize),
            Text = "FileDock",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _commands.Show();
        };
    }

    public void UpdateTooltip(int count)
    {
        // NotifyIcon text is limited to 127 characters; this stays well inside it.
        _icon.Text = count == 0 ? "FileDock â€” empty" : $"FileDock â€” {count} {(count == 1 ? "item" : "items")}";
    }

    public void ApplyTheme(bool dark)
    {
        var colors = new FlatMenuColors(dark);
        _menu.Renderer = new FlatMenuRenderer(colors);
        _menu.BackColor = colors.ToolStripDropDownBackground;
        _menu.ForeColor = colors.Text;
    }

    private void RefreshMenu()
    {
        bool visible = _commands.IsVisible();
        _hide.Enabled = visible;
        _pin.Text = _commands.IsPinned() ? "Unpin" : "Pin open";
        _clear.Enabled = _commands.ItemCount() > 0;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }

    /// <summary>Flat, neutral colours instead of the default gradient "professional" look.</summary>
    private sealed class FlatMenuColors(bool dark) : ProfessionalColorTable
    {
        public Color Text { get; } = dark ? Color.FromArgb(0xF2, 0xF2, 0xF2) : Color.FromArgb(0x20, 0x20, 0x20);
        public Color DisabledText { get; } = dark ? Color.FromArgb(0x78, 0x78, 0x78) : Color.FromArgb(0xA0, 0xA0, 0xA0);
        private Color Background { get; } = dark ? Color.FromArgb(0x2B, 0x2B, 0x2B) : Color.FromArgb(0xF9, 0xF9, 0xF9);
        private Color Hover { get; } = dark ? Color.FromArgb(0x3A, 0x3A, 0x3A) : Color.FromArgb(0xEA, 0xEA, 0xEA);
        private Color Border { get; } = dark ? Color.FromArgb(0x45, 0x45, 0x45) : Color.FromArgb(0xD4, 0xD4, 0xD4);
        private Color Separator { get; } = dark ? Color.FromArgb(0x40, 0x40, 0x40) : Color.FromArgb(0xE6, 0xE6, 0xE6);

        public override Color ToolStripDropDownBackground => Background;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color MenuItemPressedGradientBegin => Hover;
        public override Color MenuItemPressedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color SeparatorDark => Separator;
        public override Color SeparatorLight => Separator;
    }

    private sealed class FlatMenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly FlatMenuColors colors;

        public FlatMenuRenderer(FlatMenuColors colors)
            : base(colors)
        {
            this.colors = colors;
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? colors.Text : colors.DisabledText;
            base.OnRenderItemText(e);
        }
    }
}
