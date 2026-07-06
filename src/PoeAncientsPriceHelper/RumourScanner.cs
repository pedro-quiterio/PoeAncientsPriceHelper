using System.Drawing;

namespace PoeAncientsPriceHelper;

// One resolved rumour row: the OCR'd name and the matched data (null when the name didn't resolve,
// so the overlay shows "?").
internal sealed record RumourResultRow(string OcrName, RumourEntry? Entry)
{
    public bool Matched => Entry is not null;
}

// The outcome of one rumour read: the resolved rows (in the panel's top-to-bottom order) plus the
// detected panel bounds, which the overlay uses to place itself on the opposite side.
internal sealed record RumourReadResult(IReadOnlyList<RumourResultRow> Rows, Rectangle PanelBounds);

// One-shot rumour read: capture a screen region, OCR it full-frame, detect the panel, and resolve each
// rumour name. The capture/OCR are injected; the detection + resolution (BuildResult) is static so it
// can be exercised without a live screen.
internal sealed class RumourScanner
{
    private readonly IScreenCaptureBackend _capture;
    private readonly OcrScanner _ocr;
    // Swappable so a sheet refresh (#37) takes effect live; volatile for the cross-thread read by the
    // background loop.
    private volatile RumourRepository _rumours;
    // Serialises capture+OCR so the auto-detect loop (#35) and the debug trigger (#34) can't drive the
    // shared capture backend concurrently.
    private readonly object _gate = new();

    public RumourScanner(IScreenCaptureBackend capture, OcrScanner ocr, RumourRepository rumours)
    {
        _capture = capture;
        _ocr = ocr;
        _rumours = rumours;
    }

    // Swap in a refreshed dataset (from a sheet refresh) without restarting the scan loop.
    public void UseData(RumourRepository rumours) => _rumours = rumours;

    // Capture `region`, OCR it, and return the lines with bounds shifted into absolute screen coords
    // (so an overlay can be placed against them). Used both for the small WORLD-gate region and the
    // full screen.
    public IReadOnlyList<OcrTextLine> CaptureLines(Rectangle region, int upscale = 1)
    {
        lock (_gate)
        {
            using var bmp = _capture.CaptureRegion(region);
            var lines = _ocr.RecognizeLines(bmp, upscale: upscale);
            return lines
                .Select(l => l with { Bounds = Offset(l.Bounds, region.Location) })
                .ToList();
        }
    }

    // Capture `screen`, detect the panel, and resolve each rumour. Returns null when no panel is found.
    public RumourReadResult? ReadOnce(Rectangle screen) => BuildResult(CaptureLines(screen), _rumours);

    // Pure detect + resolve over absolute-coord OCR lines. Returns null when no panel is detected.
    public static RumourReadResult? BuildResult(IReadOnlyList<OcrTextLine> lines, RumourRepository rumours)
    {
        var panel = RumourPanelDetector.Detect(lines);
        if (panel is null) return null;
        var rows = panel.RumourLines
            .Select(l => new RumourResultRow(l.Text.Trim(), rumours.Resolve(l.Text)))
            .ToList();
        return new RumourReadResult(rows, panel.PanelBounds);
    }

    private static Rectangle Offset(Rectangle r, Point p) => new(r.X + p.X, r.Y + p.Y, r.Width, r.Height);
}
