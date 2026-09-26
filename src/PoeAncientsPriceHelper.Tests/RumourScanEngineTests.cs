using System.Drawing;
using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class RumourScanEngineTests
{
    [Fact]
    public void WorldGateRegion_IsTopCentreBand_OfThePrimaryScreen()
    {
        var screen = new Rectangle(0, 0, 2560, 1440);
        var gate = RumourScanEngine.WorldGateRegion(screen);

        Assert.Equal(2560 / 5, gate.Width);          // a fifth of the width
        Assert.Equal(1440 / 8, gate.Height);         // an eighth of the height
        Assert.Equal(screen.Top, gate.Top);          // pinned to the very top
        // Horizontally centred.
        Assert.Equal(screen.Left + screen.Width / 2, gate.Left + gate.Width / 2);
    }

    [Fact]
    public void WorldGateRegion_FollowsAnOffsetWindowedViewport()
    {
        // #45: a windowed client parked below/right of the monitor's top-left. The band must sit at the
        // TOP-CENTRE OF THE VIEWPORT (where the WORLD label renders), not the monitor's top edge —
        // otherwise the label falls outside the gate and the scan never triggers.
        var viewport = new Rectangle(64, 240, 1904, 816);
        var gate = RumourScanEngine.WorldGateRegion(viewport);

        Assert.Equal(viewport.Top, gate.Top);        // pinned to the viewport top, not the monitor top
        Assert.Equal(viewport.Left + viewport.Width / 2, gate.Left + gate.Width / 2);   // centred on the viewport
        Assert.True(gate.Left >= viewport.Left && gate.Right <= viewport.Right);
    }

    [Fact]
    public void WorldGateRegion_RespectsAMonitorOrigin()
    {
        // A monitor to the left of the primary (negative origin).
        var screen = new Rectangle(-1920, 0, 1920, 1080);
        var gate = RumourScanEngine.WorldGateRegion(screen);

        Assert.Equal(screen.Top, gate.Top);
        Assert.Equal(screen.Left + screen.Width / 2, gate.Left + gate.Width / 2);
        Assert.True(gate.Left >= screen.Left && gate.Right <= screen.Right);
    }

    private static OcrTextLine Line(string text) => new(text, new Rectangle(0, 0, 100, 20));

    [Fact]
    public void ContainsWorldToken_TrueWhenWorldLabelPresent()
    {
        Assert.True(RumourScanEngine.ContainsWorldToken([Line("WORLD"), Line("Act 3")]));
    }

    [Fact]
    public void ContainsWorldToken_FalseForSubstringsAndNoise()
    {
        Assert.False(RumourScanEngine.ContainsWorldToken([Line("Underworld Map"), Line("Act 1")]));
        Assert.False(RumourScanEngine.ContainsWorldToken([Line("Mistwood"), Line("Hideout")]));
        Assert.False(RumourScanEngine.ContainsWorldToken([]));
    }

    [Theory]
    [InlineData("worid")]    // l→i, a single-glyph OCR slip on the stylised banner
    [InlineData("vvorld")]   // W read as "vv"
    [InlineData("wORLO")]    // trailing D→O
    public void ContainsWorldToken_TrueForCloseOcrMisreads(string misread)
    {
        // Even upscaled, the ornate WORLD banner can lose a glyph; a near-match still counts as "on map" (#45).
        Assert.True(RumourScanEngine.ContainsWorldToken([Line(misread)]));
    }

    [Fact]
    public void WorldGateRegion_ReachesTheControllerAtlasTitle()
    {
        // Controller UI: the "ATLAS" tab title sits below the character-menu tab bar, its box ending at
        // ~10.5 % of the viewport height (y ≈ 118 of 1125) — beyond the old 1/15 band.
        var screen = new Rectangle(0, 0, 2000, 1125);
        var gate = RumourScanEngine.WorldGateRegion(screen);

        Assert.True(gate.Bottom >= 118);
        Assert.True(gate.Left <= 970 && gate.Right >= 1030);   // the label's horizontal extent
    }

    [Fact]
    public void ContainsWorldToken_TrueWhenControllerAtlasTitlePresent()
    {
        Assert.True(RumourScanEngine.ContainsWorldToken([Line("ATLAS")]));
        Assert.True(RumourScanEngine.ContainsWorldToken([Line("Atlas")]));
    }

    [Theory]
    [InlineData("atias")]    // l→i
    [InlineData("atla5")]    // S→5
    [InlineData("ATLA")]     // lost trailing glyph
    public void ContainsWorldToken_TrueForCloseAtlasMisreads(string misread)
    {
        Assert.True(RumourScanEngine.ContainsWorldToken([Line(misread)]));
    }

    [Theory]
    [InlineData("Inventory")]
    [InlineData("Quests")]
    [InlineData("Cosmetics")]
    [InlineData("Character")]
    [InlineData("Passives")]
    [InlineData("Skills")]
    public void ContainsWorldToken_FalseForOtherCharacterMenuTabs(string title)
    {
        // The other tabs show their own title in the same slot; none may open the gate.
        Assert.False(RumourScanEngine.ContainsWorldToken([Line(title)]));
    }

    [Fact]
    public void ContainsWorldToken_FalseForTheMkbActRowWithoutTheBanner()
    {
        // The taller band now also covers the act buttons under the M+KB WORLD banner; on their own they
        // must not count as being on the map.
        Assert.False(RumourScanEngine.ContainsWorldToken(
            [Line("Search here"), Line("ACT 1 ACT 2 ACT 3 ACT 4 INTERLUDE ENDGAME")]));
    }

    [Theory]
    [InlineData(34, 4)]      // tester's manual box → 4× (reads "WORLD")
    [InlineData(54, 4)]      // auto band on an 816-tall client → 4× (3× read garbage; the fix)
    [InlineData(72, 4)]      // default-res auto band → still 4×
    [InlineData(150, 4)]
    [InlineData(180, 4)]     // auto band at 1440p (1440 / 8)
    [InlineData(270, 4)]     // auto band at native 4K (2160 / 8) → keeps the 4× it had at 1/15
    [InlineData(300, 1)]     // larger (hand-drawn) band → no upscale
    [InlineData(500, 1)]
    [InlineData(0, 1)]       // guard against a zero-height region
    public void GateUpscale_FlatFourForSmallBands(int regionHeight, int expected)
    {
        Assert.Equal(expected, RumourScanEngine.GateUpscale(regionHeight));
    }
}
