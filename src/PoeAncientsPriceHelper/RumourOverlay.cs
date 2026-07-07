using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PoeAncientsPriceHelper;

// Rating tiers, parsed from the leading token of a rating string ("B (see notes)", "S+(is a gamble)",
// "F(see notes)", "A+"). Anything after the tier (parenthetical notes) is ignored for colouring.
internal enum RatingTier { SPlus, S, APlus, A, BPlus, B, C, D, F, Unknown }

internal static class RumourRating
{
    public static RatingTier Tier(string? rating)
    {
        if (string.IsNullOrWhiteSpace(rating)) return RatingTier.Unknown;
        var s = rating.TrimStart();
        char letter = char.ToUpperInvariant(s[0]);
        bool plus = s.Length > 1 && s[1] == '+';
        return letter switch
        {
            'S' => plus ? RatingTier.SPlus : RatingTier.S,
            'A' => plus ? RatingTier.APlus : RatingTier.A,
            'B' => plus ? RatingTier.BPlus : RatingTier.B,
            'C' => RatingTier.C,
            'D' => RatingTier.D,
            'F' => RatingTier.F,
            _ => RatingTier.Unknown,
        };
    }

    // Tier colour ramp: S gold → A green → B light-blue → C/D grey → F red → unknown grey.
    public static Color Color(RatingTier tier) => tier switch
    {
        RatingTier.SPlus or RatingTier.S => System.Drawing.Color.Gold,
        RatingTier.APlus or RatingTier.A => System.Drawing.Color.FromArgb(80, 255, 120),
        RatingTier.BPlus or RatingTier.B => System.Drawing.Color.FromArgb(150, 200, 255),
        RatingTier.C => System.Drawing.Color.FromArgb(205, 205, 205),
        RatingTier.D => System.Drawing.Color.FromArgb(170, 170, 170),
        RatingTier.F => System.Drawing.Color.FromArgb(255, 95, 95),
        _ => System.Drawing.Color.Gray,
    };
}

// Adaptive placement maths (pure, so it's unit-tested). A panel on the right half of the screen gets
// the overlay to its LEFT, and vice versa, so the overlay never covers the panel and stays on-screen.
internal static class RumourOverlayLayout
{
    public static bool PlaceLeftOfPanel(Rectangle panel, Rectangle screen)
        => panel.Left + panel.Width / 2 > screen.Left + screen.Width / 2;

    // Top-left position for an overlay of `size` next to `panel`, clamped inside `screen`.
    public static Point Position(Rectangle panel, Size size, Rectangle screen, int gap)
    {
        int x = PlaceLeftOfPanel(panel, screen)
            ? panel.Left - gap - size.Width
            : panel.Right + gap;
        int y = panel.Top;
        x = Math.Max(screen.Left, Math.Min(x, screen.Right - size.Width));
        y = Math.Max(screen.Top, Math.Min(y, screen.Bottom - size.Height));
        return new Point(x, y);
    }
}

// Click-through, per-pixel-alpha layered overlay that lists rumour ratings next to the in-game panel.
// Shares the layered-window approach with PriceOverlayForm (the scene is composed in absolute physical
// pixels and pushed via UpdateLayeredWindow; WS_EX_TRANSPARENT makes it click-through).
internal sealed class RumourOverlayForm : Form
{
    private IReadOnlyList<RumourResultRow> _rows = [];
    private Rectangle _panelBounds;
    private string _lastSig = "";                 // last rendered signature — skip repaint when unchanged (#45)
    private const int BoundsJitterPx = 12;        // detected-bounds moves under this are treated as "same"
    private readonly Rectangle _screenBounds;
    private readonly Font _nameFont = new("Segoe UI", 14, FontStyle.Bold);
    private readonly Font _detailFont = new("Segoe UI", 12, FontStyle.Regular);
    private readonly Font _ratingFont = new("Segoe UI", 15, FontStyle.Bold);
    private readonly Font _headerFont = new("Segoe UI", 10, FontStyle.Bold);
    private Bitmap? _buffer;

    private static readonly string[] Headers = ["Rumour", "Map", "Mods", "Rating"];

    private const int Gap = 16;       // gap between panel and overlay
    private const int PadX = 12;
    private const int PadY = 10;
    private const int RowGap = 6;
    private const int ColGap = 16;    // gap between columns
    private const int HeaderGap = 8;  // gap below the header row / separator

