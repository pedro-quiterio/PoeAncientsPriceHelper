using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace PoeAncientsPriceHelper;

internal sealed record OcrRow(string NormalizedName, string RawText, int CenterY,
                              int Multiplier = 1, bool MultiplierExplicit = false);

// A single OCR'd line with its bounding box, in the supplied bitmap's pixel coordinates. Used by the
// rumour helper, which needs raw lines + positions across a whole frame rather than the price-panel
// row extraction Scan performs.
internal sealed record OcrTextLine(string Text, System.Drawing.Rectangle Bounds);

internal sealed class OcrScanner
{
    private readonly OcrEngine _engine;
    private readonly Action<string>? _log;
    private readonly bool _debug;
    private const int UpscaleFactor = 3;
    private const int MinNameLength = 4;
    // A real row must contain a word at least this long. 4 (not 5) so two-short-word names
    // like "Void Flux" survive; OCR fragments are still mostly 1–3 char tokens.
    private const int MinWordLength = 4;

    // Pre-compiled regexes for StripLeadingNoise / ExtractMultiplier — these run on every OCR'd
    // line (~every 100ms while a panel is open), so avoiding the per-call recompile is a meaningful
    // saving on the hot path. (NormalizeName's regexes live in NameNormalizer.)
    // The 'x' may be glued straight onto the name when OCR drops the space ("6xArcanist's Etcher"),
    // so the marker is allowed to be followed by a letter — only a trailing DIGIT is rejected (that
    // would be an ambiguous "6x5", not a stack marker).
    private static readonly Regex MultiplierPattern = new(@"(?<![a-z0-9])(\d{1,3})\s*x(?![0-9])", RegexOptions.Compiled);
    private static readonly Regex LeadingNoise = new(@"^(?:\S{1,2}\s+|\S*\d\S*\s+)+", RegexOptions.Compiled);
    private static readonly Regex QuantityMarker = new(@"(?<!\w)\d+\s*x\s+", RegexOptions.Compiled);
    // A stack marker at the very start, possibly glued to the name ("6xarcanist s etcher"). Stripped
    // BEFORE LeadingNoise, whose digit-token rule would otherwise swallow "6xarcanist" whole and
    // destroy the item name. Mirrors MultiplierPattern's "letter ok, trailing digit not" boundary.
    private static readonly Regex LeadingQuantity = new(@"^\s*\d{1,3}\s*x(?![0-9])", RegexOptions.Compiled);
    // \p{L} (any-script letter), not [a-z]: an ASCII-only class would treat every Cyrillic/Greek
    // char as "non-alpha" and strip a whole non-Latin name to "" → REJ:short (#39). Leading digits
    // and punctuation are still removed (digits are \p{N}); a leading accented Latin letter survives too.
    private static readonly Regex LeadingNonAlpha = new(@"^[^\p{L}]+", RegexOptions.Compiled);

    // debug gates the diagnostic debug_ocr.png dump (see Scan) and CLI OCR-test raw-line logging.
    // App.DebugMode additionally enables raw-line logging for the live overlay when toggled at runtime.
    public OcrScanner(Action<string>? log = null, bool debug = false)
    {
        _engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"))
            ?? throw new InvalidOperationException("Windows OCR is not available. Install an English OCR language pack in Windows language settings.");
        _log = log;
        _debug = debug;
    }

    // Each row starts with ~3 cost-rune glyphs on the left, then "Nx ItemName". Cropping the
    // left IconColumnFraction removes the glyphs (which produce leading OCR garbage) while
    // keeping the quantity marker and the name. RightTrimFraction shaves the panel's right
    // border, which otherwise tacks stray characters onto the last word.
    // (internal so the overlay can draw a box matching exactly what is OCR'd.)
    internal const double IconColumnFraction = 0.30;
    internal const double RightTrimFraction = 0.02;

    public IReadOnlyList<OcrRow> Scan(Bitmap regionBitmap)
    {
        int leftCut = Math.Max(1, (int)(regionBitmap.Width * IconColumnFraction));
        int rightCut = (int)(regionBitmap.Width * RightTrimFraction);
        int cropW = Math.Max(1, regionBitmap.Width - leftCut - rightCut);
        using var cropped = CropBitmap(regionBitmap, leftCut, 0, cropW, regionBitmap.Height);
        using var preprocessed = Preprocess(cropped);
        int scale = GetSafeUpscaleFactor(preprocessed);
        using var upscaled = Upscale(preprocessed, scale);
        using var softwareBitmap = ToSoftwareBitmap(upscaled);
        int height = regionBitmap.Height;

        var result = _engine.RecognizeAsync(softwareBitmap).AsTask().GetAwaiter().GetResult();
        var rows = ExtractRows(result, height, scale);

        // When OCR catches few rows, dump the exact image fed to Windows OCR for inspection. Debug-only:
        // for end users this would be needless disk churn (~every 100ms while a panel mis-detects).
        if (_debug && rows.Count <= 2)
        {
            try { upscaled.Save(Path.Combine(AppPaths.DataDir, "debug_ocr.png"), System.Drawing.Imaging.ImageFormat.Png); }
            catch { /* best-effort diagnostic */ }
        }
        return rows;
    }

