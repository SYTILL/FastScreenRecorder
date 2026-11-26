using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

/// The whole app, drawn by hand to match design A ("Swiss Panel").
///
///   Idle                                                      Recording
///   ┌──────────────────────────────────────────────┬──────┐   ┌──────────────────────────────────────────┐
///   │ SOURCE   [Screen|Window|Area]  Display 1 ⌄   │  (●) │   │ ●  00:12:48 │ Display 1 · Sound   [II] [■] │
///   │ ──────────────────────────────────────────── │  F9  │   └──────────────────────────────────────────┘
///   │ CAPTURE  ◉ System sound  ○ Microphone  ◉ Cursor     │
///   └──────────────────────────────────────────────┴──────┘
sealed class Bar : Form
{
    const int HotkeyId = 1, VK_F9 = 0x78;
    static readonly string SettingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastScreenRecorder", "save-folder.txt");
    static readonly string DefaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FastScreenRecorder");
    string outDir = LoadOutDir();

    enum Mode { Screen, Window, Area }
    enum Hit { None, Screen, Window, Area, Target, Sound, Mic, Cursor, Rec, Close, Pause, Stop, Folder }

    // what to record
    Mode mode = Mode.Screen;
    string source = "", targetName = "", targetDetail = "";
    bool sound = true, mic, cursor = true;

    // recording
    Session? session;
    bool busy;
    readonly Stopwatch clock = new();
    readonly System.Windows.Forms.Timer tick = new() { Interval = 250 };

    // drawing
    float s = 1;
    Font fLabel = null!, fBody = null!, fMono = null!, fTime = null!;
    readonly Dictionary<Hit, RectangleF> hits = [];
    Hit hover, pressed;
    bool nativeFrame; // Windows 11 draws the rounded border and shadow for us

