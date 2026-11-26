using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;

/// Design A, "Swiss Panel": white card, hairlines, IBM Plex (bundled), one red accent.
static class Theme
{
    public static readonly Color Paper = Color.White;
    public static readonly Color Ink = Color.FromArgb(0x16, 0x16, 0x16);
    public static readonly Color Muted = Color.FromArgb(0x8A, 0x8A, 0x86);
    public static readonly Color Line = Color.FromArgb(0xDC, 0xDC, 0xD8);
    public static readonly Color Hair = Color.FromArgb(0xEC, 0xEC, 0xE8);
    public static readonly Color Wash = Color.FromArgb(0xF5, 0xF5, 0xF3);
    public static readonly Color Red = Color.FromArgb(0xE5, 0x41, 0x3B);

    public static readonly FontFamily SansLight = Load("IBMPlexSans-Light.ttf", "Segoe UI Light");
    public static readonly FontFamily Sans = Load("IBMPlexSans-Regular.ttf", "Segoe UI");
    public static readonly FontFamily SansMedium = Load("IBMPlexSans-Medium.ttf", "Segoe UI Semibold");
    public static readonly FontFamily MonoLight = Load("IBMPlexMono-Light.ttf", "Cascadia Mono Light");
    public static readonly FontFamily Mono = Load("IBMPlexMono-Regular.ttf", "Cascadia Mono");

    /// A font sized in CSS-style pixels at the given DPI scale.
    public static Font Px(FontFamily family, float px, float scale) => new(family, px * scale, GraphicsUnit.Pixel);

    [DllImport("gdi32.dll")] static extern IntPtr AddFontMemResourceEx(IntPtr data, uint len, IntPtr reserved, out uint count);

    static readonly List<PrivateFontCollection> keep = [];

    static FontFamily Load(string resource, string fallback)
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)!;
            var bytes = new byte[s.Length];
            s.ReadExactly(bytes);
            // The memory has to outlive the fonts, so it is never freed.
            var mem = Marshal.AllocCoTaskMem(bytes.Length);
            Marshal.Copy(bytes, 0, mem, bytes.Length);
            AddFontMemResourceEx(mem, (uint)bytes.Length, IntPtr.Zero, out _); // for GDI (TextRenderer, menus)
            var pfc = new PrivateFontCollection();
            pfc.AddMemoryFont(mem, bytes.Length);                              // for GDI+ (Font objects)
            keep.Add(pfc);
            return pfc.Families[0];
        }
        catch { return new FontFamily(fallback); }
    }

    // ---- drawing helpers ----

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.Left, r.Top, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Fill(Graphics g, Color c, RectangleF r, float radius)
    {
        using var b = new SolidBrush(c);
        using var p = Round(r, radius);
        g.FillPath(b, p);
    }

    public static void Stroke(Graphics g, Color c, RectangleF r, float radius, float width = 1)
    {
        using var pen = new Pen(c, width);
        using var p = Round(new RectangleF(r.X + width / 2, r.Y + width / 2, r.Width - width, r.Height - width), radius);
        g.DrawPath(pen, p);
    }

    const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

    public static Size Measure(string text, Font f) => TextRenderer.MeasureText(text, f, Size.Empty, Flags);

    /// Draws text vertically centred on <paramref name="cy"/>, starting at <paramref name="x"/>.
    public static void Text(Graphics g, string text, Font f, Color c, float x, float cy, Color? bg = null)
    {
        var sz = Measure(text, f);
        TextRenderer.DrawText(g, text, f, new Point((int)Math.Round(x), (int)Math.Round(cy - sz.Height / 2f)), c, bg ?? Paper, Flags);
    }

    /// Small-caps style label with letter spacing (TextRenderer has no tracking of its own).
    public static float Tracked(Graphics g, string text, Font f, Color c, float x, float cy, float tracking, bool draw = true)
    {
        foreach (var ch in text)
        {
            var s = ch.ToString();
            if (draw) Text(g, s, f, c, x, cy);
            x += Measure(s, f).Width + tracking;
        }
        return x - tracking;
    }

    // ---- menus ----

    public static ContextMenuStrip Menu(float scale) => new()
    {
        Renderer = new FlatRenderer(),
        ShowImageMargin = false,
        ShowCheckMargin = true,
        Font = Px(SansLight, 13, scale),
        BackColor = Paper,
        ForeColor = Ink,
        Padding = new Padding(0, 4, 0, 4),
    };

    sealed class FlatRenderer() : ToolStripProfessionalRenderer(new Palette())
    {
        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Line);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            int d = Math.Max(4, r.Height / 3);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var b = new SolidBrush(Ink);
            e.Graphics.FillEllipse(b, r.Left + (r.Width - d) / 2, r.Top + (r.Height - d) / 2, d, d);
        }
    }

    sealed class Palette : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Paper;
        public override Color ImageMarginGradientBegin => Paper;
        public override Color ImageMarginGradientMiddle => Paper;
        public override Color ImageMarginGradientEnd => Paper;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Wash;
        public override Color MenuItemSelected => Wash;
        public override Color CheckBackground => Paper;
        public override Color CheckSelectedBackground => Wash;
        public override Color CheckPressedBackground => Wash;
        public override Color SeparatorDark => Hair;
        public override Color SeparatorLight => Paper;
    }
}
