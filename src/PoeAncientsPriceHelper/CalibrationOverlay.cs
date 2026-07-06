using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PoeAncientsPriceHelper;

// Full-screen drag-to-select overlay spanning the whole virtual desktop. User draws a rectangle over
// the list panel; RegionRectResult is returned in ABSOLUTE PHYSICAL screen pixels.
//
// All selection geometry is tracked via Cursor.Position (absolute physical coords) rather than the
// form's client coordinates. The form spans monitors that can have different DPIs, and a single window
// has one DPI context, so client coords are unreliable across the DPI boundary — but the global cursor
// position is consistent physical-desktop space (the process is Per-Monitor-V2 aware via app.manifest).
// Painting converts those physical coords back to client space for display (RectangleToClient).
internal sealed class CalibrationOverlay : Form
{
    public Rectangle RegionRectResult { get; private set; }

    private Point? _dragStartScreen;        // absolute physical screen coords
    private Rectangle _currentDragScreen;   // absolute physical screen coords
    private Rectangle _confirmedRectScreen; // absolute physical screen coords
    private readonly Bitmap _screenSnapshot;
    private readonly string _instruction;

    private const string DefaultInstruction =
        "Drag a box around the item list panel, then press ENTER to confirm. ESC to cancel.";

    public CalibrationOverlay(string? instruction = null)
    {
        _instruction = instruction ?? DefaultInstruction;
        FormBorderStyle = FormBorderStyle.None;
        TopMost = true;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Opacity = 0.4;
        DoubleBuffered = true;
        KeyPreview = true;
        Cursor = Cursors.Cross;
        Text = "PoeAncientsPriceHelper - Calibration";

        // Span the whole virtual desktop (all monitors) so the region can be drawn on any of them, not
        // just the primary (#3). On multi-monitor setups VirtualScreen.Location can be negative (a
        // monitor left/above the primary); mouse points are form-client coords, so the selected rect is
        // offset by Bounds.Location back into absolute screen coords in OnMouseUp. WindowState.Maximized
        // would only fill one monitor, so it's gone — Bounds drives the size instead.
        var bounds = SystemInformation.VirtualScreen;
        Bounds = bounds;

        _screenSnapshot = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(_screenSnapshot);
        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _screenSnapshot.Dispose();
        base.Dispose(disposing);
    }

    // Force this overlay to the foreground as soon as it's shown (issue #14).
    //
    // The calibrate hotkey is a GLOBAL hook: when it fires, the foreground window belongs to the game
    // (or whatever the user is in), so OUR process is in the background. A background process can't
    // simply SetForegroundWindow — Windows' foreground lock just flashes the taskbar — so without this
    // the overlay opens BEHIND the game, the user can never press ENTER/ESC to close it, and
    // RunOnStaThread's thread.Join() blocks the WPF UI thread forever → the whole app freezes and has
    // to be killed from Task Manager. (The Start/Stop *button* path doesn't hit this because the WPF
    // window already owns the foreground when it's clicked, which is why the button "works".)
    //
    // Attaching our input queue to the current foreground thread lifts the foreground lock just long
    // enough to legitimately take focus.
    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        TopMost = true;

        IntPtr foreground = GetForegroundWindow();
        uint foreThread = GetWindowThreadProcessId(foreground, out _);
        uint thisThread = GetCurrentThreadId();
        bool attached = foreThread != 0 && foreThread != thisThread &&
                        AttachThreadInput(foreThread, thisThread, true);
        try
        {
            Activate();
            BringToFront();
            SetForegroundWindow(Handle);
            Focus();
        }
        finally
        {
            if (attached) AttachThreadInput(foreThread, thisThread, false);
        }
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        { _dragStartScreen = Cursor.Position; _currentDragScreen = Rectangle.Empty; }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragStartScreen is { } s)
        {
            var p = Cursor.Position;   // absolute physical screen coords
            _currentDragScreen = Rectangle.FromLTRB(
                Math.Min(s.X, p.X), Math.Min(s.Y, p.Y),
                Math.Max(s.X, p.X), Math.Max(s.Y, p.Y));
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_dragStartScreen is null || _currentDragScreen.Width < 3 || _currentDragScreen.Height < 3)
        { _dragStartScreen = null; _currentDragScreen = Rectangle.Empty; Invalidate(); return; }
        _dragStartScreen = null;
        _confirmedRectScreen = _currentDragScreen;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); return; }
        if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && _confirmedRectScreen.Width > 0)
        {
            // Already absolute physical screen coords (captured from Cursor.Position) — exactly what
            // ScreenCapture/overlay positioning expect under Per-Monitor-V2 awareness.
            RegionRectResult = _confirmedRectScreen;
            DialogResult = DialogResult.OK;
            Close();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        using var titleFont = new Font("Segoe UI", 18, FontStyle.Bold);
        using var subFont = new Font("Segoe UI", 12, FontStyle.Regular);
        using var fg = new SolidBrush(Color.White);

        // Pin the instructions to the PRIMARY monitor (mapped from physical → client) so they never land
        // on whichever monitor happens to sit at the virtual-desktop origin (the old bug: text on the
        // secondary). The selection rects are stored in physical coords, so convert them for drawing.
        var primary = Screen.PrimaryScreen!.Bounds;
        var titleAt = PointToClient(new Point(primary.Left + 30, primary.Top + 30));
        g.DrawString(_instruction, titleFont, fg, titleAt.X, titleAt.Y);

        if (_currentDragScreen.Width > 0)
        {
            using var pen = new Pen(Color.OrangeRed, 2) { DashStyle = System.Drawing.Drawing2D.DashStyle.Dash };
            g.DrawRectangle(pen, RectangleToClient(_currentDragScreen));
        }
        if (_confirmedRectScreen.Width > 0)
        {
            var clientRect = RectangleToClient(_confirmedRectScreen);
            using var pen2 = new Pen(Color.LimeGreen, 3);
            g.DrawRectangle(pen2, clientRect);
            g.DrawString("Press ENTER to confirm, drag to redo", subFont, fg,
                clientRect.Left, clientRect.Bottom + 6);
        }
    }

    public static Rectangle? RunOnStaThread(string? instruction = null)
    {
        Rectangle? result = null;
        var thread = new Thread(() =>
        {
            // DPI awareness is set process-wide via app.manifest (Per-Monitor-V2). The old
            // Application.SetHighDpiMode(PerMonitorV2) call here was a no-op — the WPF process had
            // already locked its DPI mode by the time calibration runs.
            using var form = new CalibrationOverlay(instruction);
            if (form.ShowDialog() == DialogResult.OK)
                result = form.RegionRectResult;
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return result;
    }
}