    public RumourOverlayForm(Rectangle screenBounds)
    {
        _screenBounds = screenBounds;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        Bounds = screenBounds;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_LAYERED = 0x00080000;
            const int WS_EX_TRANSPARENT = 0x00000020;
            const int WS_EX_NOACTIVATE = 0x08000000;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    public void ShowResult(IReadOnlyList<RumourResultRow> rows, Rectangle panelBounds)
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => ShowResult(rows, panelBounds)); return; }

        // Ignore sub-threshold jitter in the detected panel bounds so the overlay doesn't wobble every
        // scan pass (the OCR-detected bounds shift a few px between reads). (#45)
        if (!Visible || RectShift(_panelBounds, panelBounds) > BoundsJitterPx)
            _panelBounds = panelBounds;
        _rows = rows;

        bool wasVisible = Visible;
        if (!wasVisible) Show();
        ForceTopmost();   // keep on top every pass — cheap, no repaint

        // Skip the (cross-thread, layered-window) repaint when what the user sees hasn't changed. The
        // loop re-resolves the panel every ~1-2s, but a panel's rumours don't move while it's open, so
        // re-rendering identical content just made the overlay flicker and hard to read. (#45)
        var sig = ResultSignature(_rows, _panelBounds);
        if (wasVisible && sig == _lastSig) return;
        _lastSig = sig;
        RenderLayered();
    }

    // Largest edge displacement between two rects — used to treat tiny detected-bounds jitter as "same".
    private static int RectShift(Rectangle a, Rectangle b) =>
        Math.Max(Math.Max(Math.Abs(a.Left - b.Left), Math.Abs(a.Top - b.Top)),
                 Math.Max(Math.Abs(a.Right - b.Right), Math.Abs(a.Bottom - b.Bottom)));

    // A string that captures everything the render depends on — the resolved (displayed) rumour data and
    // the stabilised panel bounds — so an unchanged pass can skip the repaint. Under --debug the raw OCR
    // read is included, so debug sessions still repaint as reads jitter.
    private static string ResultSignature(IReadOnlyList<RumourResultRow> rows, Rectangle bounds)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(bounds).Append(';');
        foreach (var r in rows)
        {
            sb.Append(r.Entry is { } e ? $"{e.Rumor}|{e.MapType}|{e.Mods}|{e.Rating}" : "unknown");
            if (App.DebugMode) sb.Append('<').Append(r.OcrName);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    public void HideNow()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(HideNow); return; }
        _rows = [];
        _lastSig = "";   // force a fresh render when the overlay next appears
        if (Visible) Hide();
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }
    protected override void OnShown(EventArgs e) { base.OnShown(e); ForceTopmost(); RenderLayered(); }

    private void RenderLayered()
    {
        if (!IsHandleCreated || IsDisposed || !Visible) return;
        int w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        if (_buffer is null || _buffer.Width != w || _buffer.Height != h)
        {
            _buffer?.Dispose();
            _buffer = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        }
        using (var g = Graphics.FromImage(_buffer))
        {
            g.Clear(Color.FromArgb(0));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            PaintScene(g);
        }

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero, oldBitmap = IntPtr.Zero;
        try
        {
            hBitmap = _buffer.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memDc, hBitmap);
            var size = new SIZE { cx = w, cy = h };
            var src = new POINT { x = 0, y = 0 };
            var dst = new POINT { x = Bounds.Left, y = Bounds.Top };
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA,
            };
            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void PaintScene(Graphics g)
    {
        // Geometry is in absolute screen coords; the layered bitmap is form-local and the form may sit
        // on a non-primary monitor, so shift the scene by the form origin.
        g.TranslateTransform(-Bounds.Left, -Bounds.Top);
        if (_rows.Count == 0) return;

        // Build the four display columns per row. Show the MATCHED rumour name (what the program
        // resolved to) so the map/mods/rating can be sanity-checked against the real rumour; the raw OCR
        // read is only surfaced under --debug.
        var rows = new List<(string Name, string Map, string Mods, string Rating, Color RatingColor)>(_rows.Count);
        foreach (var row in _rows)
        {
            if (row.Entry is { } e)
                rows.Add((
                    App.DebugMode ? $"{e.Rumor}  <- {row.OcrName}" : e.Rumor,
                    e.MapType, e.Mods, e.Rating,
                    RumourRating.Color(RumourRating.Tier(e.Rating))));
            else
                rows.Add((
                    App.DebugMode ? $"unknown  <- {row.OcrName}" : "unknown rumour",
                    "", "", "?",
                    RumourRating.Color(RatingTier.Unknown)));
        }

        // Column widths = max of the header label and every row's cell.
        int nameW = Measure(g, Headers[0], _headerFont);
        int mapW = Measure(g, Headers[1], _headerFont);
        int modsW = Measure(g, Headers[2], _headerFont);
        int ratingW = Measure(g, Headers[3], _headerFont);
        foreach (var (name, map, mods, rating, _) in rows)
        {
            nameW = Math.Max(nameW, Measure(g, name, _nameFont));
            mapW = Math.Max(mapW, Measure(g, map, _detailFont));
            modsW = Math.Max(modsW, Measure(g, mods, _detailFont));
            ratingW = Math.Max(ratingW, Measure(g, rating, _ratingFont));
        }

        int rowH = Math.Max(_nameFont.Height, _ratingFont.Height);
        int headerH = _headerFont.Height;
        int blockW = PadX * 2 + nameW + ColGap + mapW + ColGap + modsW + ColGap + ratingW;
        int blockH = PadY * 2 + headerH + HeaderGap + rows.Count * rowH + (rows.Count - 1) * RowGap;

        var pos = RumourOverlayLayout.Position(_panelBounds, new Size(blockW, blockH), _screenBounds, Gap);
        var block = new Rectangle(pos.X, pos.Y, blockW, blockH);

        // Backdrop: rounded, semi-transparent slate plate (premultiplied alpha for the layered window).
        using (var path = RoundedRect(block, 8))
        using (var bg = new SolidBrush(Premultiply(Color.FromArgb(205, 28, 30, 36))))
        using (var border = new Pen(Color.FromArgb(120, 90, 95, 110), 1))
        {
            g.FillPath(bg, path);
            g.DrawPath(border, path);
        }

        int nameX = block.Left + PadX;
        int mapX = nameX + nameW + ColGap;
        int modsX = mapX + mapW + ColGap;
        int ratingX = modsX + modsW + ColGap;

        // Header row + separator line.
        int y = block.Top + PadY;
        using (var headerBrush = new SolidBrush(Color.FromArgb(150, 155, 165)))
        {
            g.DrawString(Headers[0], _headerFont, headerBrush, nameX, y);
            g.DrawString(Headers[1], _headerFont, headerBrush, mapX, y);
            g.DrawString(Headers[2], _headerFont, headerBrush, modsX, y);
            g.DrawString(Headers[3], _headerFont, headerBrush, ratingX, y);
        }
        int sepY = y + headerH + HeaderGap / 2;
        using (var sepPen = new Pen(Color.FromArgb(90, 95, 110), 1))
            g.DrawLine(sepPen, block.Left + PadX, sepY, block.Right - PadX, sepY);

        y += headerH + HeaderGap;
        using var nameBrush = new SolidBrush(Color.White);
        using var detailBrush = new SolidBrush(Color.FromArgb(195, 195, 200));
        int dy = (_nameFont.Height - _detailFont.Height) / 2;
        foreach (var (name, map, mods, rating, ratingColor) in rows)
        {
            g.DrawString(name, _nameFont, nameBrush, nameX, y);
            g.DrawString(map, _detailFont, detailBrush, mapX, y + dy);
            g.DrawString(mods, _detailFont, detailBrush, modsX, y + dy);
            using (var ratingBrush = new SolidBrush(ratingColor))
                g.DrawString(rating, _ratingFont, ratingBrush, ratingX, y);
            y += rowH + RowGap;
        }
    }

    private static int Measure(Graphics g, string s, Font f) =>
        (int)Math.Ceiling(g.MeasureString(s, f).Width);

    private static Color Premultiply(Color c) =>
        Color.FromArgb(c.A, c.R * c.A / 255, c.G * c.A / 255, c.B * c.A / 255);

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void ForceTopmost()
    {
        if (IsDisposed || !IsHandleCreated || !Visible) return;
        if (InvokeRequired) { BeginInvoke(ForceTopmost); return; }
        SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0002 | 0x0001 | 0x0010);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _nameFont.Dispose(); _detailFont.Dispose(); _ratingFont.Dispose(); _headerFont.Dispose(); _buffer?.Dispose(); }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    private const int ULW_ALPHA = 0x02;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hDC);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);
    [DllImport("user32.dll")] private static extern bool UpdateLayeredWindow(
        IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc,
        ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
}

