using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

// The stabiliser holds the best resolution per rumour row while a panel is open, so OCR jitter can't
// flip a resolved row back to "unknown" (or to a different rumour) under the user (#45 follow-up).
public class RumourStabilizerTests
{
    private static RumourEntry Entry(string name) => new(name, "Map", "Mods", "A");

    private static RumourResultRow Resolved(string name) => new(name, Entry(name));
    private static RumourResultRow Unknown(string ocr) => new(ocr, null);

    // Names of the resolved entries per row, "?" for an unresolved row — the display-relevant summary.
    private static string[] Names(IReadOnlyList<RumourResultRow> rows) =>
        rows.Select(r => r.Entry?.Rumor ?? "?").ToArray();

    [Fact]
    public void FirstResolvedRead_ShowsImmediately()
    {
        var s = new RumourStabilizer();
        var outRows = s.Stabilize([Resolved("Cold ruins"), Resolved("Warm cave")]);
        Assert.Equal(["Cold ruins", "Warm cave"], Names(outRows));
    }

    [Fact]
    public void ResolvedRow_DoesNotDowngradeWhenLaterReadIsUnknown()
    {
        var s = new RumourStabilizer();
        s.Stabilize([Resolved("Cold ruins")]);
        // Next pass OCR garbles the line so it no longer resolves — the row must stay locked.
        var outRows = s.Stabilize([Unknown("Co1d rww5")]);
        Assert.Equal(["Cold ruins"], Names(outRows));
    }

    [Fact]
    public void UnknownRow_UpgradesOnceItResolves()
    {
        var s = new RumourStabilizer();
        var first = s.Stabilize([Unknown("garbled")]);
        Assert.Equal(["?"], Names(first));
        var second = s.Stabilize([Resolved("Cold ruins")]);
        Assert.Equal(["Cold ruins"], Names(second));
    }

    [Fact]
    public void DataGapUnknown_StaysUnknownStably()
    {
        // A rumour whose in-game name isn't in the sheet never resolves — it must stay "unknown", not
        // flicker, no matter how its raw OCR text jitters.
        var s = new RumourStabilizer();
        Assert.Equal(["?"], Names(s.Stabilize([Unknown("Warm but risky")])));
        Assert.Equal(["?"], Names(s.Stabilize([Unknown("Warw bvt risky")])));
        Assert.Equal(["?"], Names(s.Stabilize([Unknown("Warm but risky")])));
    }

    [Fact]
    public void SinglePassDifferentEntry_DoesNotFlipALockedRow()
    {
        var s = new RumourStabilizer();
        s.Stabilize([Resolved("Cold ruins")]);
        // One stray pass resolves elsewhere — not enough to move the lock.
        var outRows = s.Stabilize([Resolved("Warm cave")]);
        Assert.Equal(["Cold ruins"], Names(outRows));
    }

    [Fact]
    public void DifferentEntryOnTwoConsecutivePasses_TakesOver()
    {
        var s = new RumourStabilizer();
        s.Stabilize([Resolved("Cold ruins")]);
        s.Stabilize([Resolved("Warm cave")]);            // pass 1 of the different entry — still Cold
        var outRows = s.Stabilize([Resolved("Warm cave")]); // pass 2 — now switch
        Assert.Equal(["Warm cave"], Names(outRows));
    }

    [Fact]
    public void DifferentEntryStreak_IsBrokenByAnInterveningUnknown()
    {
        var s = new RumourStabilizer();
        s.Stabilize([Resolved("Cold ruins")]);
        s.Stabilize([Resolved("Warm cave")]);   // different-entry pass 1
        s.Stabilize([Unknown("noise")]);        // breaks the streak — locked row held, pending cleared
        var outRows = s.Stabilize([Resolved("Warm cave")]); // only pass 1 again → no switch yet
        Assert.Equal(["Cold ruins"], Names(outRows));
    }

    [Fact]
    public void Reset_ClearsLocksSoAReopenedPanelStartsFresh()
    {
        var s = new RumourStabilizer();
        s.Stabilize([Resolved("Cold ruins")]);
        s.Reset();
        // A different panel with the same single-row shape opens — should show its own rumour at once.
        var outRows = s.Stabilize([Resolved("Warm cave")]);
        Assert.Equal(["Warm cave"], Names(outRows));
    }

    [Fact]
    public void RowCountChange_RebuildsFromTheNewRead()
    {
        var s = new RumourStabilizer();
        s.Stabilize([Resolved("Cold ruins"), Resolved("Warm cave")]);
        // A structurally different panel (three rows) — map onto the new read, not the stale two slots.
        var outRows = s.Stabilize([Resolved("A"), Resolved("B"), Resolved("C")]);
        Assert.Equal(["A", "B", "C"], Names(outRows));
    }

    [Fact]
    public void LockedRow_IsFullyFrozenWhileTheEntryHolds()
    {
        // Once locked, a same-entry re-read leaves the whole row untouched (raw OCR name included), so
        // even the --debug "resolved <- raw" line — and therefore the overlay signature — stays put.
        var s = new RumourStabilizer();
        s.Stabilize([Resolved("Cold ruins")]);
        var outRows = s.Stabilize([new RumourResultRow("Cold rvins", Entry("Cold ruins"))]);
        Assert.Equal("Cold ruins", outRows[0].OcrName);
        Assert.Equal("Cold ruins", outRows[0].Entry!.Rumor);
    }
}
