namespace PoeAncientsPriceHelper;

// Scroll tracking for the reward panel (experimental, opt-in via AppConfig.ScrollTrackingEnabled).
// Contributed by @Zatyp-Tema (#68). It follows the panel's vertical scroll between OCR passes so
// priced rows move with the list and offscreen rows are remembered without re-OCR, instead of the
// legacy fixed-position slot locking. This file is deliberately free of any Bitmap/Windows dependency
// so the exact matcher can be unit-tested against synthetic frames.

// A small, horizontally differentiated image. No bitmap/Windows dependency: the exact matcher
// used in the app can also be replayed against recordings in the test harness.
internal sealed record ScrollFrame(int Width, int Height, int Scale, short[] Pixels)
{
    public double Difference(ScrollFrame other, int shift, int step = 3)
    {
        int top = Math.Max(Height / 6, -shift + other.Height / 6);
        int bottom = Math.Min(Height * 5 / 6, other.Height * 5 / 6 - shift);
        if (bottom - top < Height / 4) return double.MaxValue;
        long error = 0; int count = 0;
        for (int y = top; y < bottom; y += step)
            for (int x = 1; x < Width - 1; x += 2)
            {
                error += Math.Abs(Pixels[y * Width + x] - other.Pixels[(y + shift) * Width + x]);
                count++;
            }
        return count == 0 ? double.MaxValue : (double)error / count;
    }

    public short[]? Fingerprint(int pixelY)
    {
        int y = pixelY / Scale;
        const int radius = 5;
        if (y < radius || y + radius >= Height) return null;
        var result = new short[(radius * 2 + 1) * Width];
        Array.Copy(Pixels, (y - radius) * Width, result, 0, result.Length);
        return result;
    }

    public int? Locate(short[] signature, int pixelY)
    {
        // Text can land between physical pixels while the game animates a scroll. Refine locally
        // rather than accumulating those 1px rounding errors or hiding a correctly tracked row.
        double best = 15; int? found = null;
        for (int delta = -6; delta <= 6; delta++)
        {
            int start = (pixelY + delta) / Scale - 5;
            if (start < 0 || start + 11 > Height || signature.Length != Width * 11) continue;
            long error = 0;
            for (int i = 0; i < signature.Length; i++) error += Math.Abs(Pixels[start * Width + i] - signature[i]);
            double mean = (double)error / signature.Length;
            if (mean < best) { best = mean; found = pixelY + delta; }
        }
        return found;
    }

}

internal static class VerticalScrollMatcher
{
    // Positive means content moved DOWN on screen (the logical viewport offset decreases).
    public static (bool Reliable, int Pixels, double Error) Match(ScrollFrame before, ScrollFrame after)
    {
        if (before.Width != after.Width || before.Height != after.Height) return (false, 0, double.MaxValue);
        double stationary = before.Difference(after, 0);
        if (stationary < 1.8) return (true, 0, stationary);
        int range = before.Height / 2;
        double best = double.MaxValue; int bestShift = 0;
        var scores = new List<(int Shift, double Error)>();
        for (int dy = -range; dy <= range; dy++)
        {
            double error = before.Difference(after, dy);
            scores.Add((dy, error));
            if (error < best) { best = error; bestShift = dy; }
        }
        int coarse = bestShift;
        for (int dy = coarse - 2; dy <= coarse + 2; dy++)
        {
            double error = before.Difference(after, dy, 1);
            if (error < best) { best = error; bestShift = dy; }
        }
        double competitor = scores.Where(s => Math.Abs(s.Shift - bestShift) > 4)
            .Select(s => s.Error).DefaultIfEmpty(double.MaxValue).Min();
        bool reliable = best < 12 && (best < 2.5 || competitor > best * 1.2 + 0.5);
        return (reliable, bestShift * before.Scale, best);
    }
}

