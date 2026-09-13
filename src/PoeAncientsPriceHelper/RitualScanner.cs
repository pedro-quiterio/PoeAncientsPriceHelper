using System.Drawing;

namespace PoeAncientsPriceHelper;

// Reads the small ritual-counter region: capture it, OCR the pixels, and hand back the recognised text
// as one line for RitualCounter to parse. The region is tiny (just the "N/M" badge), so this is cheap
// enough to run every ~500ms. Capture+OCR are serialised so it can share a capture backend with the
// other loops without driving it concurrently (mirrors RumourScanner's gate).
internal sealed class RitualScanner
{
    private readonly IScreenCaptureBackend _capture;
    private readonly OcrScanner _ocr;
    private readonly object _gate = new();

    public RitualScanner(IScreenCaptureBackend capture, OcrScanner ocr)
    {
        _capture = capture;
        _ocr = ocr;
    }

    // Capture `region` and return every recognised line joined into one string. `upscale` enlarges the
    // small counter glyphs before OCR (the ritual badge is only ~20-30px tall, like the WORLD gate).
    public string ReadText(Rectangle region, int upscale)
    {
        lock (_gate)
        {
            using var bmp = _capture.CaptureRegion(region);
            var lines = _ocr.RecognizeLines(bmp, upscale: upscale);
            return string.Join(" ", lines.Select(l => l.Text));
        }
    }
}