    // General full-frame OCR for the rumour helper: returns every recognised line with its bounding
    // box (in the supplied bitmap's pixel coords), instead of the price-panel row extraction Scan does.
    // Optionally inverts first (light-on-dark game text → dark-on-light reads more reliably), and
    // downscales when the frame exceeds the engine's MaxImageDimension, mapping the boxes back to
    // original coordinates so callers can position an overlay against them.
    public IReadOnlyList<OcrTextLine> RecognizeLines(Bitmap bmp, bool invert = true)
    {
        using var prepared = invert ? Preprocess(bmp) : null;
        var working = prepared ?? bmp;

        int maxDim = (int)OcrEngine.MaxImageDimension;
        double scale = 1.0;
        if (maxDim > 0 && Math.Max(working.Width, working.Height) > maxDim)
            scale = (double)maxDim / Math.Max(working.Width, working.Height);

        using var scaled = scale < 1.0 ? Resize(working, scale) : null;
        using var softwareBitmap = ToSoftwareBitmap(scaled ?? working);

        var result = _engine.RecognizeAsync(softwareBitmap).AsTask().GetAwaiter().GetResult();
        var lines = new List<OcrTextLine>(result.Lines.Count);
        foreach (var line in result.Lines)
        {
            if (string.IsNullOrWhiteSpace(line.Text) || line.Words.Count == 0) continue;
            var bounds = UnionBounds(line, scale);
            if (!bounds.IsEmpty) lines.Add(new OcrTextLine(line.Text.Trim(), bounds));
        }
        return lines;
    }

