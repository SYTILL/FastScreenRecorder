/// Dims every monitor; drag a rectangle on one of them. Esc or right-click cancels.
/// Result is in physical pixels relative to that monitor (the app is per-monitor DPI aware).
sealed class AreaPicker : Form
{
    public static Task<(Screen Screen, Rectangle Rect)?> Pick()
    {
        var done = new TaskCompletionSource<(Screen, Rectangle)?>();
        var all = Screen.AllScreens.Select(s => new AreaPicker(s)).ToList();
        foreach (var p in all)
        {
            p.finish = r =>
            {
                done.TrySetResult(r);
                foreach (var q in all) q.Close();
            };
            p.Show();
        }
        (all.FirstOrDefault(p => p.screen.Bounds.Contains(Cursor.Position)) ?? all[0]).Activate();
        return done.Task;
    }

    static readonly Color Hole = Color.FromArgb(255, 0, 255);

    readonly Screen screen;
    Action<(Screen, Rectangle)?> finish = _ => { };
    Point start;
    Rectangle sel;
    bool dragging;

    AreaPicker(Screen s)
    {
        screen = s;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Bounds = s.Bounds;
        BackColor = Color.Black;
        TransparencyKey = Hole;
        Opacity = 0.35;
        Cursor = Cursors.Cross;
        DoubleBuffered = true;
        KeyPreview = true;
    }

    // Don't let Windows rescale the overlay when it lands on a monitor with a different DPI.
    protected override void OnDpiChanged(DpiChangedEventArgs e) { e.Cancel = true; Bounds = screen.Bounds; }
    protected override void OnShown(EventArgs e) { base.OnShown(e); Bounds = screen.Bounds; }

    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) finish(null); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) { finish(null); return; }
        start = e.Location; sel = Rectangle.Empty; dragging = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!dragging) return;
        var old = sel;
        sel = Rectangle.FromLTRB(Math.Min(start.X, e.X), Math.Min(start.Y, e.Y), Math.Max(start.X, e.X), Math.Max(start.Y, e.Y));
        sel.Intersect(ClientRectangle);
        var dirty = Rectangle.Union(old, sel);
        dirty.Inflate(120, 40);
        Invalidate(dirty);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (!dragging) return;
        dragging = false;
        var r = new Rectangle(sel.X, sel.Y, sel.Width & ~1, sel.Height & ~1); // encoders want even sizes
        if (r.Width < 16 || r.Height < 16) { sel = Rectangle.Empty; Invalidate(); return; }
        finish((screen, r));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (sel.IsEmpty) return;
        var g = e.Graphics;
        using (var hole = new SolidBrush(Hole)) g.FillRectangle(hole, sel);
        using (var pen = new Pen(Color.White)) g.DrawRectangle(pen, sel.X - 1, sel.Y - 1, sel.Width + 1, sel.Height + 1);
        var label = $"{sel.Width & ~1} × {sel.Height & ~1}";
        var y = sel.Bottom + 6 + 20 > ClientSize.Height ? sel.Top - 26 : sel.Bottom + 6;
        using var font = Theme.Px(Theme.Mono, 12, DeviceDpi / 96f);
        TextRenderer.DrawText(g, label, font, new Point(sel.X, Math.Max(0, y)), Color.White);
    }
}
