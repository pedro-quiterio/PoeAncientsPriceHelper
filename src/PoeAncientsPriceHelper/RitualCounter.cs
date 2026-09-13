namespace PoeAncientsPriceHelper;

// One read of the ritual region: either nothing recognisable (Blank) or a parsed "N/M" tribute counter.
internal readonly record struct RitualReading(bool HasCounter, int N, int M)
{
    public static readonly RitualReading Blank = new(false, 0, 0);
    public static RitualReading Counter(int n, int m) => new(true, n, m);
    public bool IsFull => HasCounter && M > 0 && N == M;
}

// Pure parse of the ritual tribute counter from an OCR'd line. The in-game counter reads "N/M"
// (completed / required, e.g. "4/4") in a small stylised font, so OCR mangles it in predictable ways:
// the "/" separator often comes back as 1 / l / I / | (and occasionally 7), and an "O" for "0". This is
// deliberately tolerant of those, then sanity-clamps the result so garbage frames can't fire the chime:
//   - M must be a plausible ritual size (1..9) — real rituals are 3/3, 4/4, up to 6/6.
//   - N must not exceed M (you can't have more tributes than the altar requires) — kills "4/1" misreads.
// The upscale factor and the exact separator character-class are the two tunables to adjust against a
// real client's font (see the --debug ritual readout).
internal static class RitualCounter
{
    // (\d) sep (\d): the separator is a single char from the tolerant class — a real "/" or its common
    // OCR look-alikes. Digits are matched after the O→0 fold below. Only single-digit N and M are
    // expected (rituals never exceed 9), which also avoids "44/4"-style two-digit garbage matching.
    private static readonly Regex CounterPattern =
        new(@"(\d)\s*[/1IL|7\\]\s*(\d)", RegexOptions.Compiled);

    public static bool TryParse(string? text, out int n, out int m)
    {
        n = 0;
        m = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Uppercase so l→L / i→I land in the separator class, and fold the common O→0 digit misread.
        var s = text.ToUpperInvariant().Replace('O', '0');
        foreach (Match match in CounterPattern.Matches(s))
        {
            int a = match.Groups[1].Value[0] - '0';
            int b = match.Groups[2].Value[0] - '0';
            if (b is >= 1 and <= 9 && a <= b) { n = a; m = b; return true; }
        }
        return false;
    }
}

// The chime arming state machine (pure, so it's unit-tested). Fires exactly once per ritual on the
// transition into a confirmed-full counter, then stays disarmed until it re-arms. Re-arms on the
// guaranteed "0/M" that opens every ritual area, and also — as a belt-and-suspenders — when the region
// reads nothing for a couple of passes (left the area / OCR missed the 0/M frame). A single misread
// frame can't fire: the full reading must hold for two consecutive passes. Re-arm paths only ever CLEAR
// the fired flag, so they can never cause a false chime.
internal sealed class RitualChimeState
{
    private const int FullStreakToFire = 2;   // consecutive full reads before chiming (OCR-noise guard)
    private const int BlankPassesToRearm = 2; // consecutive blank reads that count as "left the area"

    private bool _armed = true;
    private int _fullStreak;
    private int _blankStreak;

    public bool Armed => _armed;

    // Feed one reading; returns true on the single pass that should play the chime.
    public bool Observe(RitualReading reading)
    {
        if (!reading.HasCounter)
        {
            _fullStreak = 0;
            if (++_blankStreak >= BlankPassesToRearm) _armed = true;
            return false;
        }

        _blankStreak = 0;

        if (reading.N == 0)        // fresh ritual area — re-arm for the next fill
        {
            _armed = true;
            _fullStreak = 0;
            return false;
        }

        if (reading.IsFull)
        {
            if (_fullStreak < FullStreakToFire) _fullStreak++;
            if (_armed && _fullStreak >= FullStreakToFire)
            {
                _armed = false;    // one-shot: don't chime again until re-armed
                return true;
            }
            return false;
        }

        _fullStreak = 0;           // partial (0 < N < M)
        return false;
    }

    // Reset to the initial armed state (feature toggled off/on).
    public void Reset()
    {
        _armed = true;
        _fullStreak = 0;
        _blankStreak = 0;
    }
}
