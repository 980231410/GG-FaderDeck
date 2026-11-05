using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace FaderDeck.App;

/// <summary>
/// 托盘右键菜单的深色皮肤：和面板同一套配色（#1B2228 底、#2F3B46 悬停、#2A353F 分隔线）。
/// 圆角靠给下拉窗口本身设置 Region 实现（四角对称、真正裁掉），
/// 文字用 TextRenderer 垂直居中重绘，避免系统渲染器把字画偏上。
/// </summary>
internal sealed class TrayMenuRenderer : ToolStripProfessionalRenderer
{
    internal static readonly Color Background = Color.FromArgb(0x1B, 0x22, 0x28);
    internal static readonly Color Border = Color.FromArgb(0x2A, 0x35, 0x3F);
    internal static readonly Color Hover = Color.FromArgb(0x2F, 0x3B, 0x46);
    internal static readonly Color Text = Color.FromArgb(0xE6, 0xEA, 0xF0);
    internal static readonly Color TextMuted = Color.FromArgb(0x8B, 0x96, 0xA8);

    /// <summary>菜单外边距：条目悬停块离窗口边缘的间距。</summary>
    internal const int MenuInset = 6;

    /// <summary>圆角半径（外轮廓与 Region 一致）。</summary>
    internal const int CornerRadius = 8;

    public TrayMenuRenderer()
        : base(new TrayMenuColorTable())
    {
        RoundedEdges = false;
    }

    /// <summary>把下拉窗口裁成圆角矩形——上下左右四角一致。</summary>
    internal static void ApplyRoundedRegion(ToolStripDropDown menu)
    {
        if (menu.Width <= 0 || menu.Height <= 0)
        {
            return;
        }

        using var path = CreateRoundedPath(new Rectangle(0, 0, menu.Width, menu.Height), CornerRadius);
        var old = menu.Region;
        menu.Region = new Region(path);
        old?.Dispose();
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var brush = new SolidBrush(Background);
        var strip = e.ToolStrip;
        g.FillRectangle(brush, new Rectangle(0, 0, e.AffectedBounds.Width + e.AffectedBounds.X, strip?.Height ?? e.AffectedBounds.Height));
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

        // 宽度取窗口宽度而不是条目宽度：条目宽度在左右内边距下会溢出，导致右侧圆角被裁掉。
        var stripWidth = e.ToolStrip?.Width ?? e.Item.Width;
        var width = stripWidth - (MenuInset * 2);
        var height = e.Item.Height - 8;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var bounds = new Rectangle(MenuInset, (e.Item.Height - height) / 2, width, height);
        using var path = CreateRoundedPath(bounds, 6);
        using var brush = new SolidBrush(Hover);
        g.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        var g = e.Graphics;
        var color = e.Item.Enabled ? Text : TextMuted;
        var stripWidth = e.ToolStrip?.Width ?? e.Item.Width;
        var bounds = new Rectangle(12, 0, stripWidth - 20, e.Item.Height);
        TextRenderer.DrawText(
            g,
            e.Text,
            e.TextFont,
            bounds,
            color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var g = e.Graphics;
        var y = e.Item.Height / 2;
        using var pen = new Pen(Border);
        var stripWidth = e.ToolStrip?.Width ?? e.Item.Width;
        g.DrawLine(pen, new Point(MenuInset * 2, y), new Point(stripWidth - (MenuInset * 2), y));
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