// Hosts the rumour overlay on its own STA thread, mirroring PriceOverlayManager. The window is pinned
// Per-Monitor-V2 before its handle is created so the layered bitmap stays physical-pixel (see #21).
internal static class RumourOverlayManager
{
    private static RumourOverlayForm? _form;
    private static Thread? _thread;
    private static readonly object _lock = new();

    public static void Show(IReadOnlyList<RumourResultRow> rows, Rectangle panelBounds)
    {
        EnsureForm(panelBounds);
        WithForm(f => f.ShowResult(rows, panelBounds));
    }

    public static void HideNow() => WithForm(f => f.HideNow());

    public static void Close()
    {
        WithForm(f => f.Invoke(() => { if (!f.IsDisposed) f.Close(); }));
    }

    private static void EnsureForm(Rectangle panelBounds)
    {
        lock (_lock)
        {
            if (_form is not null && !_form.IsDisposed) return;

            using var ready = new ManualResetEventSlim(false);
            _thread = new Thread(() =>
            {
                SetThreadDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
                // Host on the monitor that contains the detected panel so the layered bitmap stays one
                // monitor small and the overlay lands on the screen PoE is on.
                var screen = Screen.FromRectangle(panelBounds).Bounds;
                var f = new RumourOverlayForm(screen);
                f.Shown += (_, _) => ready.Set();
                _form = f;
                System.Windows.Forms.Application.Run(f);
                lock (_lock) _form = null;
            }) { IsBackground = true, Name = "RumourOverlay-STA" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait(TimeSpan.FromSeconds(2));
        }
    }

    private static void WithForm(Action<RumourOverlayForm> action)
    {
        RumourOverlayForm? f;
        lock (_lock) { f = _form; }
        if (f is null || f.IsDisposed) return;
        try { action(f); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr dpiContext);
}
