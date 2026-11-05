using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FaderDeck.App;

/// <summary>
/// 托盘右键菜单的深色皮肤：和面板同一套配色（#1B2228 底、#2A343D 悬停、#2A353F 分隔线），
/// 圆角 + 细边，去掉系统默认的浅色渐变与图标栏留白。
/// </summary>
internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
{
    internal static readonly Color Background = Color.FromArgb(0x1B, 0x22, 0x28);
    internal static readonly Color Border = Color.FromArgb(0x2A, 0x35, 0x3F);
    internal static readonly Color Hover = Color.FromArgb(0x2F, 0x3B, 0x46);
    internal static readonly Color Text = Color.FromArgb(0xE6, 0xEA, 0xF0);
    internal static readonly Color TextMuted = Color.FromArgb(0x8B, 0x96, 0xA8);

    private const int CornerRadius = 8;

    public TrayMenuRenderer()
        : base(new TrayMenuColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        using var path = CreateRoundedPath(bounds, CornerRadius);
        using var brush = new SolidBrush(Background);
        g.FillPath(brush, path);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        using var path = CreateRoundedPath(bounds, CornerRadius);
        using var pen = new Pen(Border);
        g.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled)
        {
            return;
        }

        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
        using var path = CreateRoundedPath(bounds, 5);
        using var brush = new SolidBrush(Hover);
        g.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Text : TextMuted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var g = e.Graphics;
        var y = e.Item.Height / 2;
        using var pen = new Pen(Border);
        g.DrawLine(pen, new Point(12, y), new Point(e.Item.Width - 12, y));
    }

    private static GraphicsPath CreateRoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private sealed class TrayMenuColorTable : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Background;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}
