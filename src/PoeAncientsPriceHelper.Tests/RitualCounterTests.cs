using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class RitualCounterTests
{
    [Theory]
    [InlineData("4/4", 4, 4)]
    [InlineData("0/4", 0, 4)]
    [InlineData("3/6", 3, 6)]
    [InlineData(" 2 / 3 ", 2, 3)]        // spaces around the separator
    [InlineData("Tribute 1/4", 1, 4)]    // counter embedded in other OCR text
    public void TryParse_ReadsCleanCounters(string text, int expN, int expM)
    {
        Assert.True(RitualCounter.TryParse(text, out int n, out int m));
        Assert.Equal(expN, n);
        Assert.Equal(expM, m);
    }

    [Theory]
    [InlineData("414", 4, 4)]    // "/" OCR'd as 1
    [InlineData("4l4", 4, 4)]    // "/" OCR'd as lowercase L
    [InlineData("4I4", 4, 4)]    // "/" OCR'd as uppercase i
    [InlineData("4|4", 4, 4)]    // "/" OCR'd as pipe
    [InlineData("O/4", 0, 4)]    // leading zero OCR'd as letter O
    public void TryParse_ToleratesSeparatorAndDigitMisreads(string text, int expN, int expM)
    {
        Assert.True(RitualCounter.TryParse(text, out int n, out int m));
        Assert.Equal(expN, n);
        Assert.Equal(expM, m);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello")]
    [InlineData("4/0")]    // M must be >= 1
    [InlineData("4/1")]    // N must not exceed M (garbage)
    [InlineData("44")]     // no separator
    public void TryParse_RejectsBlankAndImplausible(string text)
    {
        Assert.False(RitualCounter.TryParse(text, out _, out _));
    }

    // ---- state machine ----

    private static bool Feed(RitualChimeState s, params RitualReading[] readings)
    {
        bool fired = false;
        foreach (var r in readings) fired = s.Observe(r);
        return fired;   // return value of the LAST observed reading
    }

    [Fact]
    public void Chime_FiresOnce_AfterTwoConsecutiveFullReads()
    {
        var s = new RitualChimeState();
        Assert.False(s.Observe(RitualReading.Counter(0, 4)));   // arm
        Assert.False(s.Observe(RitualReading.Counter(3, 4)));   // partial
        Assert.False(s.Observe(RitualReading.Counter(4, 4)));   // first full — not yet
        Assert.True(s.Observe(RitualReading.Counter(4, 4)));    // second full — fire
        Assert.False(s.Observe(RitualReading.Counter(4, 4)));   // still full — no repeat
    }

    [Fact]
    public void Chime_DoesNotFireOnSingleFullFrame_BetweenPartials()
    {
        var s = new RitualChimeState();
        s.Observe(RitualReading.Counter(0, 4));
        Assert.False(s.Observe(RitualReading.Counter(4, 4)));   // one bad/full frame
        Assert.False(s.Observe(RitualReading.Counter(3, 4)));   // back to partial — streak broken
        Assert.False(s.Observe(RitualReading.Counter(4, 4)));   // full again, only one in a row
    }

    [Fact]
    public void Chime_ReArmsOnZeroCounter_ForTheNextRitual()
    {
        var s = new RitualChimeState();
        Feed(s, RitualReading.Counter(0, 4), RitualReading.Counter(4, 4), RitualReading.Counter(4, 4)); // fired
        Assert.False(s.Armed);
        Assert.False(s.Observe(RitualReading.Counter(0, 4)));   // next area starts at 0 → re-arm
        Assert.True(s.Armed);
        Assert.False(s.Observe(RitualReading.Counter(4, 4)));
        Assert.True(s.Observe(RitualReading.Counter(4, 4)));    // fires again for the new ritual
    }

    [Fact]
    public void Chime_ReArmsAfterRegionBlankForTwoPasses()
    {
        var s = new RitualChimeState();
        Feed(s, RitualReading.Counter(0, 4), RitualReading.Counter(4, 4), RitualReading.Counter(4, 4)); // fired
        Assert.False(s.Armed);
        s.Observe(RitualReading.Blank);                          // 1 blank — not yet
        Assert.False(s.Armed);
        s.Observe(RitualReading.Blank);                          // 2 blanks — re-arm (left the area)
        Assert.True(s.Armed);
    }

    [Fact]
    public void Chime_DoesNotFireWhenNeverStartedAtZero_UntilFullHoldsTwice()
    {
        // Tabbing in on an already-full 4/4 (armed by default): the two-pass guard still applies, so it
        // fires on the second consecutive full read rather than instantly on the first frame.
        var s = new RitualChimeState();
        Assert.False(s.Observe(RitualReading.Counter(4, 4)));
        Assert.True(s.Observe(RitualReading.Counter(4, 4)));
    }
}