internal sealed class ScrollTrackingSession
{
    private sealed class Entry
    {
        public required int DocumentY;
        public required PriceRow Row;
        public required short[] Signature;
        public string PendingName = "";
        public int Confirmations;
        public bool Locked;
    }
    private readonly List<Entry> _entries = [];
    private ScrollFrame? _reference;
    private ScrollFrame? _current;
    private int _failures;
    private int _referenceOffset;
    public int Offset { get; private set; }
    public int Epoch { get; private set; }
    public bool Aligned { get; private set; }
    public int CachedRows => _entries.Count;
    public bool HasPrices => _entries.Any(e => e.Locked);

    public void Reset()
    {
        _entries.Clear(); _reference = null; _current = null;
        Offset = 0; _referenceOffset = 0; Aligned = false; _failures = 0; Epoch++;
    }

    public void Observe(ScrollFrame frame)
    {
        var previous = _current;
        _current = frame;
        if (Aligned && previous is not null && previous.Difference(frame, 0) < 0.6) return;
        if (_reference is null) { _reference = frame; Aligned = true; return; }
        var motion = VerticalScrollMatcher.Match(_reference, frame);
        if (!motion.Reliable)
        {
            Aligned = false;
            // Keep the last reliable frame briefly for recovery after hover/tooltips. If the image
            // cannot be aligned, start a new coordinate system and invalidate in-flight OCR.
            if (++_failures >= 15) { Reset(); _reference = _current = frame; Aligned = true; }
            return;
        }
        Offset = _referenceOffset - motion.Pixels;
        // Register against a stable keyframe instead of adding rounded per-frame deltas. Rebase
        // before the common viewport overlap becomes too small for a reliable match.
        if (Math.Abs(motion.Pixels) > frame.Height * frame.Scale / 6)
        {
            _reference = frame; _referenceOffset = Offset;
        }
        _failures = 0; Aligned = true;
    }

    public void Accept(IReadOnlyList<PriceRow> reads, ScrollFrame source, int sourceOffset, int sourceEpoch)
    {
        if (sourceEpoch != Epoch) return;
        foreach (var read in reads.Where(r => r.HasPrice))
        {
            var signature = source.Fingerprint(read.CenterY);
            if (signature is null) continue;
            int documentY = read.CenterY + sourceOffset;
            var entry = _entries.OrderBy(e => Math.Abs(e.DocumentY - documentY))
                .FirstOrDefault(e => Math.Abs(e.DocumentY - documentY) <= 18);
            if (entry is null)
            {
                entry = new Entry { DocumentY = documentY, Row = read, Signature = signature };
                _entries.Add(entry);
            }
            // The position AND local pixels identify a row, so repeated currencies at different
            // positions keep independent quantities. A new item at this position replaces the old.
            bool same = entry.Row.Name == read.Name && entry.Locked;
            int quantity = read.MultiplierExplicit || read.Multiplier > 1
                ? read.Multiplier : same ? entry.Row.Multiplier : read.Multiplier;
            entry.Confirmations = entry.PendingName == read.Name ? entry.Confirmations + 1 : 1;
            entry.PendingName = read.Name;
            entry.Locked = read.ExactMatch || entry.Confirmations >= 2;
            entry.Row = read with { Multiplier = quantity,
                MultiplierExplicit = read.MultiplierExplicit || (same && entry.Row.MultiplierExplicit) };
            entry.DocumentY = documentY;
            entry.Signature = signature;
        }
        // Bounded per-window RAM; never write scanned content to a temporary file.
        if (_entries.Count > 512) _entries.RemoveRange(0, _entries.Count - 512);
    }

    public IReadOnlyList<PriceRow> Visible()
    {
        if (!Aligned || _current is null) return [];
        var result = new List<PriceRow>();
        foreach (var entry in _entries.Where(e => e.Locked))
        {
            int y = entry.DocumentY - Offset;
            // Pixel validation also clips rows hidden by the fixed header/footer and tooltips.
            if (_current.Locate(entry.Signature, y) is int actualY)
                result.Add(entry.Row with { CenterY = actualY });
        }
        return result.OrderBy(r => r.CenterY).ToArray();
    }
}