    // Union of a line's word bounding rects, scaled back to the original (pre-downscale) coordinates.
    private static Rectangle UnionBounds(OcrLine line, double scale)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        foreach (var w in line.Words)
        {
            var box = w.BoundingRect;
            l = Math.Min(l, box.X); t = Math.Min(t, box.Y);
            r = Math.Max(r, box.X + box.Width); b = Math.Max(b, box.Y + box.Height);
        }
        if (l > r || t > b) return Rectangle.Empty;
        return Rectangle.FromLTRB(
            (int)Math.Floor(l / scale), (int)Math.Floor(t / scale),
            (int)Math.Ceiling(r / scale), (int)Math.Ceiling(b / scale));
    }

    // Fractional downscale (Upscale only does integer factors), used to fit a full frame under
    // OcrEngine.MaxImageDimension.
    private static Bitmap Resize(Bitmap src, double scale)
    {
        int w = Math.Max(1, (int)Math.Round(src.Width * scale));
        int h = Math.Max(1, (int)Math.Round(src.Height * scale));
        var dst = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, w, h);
        return dst;
    }

    private static int GetSafeUpscaleFactor(Bitmap bitmap)
    {
        int maxDim = (int)OcrEngine.MaxImageDimension;
        if (maxDim <= 0) return UpscaleFactor;
        int byWidth = maxDim / Math.Max(1, bitmap.Width);
        int byHeight = maxDim / Math.Max(1, bitmap.Height);
        return Math.Max(1, Math.Min(UpscaleFactor, Math.Min(byWidth, byHeight)));
    }

    private static Bitmap CropBitmap(Bitmap src, int x, int y, int w, int h)
    {
        var dst = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.DrawImage(src, new Rectangle(0, 0, w, h), new Rectangle(x, y, w, h), GraphicsUnit.Pixel);
        return dst;
    }

    private IReadOnlyList<OcrRow> ExtractRows(OcrResult result, int bitmapHeight, int scale = 1)
    {
        var rows = new List<OcrRow>();
        List<string>? diag = ShouldLogOcrDiagnostics ? [] : null;

        foreach (var line in result.Lines)
        {
            var text = line.Text;
            string? reject = null;
            string normalized = "";
            int multiplier = 1;
            bool multiplierExplicit = false;
            int centerY = 0;

            if (string.IsNullOrWhiteSpace(text))
            {
                reject = "empty";
            }
            else if (line.Words.Count == 0)
            {
                reject = "nowords";
            }
            else
            {
                centerY = GetLineCenterY(line, bitmapHeight, scale);
                var normalizedRaw = NameNormalizer.Normalize(text);
                (multiplier, multiplierExplicit) = ExtractMultiplierWithConfidence(normalizedRaw);
                normalized = StripLeadingNoise(normalizedRaw);
                if (normalized.Length < MinNameLength) reject = "short";
                else if (!HasLongWord(normalized, MinWordLength)) reject = "noword";
            }

            if (reject is null)
                rows.Add(new OcrRow(normalized, text.Trim(), centerY, multiplier, multiplierExplicit));
            diag?.Add($"y={centerY} words={line.Words.Count} '{(text ?? "").Trim()}'{(reject is null ? "" : $" REJ:{reject}")}");
        }

        rows.Sort((x, y) => x.CenterY.CompareTo(y.CenterY));

        // Diagnostic: when few rows survive, show every line Windows OCR actually produced so we
        // can tell "OCR only saw 1 line" from "saw 5 but the filters dropped 4".
        if (rows.Count <= 2 && diag is { Count: > 0 })
            _log?.Invoke($"OCR raw {diag.Count} lines → " + string.Join(" | ", diag));

        return rows;
    }

    private static int GetLineCenterY(OcrLine line, int bitmapHeight, int scale)
    {
        double top = double.MaxValue;
        double bottom = double.MinValue;
        foreach (var word in line.Words)
        {
            var box = word.BoundingRect;
            top = Math.Min(top, box.Y);
            bottom = Math.Max(bottom, box.Y + box.Height);
        }
        if (top == double.MaxValue || bottom == double.MinValue) return 0;
        return Math.Clamp((int)Math.Round((top + bottom) / 2.0 / scale), 0, bitmapHeight - 1);
    }

    private static Bitmap Upscale(Bitmap src, int factor)
    {
        var dst = new Bitmap(src.Width * factor, src.Height * factor, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, dst.Width, dst.Height);
        return dst;
    }

    // The list shows a stack quantity as "Nx" before the item name ("1x", "2x", "14x").
    // Capture it so the price can be multiplied by the stack size. Read from the raw
    // normalized string BEFORE StripLeadingNoise removes the marker. Returns 1 when absent.
    internal static int ExtractMultiplier(string normalized) =>
        ExtractMultiplierWithConfidence(normalized).Multiplier;

    // Same parse, but also reports whether an explicit "Nx" marker was actually read (Explicit=true)
    // versus the absent-marker fallback to 1 (Explicit=false). The caller uses that to tell a real
    // stack from an assumed single, so a pass where OCR drops the marker doesn't flip a known stack
    // back to a unit price. (See ScanEngine quantity memory.)
    internal static (int Multiplier, bool Explicit) ExtractMultiplierWithConfidence(string normalized)
    {
        var m = MultiplierPattern.Match(normalized);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var n) && n >= 1)
            return (Math.Min(n, 999), true);
        return (1, false);
    }

    // Strip leading noise: a glued stack marker ("6xarcanist…"), then short/numeric tokens ("e",
    // "l8"), then anything before the first quantity marker ("1x", "11x"), then remaining leading
    // non-alpha chars.
    // e.g. "6x arcanist s etcher" / "6xarcanist s etcher" → "arcanist s etcher"
    // e.g. "krogin 1x ancient rune of decay"             → "ancient rune of decay"
    // e.g. "e l8 n 1x the greatwolf"                      → "the greatwolf"
    internal static string StripLeadingNoise(string normalized)
    {
        // Remove a leading "Nx" first — even when OCR glued it to the name. LeadingNoise's digit-token
        // rule would otherwise eat the whole "6xarcanist" token and leave only "s etcher".
        var s = LeadingQuantity.Replace(normalized, "");
        s = LeadingNoise.Replace(s, "");
        // If a quantity marker still exists, drop everything before (and including) it
        var qm = QuantityMarker.Match(s);
        if (qm.Success) s = s.Substring(qm.Index + qm.Length);
        s = LeadingNonAlpha.Replace(s, "");
        return s.Trim();
    }

    private static bool HasLongWord(string normalized, int minLen)
    {
        int run = 0;
        foreach (char c in normalized)
        {
            if (char.IsLetter(c)) { if (++run >= minLen) return true; }
            else run = 0;
        }
        return false;
    }

    // PoE list panel has light text on a textured dark background. Feed Windows OCR dark text on
    // a light background, which is the shape most OCR engines handle more consistently.
    private static Bitmap Preprocess(Bitmap src)
    {
        var dst = new Bitmap(src.Width, src.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(dst);
        g.DrawImage(src, 0, 0);
        InvertBitmap(dst);
        return dst;
    }

    private static void InvertBitmap(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        try
        {
            int len = data.Stride * bmp.Height;
            var buf = new byte[len];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, len);
            for (int i = 0; i < buf.Length; i++) buf[i] = (byte)(255 - buf[i]);
            System.Runtime.InteropServices.Marshal.Copy(buf, 0, data.Scan0, len);
        }
        finally { bmp.UnlockBits(data); }
    }

    private static SoftwareBitmap ToSoftwareBitmap(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Bmp);
        ms.Position = 0;
        using var stream = ms.AsRandomAccessStream();
        var decoder = BitmapDecoder.CreateAsync(stream).AsTask().GetAwaiter().GetResult();
        return decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)
            .AsTask().GetAwaiter().GetResult();
    }

    private bool ShouldLogOcrDiagnostics => _log is not null && (_debug || App.DebugMode);
}