    public Bar()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Theme.Paper;
        Text = "FastScreenRecorder";
        DoubleBuffered = true;
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }

        SelectScreen(Screen.PrimaryScreen!);
        tick.Tick += (_, _) => Invalidate();
    }

    // ---- window plumbing ----

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x20000;        // WS_MINIMIZEBOX: lets the taskbar button minimize/restore
            cp.ClassStyle |= 0x20000;   // CS_DROPSHADOW
            return cp;
        }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.RegisterHotKey(Handle, HotkeyId, 0, VK_F9);
        Native.ExcludeFromCapture(Handle);
        int round = 2, border = ColorTranslator.ToWin32(Theme.Line);
        nativeFrame = DwmSetWindowAttribute(Handle, 33, ref round, 4) == 0; // DWMWA_WINDOW_CORNER_PREFERENCE
        DwmSetWindowAttribute(Handle, 34, ref border, 4);                  // DWMWA_BORDER_COLOR
        Rescale();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Relayout();
        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (int)(16 * s));
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Rescale();
    }

    void Rescale()
    {
        s = DeviceDpi / 96f * UiScale;
        fLabel = Theme.Px(Theme.SansMedium, 10, s);
        fBody = Theme.Px(Theme.SansLight, 13, s);
        fMono = Theme.Px(Theme.Mono, 11.5f, s);
        fTime = Theme.Px(Theme.MonoLight, 20, s);
        Relayout();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && m.WParam == HotkeyId) Toggle();
        base.WndProc(ref m);
        if (m.Msg == Native.WM_NCHITTEST && m.Result == 1)
        {
            var p = PointToClient(new Point((short)(m.LParam.ToInt64() & 0xFFFF), (short)((m.LParam.ToInt64() >> 16) & 0xFFFF)));
            if (HitAt(p) == Hit.None) m.Result = Native.HTCAPTION; // drag from anywhere that isn't a control
        }
    }

    // ---- layout (logical px × s) ----

    /// Overall size of the UI relative to the design mockup.
    const float UiScale = 0.85f;

    float P(float v) => v * s;

    static readonly (Hit hit, string text)[] Segments = [(Hit.Screen, "Screen"), (Hit.Window, "Window"), (Hit.Area, "Area")];

    void Relayout()
    {
        if (fBody == null) return;
        hits.Clear();
        Size size;
        if (session == null)
        {
            float x0 = P(24) + P(64) + P(20), rowY = P(20), rowH = P(36);

            float x = x0;
            foreach (var (hit, text) in Segments)
            {
                float w = P(14) + P(15) + P(8) + Theme.Measure(text, fBody).Width + P(14);
                hits[hit] = new RectangleF(x, rowY, w, rowH);
                x += w;
            }
            x += P(20);
            float tw = Theme.Measure(targetName, fBody).Width + P(10) + Theme.Measure(targetDetail, fMono).Width + P(10) + P(12) + P(4);
            hits[Hit.Target] = new RectangleF(x, rowY, tw, rowH);
            float row1Right = x + tw;

            float row2Y = rowY + rowH + P(18) + 1 + P(18), row2H = P(24);
            x = x0;
            foreach (var (hit, text) in new[] { (Hit.Sound, "System sound"), (Hit.Mic, "Microphone"), (Hit.Cursor, "Cursor") })
            {
                float w = P(28) + P(10) + Theme.Measure(text, fBody).Width;
                hits[hit] = new RectangleF(x, row2Y, w, row2H);
                x += w + P(20);
            }
            float row2Right = x - P(20);

            float row3Y = row2Y + row2H + P(14) + 1 + P(14);
            hits[Hit.Folder] = new RectangleF(x0, row3Y, Theme.Measure(FolderText(), fBody).Width + P(10) + Theme.Measure("Change", fBody).Width + P(2), row2H);
            float row3Right = hits[Hit.Folder].Right;

            float leftW = Math.Max(row1Right, Math.Max(row2Right, row3Right)) + P(24);
            float h = row3Y + row2H + P(20);
            float colW = P(28) + P(58) + P(28);
            float block = P(58) + P(10) + P(14);
            hits[Hit.Rec] = new RectangleF(leftW + P(28), (h - block) / 2, P(58), P(58));
            hits[Hit.Close] = new RectangleF(leftW + colW - P(26), P(4), P(22), P(22));
            size = new Size((int)Math.Ceiling(leftW + colW), (int)Math.Ceiling(h));
        }
        else
        {
            float h = P(56), cy = h / 2, x = P(20);
            x += P(9) + P(18);
            x += Theme.Measure("00:00:00", fTime).Width + P(18);
            x += 1 + P(18);
            x += Theme.Measure(Summary(), fBody).Width + P(18);
            hits[Hit.Pause] = new RectangleF(x, cy - P(18), P(36), P(36));
            x += P(36) + P(8);
            hits[Hit.Stop] = new RectangleF(x, cy - P(18), P(36), P(36));
            x += P(36) + P(12);
            size = new Size((int)Math.Ceiling(x), (int)Math.Ceiling(h));
        }

        // Set the window size directly: setting ClientSize before the first show lets WinForms add a
        // caption's worth of height even though the window has no caption.
        if (Size != size)
        {
            int center = Left + Width / 2;
            Size = size;
            if (IsHandleCreated && Visible) Left = center - Width / 2;
        }
        Invalidate();
    }

    string Summary()
    {
        var bits = new List<string> { targetName };
        if (sound) bits.Add("Sound");
        if (mic) bits.Add("Mic");
        if (cursor) bits.Add("Cursor");
        return session?.Paused == true ? "Paused" : string.Join(" · ", bits);
    }

    Hit HitAt(Point p)
    {
        foreach (var (hit, r) in hits) if (r.Contains(p)) return hit;
        return Hit.None;
    }

    // ---- painting ----

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        if (session == null) PaintIdle(g); else PaintRecording(g);
        if (!nativeFrame)
        {
            using var pen = new Pen(Theme.Line);
            g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        }
    }

    void PaintIdle(Graphics g)
    {
        var seg0 = hits[Hit.Screen];
        float row1 = seg0.Top + seg0.Height / 2;
        var tog0 = hits[Hit.Sound];
        float row2 = tog0.Top + tog0.Height / 2;

        Theme.Tracked(g, "SOURCE", fLabel, Theme.Muted, P(24), row1, P(1.4f));
        Theme.Tracked(g, "CAPTURE", fLabel, Theme.Muted, P(24), row2, P(1.4f));

        // segmented control
        var all = RectangleF.Union(hits[Hit.Screen], hits[Hit.Area]);
        using (var clip = Theme.Round(all, P(7)))
        {
            var state = g.Save();
            g.SetClip(clip);
            foreach (var (hit, text) in Segments)
            {
                var r = hits[hit];
                bool on = (hit == Hit.Screen && mode == Mode.Screen) || (hit == Hit.Window && mode == Mode.Window) || (hit == Hit.Area && mode == Mode.Area);
                var bg = on ? Theme.Ink : hover == hit ? Theme.Wash : Theme.Paper;
                using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);
                var fg = on ? Color.White : Theme.Ink;
                DrawSourceIcon(g, hit, r.X + P(14), row1, fg);
                Theme.Text(g, text, fBody, fg, r.X + P(14) + P(15) + P(8), row1, bg);
                if (hit != Hit.Screen)
                    using (var pen = new Pen(Theme.Line)) g.DrawLine(pen, r.X, r.Top, r.X, r.Bottom);
            }
            g.Restore(state);
        }
        Theme.Stroke(g, Theme.Line, all, P(7));

        // target dropdown
        var t = hits[Hit.Target];
        float x = t.X + P(2);
        Theme.Text(g, targetName, fBody, Theme.Ink, x, row1);
        x += Theme.Measure(targetName, fBody).Width + P(10);
        Theme.Text(g, targetDetail, fMono, Theme.Muted, x, row1);
        x += Theme.Measure(targetDetail, fMono).Width + P(10);
        using (var pen = new Pen(Theme.Ink, P(1.3f)))
        {
            g.DrawLines(pen, [new PointF(x, row1 - P(2)), new PointF(x + P(4), row1 + P(2)), new PointF(x + P(8), row1 - P(2))]);
        }
        using (var pen = new Pen(hover == Hit.Target ? Theme.Ink : Theme.Line))
            g.DrawLine(pen, t.Left, t.Bottom - 1, t.Right, t.Bottom - 1);

        // hairline between rows
        float hy = seg0.Bottom + P(18);
        var rec = hits[Hit.Rec];
        float leftW = rec.X - P(28);
        using (var pen = new Pen(Theme.Hair))
        {
            g.DrawLine(pen, P(24), hy, leftW - P(24), hy);
            g.DrawLine(pen, leftW, 0, leftW, Height);
        }

        // save folder
        var fr = hits[Hit.Folder];
        float row3 = fr.Top + fr.Height / 2;
        using (var pen = new Pen(Theme.Hair)) g.DrawLine(pen, P(24), tog0.Bottom + P(14), leftW - P(24), tog0.Bottom + P(14));
        Theme.Tracked(g, "SAVE TO", fLabel, Theme.Muted, P(24), row3, P(1.4f));
        Theme.Text(g, FolderText(), fBody, Theme.Ink, fr.X, row3);
        float cx0 = fr.X + Theme.Measure(FolderText(), fBody).Width + P(10);
        Theme.Text(g, "Change", fBody, hover == Hit.Folder ? Theme.Ink : Theme.Muted, cx0, row3);
        using (var pen = new Pen(hover == Hit.Folder ? Theme.Ink : Theme.Line))
            g.DrawLine(pen, cx0, fr.Bottom - 1, cx0 + Theme.Measure("Change", fBody).Width, fr.Bottom - 1);

        // toggles
        Toggle(g, Hit.Sound, "System sound", sound, row2);
        Toggle(g, Hit.Mic, "Microphone", mic, row2);
        Toggle(g, Hit.Cursor, "Cursor", cursor, row2);

        // record button
        using (var b = new SolidBrush(hover == Hit.Rec ? Theme.Wash : Theme.Paper)) g.FillEllipse(b, rec);
        using (var pen = new Pen(hover == Hit.Rec ? Theme.Muted : Theme.Line)) g.DrawEllipse(pen, rec.X + 0.5f, rec.Y + 0.5f, rec.Width - 1, rec.Height - 1);
        float d = P(24);
        using (var b = new SolidBrush(busy ? Theme.Line : Theme.Red))
            g.FillEllipse(b, rec.X + (rec.Width - d) / 2, rec.Y + (rec.Height - d) / 2, d, d);
        var f9 = Theme.Measure("F9", fMono);
        Theme.Text(g, "F9", fMono, Theme.Muted, rec.X + (rec.Width - f9.Width) / 2, rec.Bottom + P(10) + P(7));

        // close
        var c = hits[Hit.Close];
        using (var pen = new Pen(hover == Hit.Close ? Theme.Ink : Theme.Muted, P(1.2f)))
        {
            float k = P(4), cx = c.X + c.Width / 2, cy = c.Y + c.Height / 2;
            g.DrawLine(pen, cx - k, cy - k, cx + k, cy + k);
            g.DrawLine(pen, cx - k, cy + k, cx + k, cy - k);
        }
    }

    void Toggle(Graphics g, Hit hit, string text, bool on, float cy)
    {
        var r = hits[hit];
        var track = new RectangleF(r.X, cy - P(8), P(28), P(16));
        Theme.Fill(g, on ? Theme.Ink : hover == hit ? Theme.Muted : Theme.Line, track, P(8));
        float k = P(12);
        using (var b = new SolidBrush(Color.White))
            g.FillEllipse(b, on ? track.Right - P(2) - k : track.X + P(2), cy - k / 2, k, k);
        Theme.Text(g, text, fBody, on || hover == hit ? Theme.Ink : Theme.Muted, track.Right + P(10), cy);
    }

    void PaintRecording(Graphics g)
    {
        float cy = Height / 2f, x = P(20);
        bool paused = session!.Paused;
        using (var b = new SolidBrush(paused ? Theme.Muted : Theme.Red))
            g.FillEllipse(b, x, cy - P(4.5f), P(9), P(9));
        x += P(9) + P(18);

        var t = clock.Elapsed;
        Theme.Text(g, $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}", fTime, paused ? Theme.Muted : Theme.Ink, x, cy);
        x += Theme.Measure("00:00:00", fTime).Width + P(18);

        using (var pen = new Pen(Theme.Hair)) g.DrawLine(pen, x, cy - P(10), x, cy + P(10));
        x += 1 + P(18);
        Theme.Text(g, Summary(), fBody, Theme.Muted, x, cy);

        var p = hits[Hit.Pause];
        Theme.Fill(g, hover == Hit.Pause ? Theme.Wash : Theme.Paper, p, P(7));
        Theme.Stroke(g, Theme.Line, p, P(7));
        float pcx = p.X + p.Width / 2;
        if (paused)
        {
            using var b = new SolidBrush(Theme.Ink);
            g.FillPolygon(b, [new PointF(pcx - P(4), cy - P(6)), new PointF(pcx + P(6), cy), new PointF(pcx - P(4), cy + P(6))]);
        }
        else
        {
            using var pen = new Pen(Theme.Ink, P(1.6f));
            g.DrawLine(pen, pcx - P(3), cy - P(7), pcx - P(3), cy + P(7));
            g.DrawLine(pen, pcx + P(3), cy - P(7), pcx + P(3), cy + P(7));
        }

        var st = hits[Hit.Stop];
        Theme.Fill(g, hover == Hit.Stop ? Color.FromArgb(0x33, 0x33, 0x33) : Theme.Ink, st, P(7));
        float q = P(11);
        Theme.Fill(g, busy ? Theme.Muted : Color.White, new RectangleF(st.X + (st.Width - q) / 2, cy - q / 2, q, q), P(2));
    }

    void DrawSourceIcon(Graphics g, Hit hit, float x, float cy, Color c)
    {
        // 15px icons on a 24-unit grid, stroked like the mockup's inline SVGs.
        float u = P(15) / 24f, top = cy - P(7.5f);
        PointF Pt(float px, float py) => new(x + px * u, top + py * u);
        using var pen = new Pen(c, P(1.2f)) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        switch (hit)
        {
            case Hit.Screen:
                using (var r = Theme.Round(new RectangleF(Pt(3, 4), new SizeF(18 * u, 12 * u)), 1.5f * u)) g.DrawPath(pen, r);
                g.DrawLine(pen, Pt(8, 20), Pt(16, 20));
                g.DrawLine(pen, Pt(12, 16), Pt(12, 20));
                break;
            case Hit.Window:
                using (var r = Theme.Round(new RectangleF(Pt(3, 5), new SizeF(18 * u, 14 * u)), 1.5f * u)) g.DrawPath(pen, r);
                g.DrawLine(pen, Pt(3, 9), Pt(21, 9));
                break;
            case Hit.Area:
                g.DrawLines(pen, [Pt(7, 3), Pt(7, 17), Pt(21, 17)]);
                g.DrawLines(pen, [Pt(3, 7), Pt(17, 7), Pt(17, 21)]);
                break;
        }
    }

    // ---- input ----

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = HitAt(e.Location);
        if (h == hover) return;
        hover = h;
        Cursor = h == Hit.None ? Cursors.Default : Cursors.Hand;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e) { hover = Hit.None; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = HitAt(e.Location); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        var h = HitAt(e.Location);
        if (h != pressed || h == Hit.None) return;
        pressed = Hit.None;
        switch (h)
        {
            case Hit.Screen: PickScreen(); break;
            case Hit.Window: PickWindow(); break;
            case Hit.Area: PickArea(); break;
            case Hit.Target: if (mode == Mode.Screen) PickScreen(); else if (mode == Mode.Window) PickWindow(); else PickArea(); break;
            case Hit.Sound: sound = !sound; Invalidate(); break;
            case Hit.Mic: mic = !mic; Invalidate(); break;
            case Hit.Cursor: cursor = !cursor; Invalidate(); break;
            case Hit.Rec: case Hit.Stop: Toggle(); break;
            case Hit.Pause: TogglePause(); break;
            case Hit.Close: Close(); break;
            case Hit.Folder: PickFolder(); break;
        }
    }

    // ---- choosing what to record ----

    void PickScreen()
    {
        var screens = Screens();
        if (screens.Length == 1) { SelectScreen(screens[0]); return; }
        var menu = Theme.Menu(s);
        for (int i = 0; i < screens.Length; i++)
        {
            var sc = screens[i];
            var item = new ToolStripMenuItem($"Display {i + 1}     {sc.Bounds.Width}×{sc.Bounds.Height}{(sc.Primary ? "     primary" : "")}")
            { Checked = mode == Mode.Screen && source == $"hmonitor={Native.Monitor(sc)}" };
            item.Click += (_, _) => SelectScreen(sc);
            menu.Items.Add(item);
        }
        Drop(menu, hits[Hit.Screen]);
    }

    /// Main monitor first, then left to right, so "Display 1" is always the main one.
    static Screen[] Screens() => [.. Screen.AllScreens.OrderByDescending(x => x.Primary).ThenBy(x => x.Bounds.Left).ThenBy(x => x.Bounds.Top)];

    void SelectScreen(Screen sc)
    {
        mode = Mode.Screen;
        source = $"hmonitor={Native.Monitor(sc)}";
        targetName = $"Display {Array.IndexOf(Screens(), sc) + 1}";
        targetDetail = $"{sc.Bounds.Width}×{sc.Bounds.Height}";
        Relayout();
    }

    void PickWindow()
    {
        var menu = Theme.Menu(s);
        var windows = Native.Windows();
        if (windows.Count == 0) menu.Items.Add(new ToolStripMenuItem("No windows") { Enabled = false });
        foreach (var (hwnd, title) in windows)
        {
            var item = new ToolStripMenuItem(Clip(title, 60)) { Checked = mode == Mode.Window && source == $"hwnd={hwnd}" };
            item.Click += (_, _) =>
            {
                mode = Mode.Window;
                source = $"hwnd={hwnd}";
                targetName = Clip(title, 26);
                targetDetail = "window";
                Relayout();
            };
            menu.Items.Add(item);
        }
        Drop(menu, hits[Hit.Window]);
    }

    async void PickArea()
    {
        Hide();
        var pick = await AreaPicker.Pick();
        Show();
        if (pick is not { } p) return;
        var b = p.Screen.Bounds;
        var r = p.Rect;
        mode = Mode.Area;
        source = $"hmonitor={Native.Monitor(p.Screen)}:crop_left={r.Left}:crop_top={r.Top}:crop_right={b.Width - r.Right}:crop_bottom={b.Height - r.Bottom}";
        targetName = "Area";
        targetDetail = $"{r.Width}×{r.Height}";
        Relayout();
    }

    void Drop(ContextMenuStrip menu, RectangleF under)
    {
        menu.Closed += (_, _) => menu.BeginInvoke(menu.Dispose);
        menu.Show(this, new Point((int)under.X, (int)(under.Bottom + P(6))));
    }

    // ---- save folder ----

    static string LoadOutDir()
    {
        try { var d = File.ReadAllText(SettingsFile).Trim(); if (d.Length > 0) return d; } catch { }
        return DefaultDir;
    }

    /// The folder for display: shortened from the middle so both the drive and the last folder stay visible.
    string FolderText()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var t = outDir.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? "~\\" + outDir[home.Length..].TrimStart('\\') : outDir;
        return t.Length <= 40 ? t : t[..14] + "…" + t[^25..];
    }

    void PickFolder()
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = outDir, Description = "Folder for recordings", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        outDir = dlg.SelectedPath;
        try { Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!); File.WriteAllText(SettingsFile, outDir); } catch { }
        Relayout();
    }

    static string Clip(string text, int n) => text.Length <= n ? text : text[..(n - 1)].TrimEnd() + "…";

    // ---- recording ----

    async void Toggle()
    {
        if (busy) return;
        busy = true;
        Invalidate();
        try
        {
            if (session == null) await StartRecording();
            else await StopRecording();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "FastScreenRecorder", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { busy = false; Invalidate(); }
    }

    async void TogglePause()
    {
        if (busy || session == null) return;
        busy = true;
        Invalidate();
        try
        {
            if (session.Paused) { await session.Resume(); clock.Start(); }
            else { clock.Stop(); await session.Pause(); }
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "FastScreenRecorder", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally { busy = false; Relayout(); }
    }

    async Task StartRecording()
    {
        Directory.CreateDirectory(outDir);
        var file = Path.Combine(outDir, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.mp4");
        var sn = session = await Session.Start(source, cursor, sound, mic, file);
        sn.OnFailed(msg => BeginInvoke(() =>
        {
            if (session != sn) return;
            clock.Stop();
            Relayout();
            MessageBox.Show(this, msg, "FastScreenRecorder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (session == sn && !busy) Toggle(); // keep whatever was recorded
        }));
        clock.Restart();
        tick.Start();
        Relayout();
    }

    async Task StopRecording()
    {
        var sn = session!;
        tick.Stop();
        clock.Stop();
        bool ok = await sn.Finish();
        session = null;
        Relayout();
        if (ok) Process.Start("explorer.exe", $"/select,\"{sn.OutputFile}\"");
    }

    protected override async void OnFormClosing(FormClosingEventArgs e)
    {
        if (session != null)
        {
            e.Cancel = true;
            if (busy) return;
            busy = true;
            await session.Finish();
            session = null;
            Close();
            return;
        }
        Native.UnregisterHotKey(Handle, HotkeyId);
        base.OnFormClosing(e);
    }
}
