using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows;
using System.Windows.Input;
using Utility.App.Localization;
using Utility.App.ViewModels;
using Utility.Core.Models;
using Forms = System.Windows.Forms;

namespace Utility.App;

/// <summary>
/// Notification-area icon with a light, app-styled menu: open, the two power profiles
/// (active one checked), cleanup / cooling actions, and exit. Item states refresh on open.
/// </summary>
internal sealed class UtilityTray : IDisposable
{
    private readonly MainWindow _window;
    private readonly MainViewModel _viewModel;
    private readonly Icon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly List<Image> _glyphs = [];
    private readonly Font _glyphFont;
    private bool _exiting;

    public UtilityTray(MainWindow window, MainViewModel viewModel, Action exit)
    {
        _window = window;
        _viewModel = viewModel;

        var iconUri = new Uri("pack://application:,,,/Assets/Utility.ico");
        using var resource = Application.GetResourceStream(iconUri).Stream;
        // Pick the icon frame for the current system tray DPI.
        _icon = new Icon(resource, Forms.SystemInformation.SmallIconSize);

        var scale = Math.Max(1f, Forms.SystemInformation.SmallIconSize.Width / 16f);
        _menu = new Forms.ContextMenuStrip
        {
            Renderer = new TrayRenderer(),
            Font = new Font("Microsoft YaHei UI", 9.5f),
            ShowImageMargin = true,
            ImageScalingSize = new System.Drawing.Size((int)(16 * scale), (int)(16 * scale)),
            Padding = new Forms.Padding(4, 6, 4, 6)
        };

        _glyphFont = CreateGlyphFont(_menu.ImageScalingSize.Height * 0.72f);
        var open = AddItem(Loc.Get("TrayOpen"), "", (_, _) => ShowWindow());
        open.Font = new Font(_menu.Font, System.Drawing.FontStyle.Bold);
        _menu.Items.Add(new Forms.ToolStripSeparator());

        var daily = AddCommand("ProfileDailyName", null, viewModel.ApplyProfilePowerCommand, ProfileSeedFactory.DailyId);
        var gaming = AddCommand("ProfileGamingName", null, viewModel.ApplyProfilePowerCommand, ProfileSeedFactory.GamingId);
        _menu.Items.Add(new Forms.ToolStripSeparator());

        AddCommand("BtnQuickCleanup", "", viewModel.QuickCleanupCommand);
        AddCommand("BtnCooling", "", viewModel.StartCoolingCommand);
        var stopCooling = AddCommand("BtnStopCooling", "", viewModel.StopCoolingCommand);
        _menu.Items.Add(new Forms.ToolStripSeparator());

        var exitItem = AddItem(Loc.Get("TrayExit"), "", (_, _) =>
        {
            if (viewModel.IsHardwareBusy)
                return;
            _exiting = true;
            exit();
        });

        _menu.Opening += (_, _) =>
        {
            daily.Checked = viewModel.ActiveProfileId == ProfileSeedFactory.DailyId;
            gaming.Checked = viewModel.ActiveProfileId == ProfileSeedFactory.GamingId;
            stopCooling.Visible = viewModel.StopCoolingCommand.CanExecute(null);
            exitItem.Enabled = !viewModel.IsHardwareBusy;
        };

        _notifyIcon = new Forms.NotifyIcon { Icon = _icon, Text = "Utility", ContextMenuStrip = _menu, Visible = true };
        _notifyIcon.MouseMove += (_, _) => _notifyIcon.Text = Truncate("Utility · " + StatusLine(), 127);
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
                ShowWindow();
        };
        _notifyIcon.BalloonTipClicked += (_, _) => ShowWindow();
        _window.Closing += HideOnClose;
        _viewModel.QuickActionCompleted += NotifyWhenHidden;
    }

    /// <summary>Active power profile name only; "Utility" when the profile is unknown.</summary>
    private string StatusLine()
    {
        return _viewModel.ActiveProfileId switch
        {
            ProfileSeedFactory.GamingId => Loc.Get("ProfileGamingName"),
            ProfileSeedFactory.DailyId => Loc.Get("ProfileDailyName"),
            _ => "Utility"
        };
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    private Forms.ToolStripMenuItem AddItem(string text, string? glyph, EventHandler onClick)
    {
        var item = new Forms.ToolStripMenuItem(text, glyph is null ? null : Glyph(glyph), onClick) { Padding = new Forms.Padding(4, 5, 4, 5) };
        _menu.Items.Add(item);
        return item;
    }

    private Forms.ToolStripMenuItem AddCommand(string label, string? glyph, ICommand command, object? parameter = null)
    {
        var item = AddItem(Loc.Get(label), glyph, (_, _) => command.Execute(parameter));
        _menu.Opening += (_, _) => item.Enabled = command.CanExecute(parameter);
        return item;
    }

    /// <summary>Renders a Segoe Fluent Icons glyph to a bitmap for the menu's image margin.</summary>
    private Image Glyph(string glyph)
    {
        var size = _menu.ImageScalingSize;
        var bitmap = new Bitmap(size.Width, size.Height);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var brush = new SolidBrush(TrayRenderer.IconColor))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString(glyph, _glyphFont, brush, new RectangleF(0, 0, size.Width, size.Height), format);
        }
        _glyphs.Add(bitmap);
        return bitmap;
    }

    private static Font CreateGlyphFont(float size)
    {
        using var preferred = new Font("Segoe Fluent Icons", size, GraphicsUnit.Pixel);
        return preferred.Name == "Segoe Fluent Icons" ? (Font)preferred.Clone() : new Font("Segoe MDL2 Assets", size, GraphicsUnit.Pixel);
    }

    private void HideOnClose(object? sender, CancelEventArgs e)
    {
        if (_exiting)
            return;
        e.Cancel = true;
        _window.Hide();
    }

    public void ShowWindow()
    {
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        if (!_window.IsVisible)
            _window.Show();
        _window.Activate();
    }

    private void NotifyWhenHidden(string status)
    {
        if (!_window.IsVisible || _window.WindowState == WindowState.Minimized)
            _notifyIcon.ShowBalloonTip(4500, "Utility", status, Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _viewModel.QuickActionCompleted -= NotifyWhenHidden;
        _window.Closing -= HideOnClose;
        _notifyIcon.Dispose();
        _menu.Dispose();
        foreach (var image in _glyphs)
            image.Dispose();
        _icon.Dispose();
        _glyphFont.Dispose();
    }

    /// <summary>Light menu matching the app: white surface, soft rounded highlight, thin separators.</summary>
    private sealed class TrayRenderer : Forms.ToolStripProfessionalRenderer
    {
        public static readonly Color Surface = Color.White;
        public static readonly Color Hover = Color.FromArgb(0xEA, 0xF0, 0xFF);
        public static readonly Color Border = Color.FromArgb(0xD4, 0xDA, 0xE4);
        public static readonly Color Separator = Color.FromArgb(0xE5, 0xE9, 0xF0);
        public static readonly Color Text = Color.FromArgb(0x1B, 0x21, 0x30);
        public static readonly Color MutedText = Color.FromArgb(0x5F, 0x69, 0x7B);
        public static readonly Color DisabledText = Color.FromArgb(0xAA, 0xB1, 0xBE);
        public static readonly Color IconColor = Color.FromArgb(0x2F, 0x6B, 0xFF);

        public TrayRenderer() : base(new Palette()) => RoundedEdges = false;

        protected override void OnRenderToolStripBackground(Forms.ToolStripRenderEventArgs e) =>
            e.Graphics.Clear(Surface);

        protected override void OnRenderToolStripBorder(Forms.ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Border);
            e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }

        protected override void OnRenderImageMargin(Forms.ToolStripRenderEventArgs e)
        {
            // No grey gutter: the icon column shares the white surface.
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled)
                return;
            var bounds = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
            using var path = RoundedRectangle(bounds, 6);
            using var brush = new SolidBrush(Hover);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(brush, path);
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = !e.Item.Enabled ? DisabledText : e.Item is Forms.ToolStripLabel ? MutedText : Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(Forms.ToolStripItemImageRenderEventArgs e)
        {
            // Checked = active profile: a small accent dot instead of the boxed check mark.
            var size = Math.Min(e.ImageRectangle.Width, e.ImageRectangle.Height) / 2.6f;
            var x = e.ImageRectangle.Left + (e.ImageRectangle.Width - size) / 2;
            var y = e.ImageRectangle.Top + (e.ImageRectangle.Height - size) / 2;
            using var brush = new SolidBrush(IconColor);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillEllipse(brush, x, y, size, size);
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using var pen = new Pen(Separator);
            e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
        }

        private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            var diameter = radius * 2;
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private sealed class Palette : Forms.ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground => Surface;
            public override Color MenuBorder => Border;
            public override Color MenuItemBorder => Color.Transparent;
            public override Color MenuItemSelected => Hover;
            public override Color ImageMarginGradientBegin => Surface;
            public override Color ImageMarginGradientMiddle => Surface;
            public override Color ImageMarginGradientEnd => Surface;
            public override Color SeparatorDark => Separator;
            public override Color SeparatorLight => Surface;
        }
    }
}
