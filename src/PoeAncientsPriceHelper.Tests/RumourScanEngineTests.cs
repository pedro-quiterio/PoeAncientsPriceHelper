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
        Assert.Equal(1440 / 15, gate.Height);        // a fifteenth of the height
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
}
