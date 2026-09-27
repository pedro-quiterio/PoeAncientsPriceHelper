using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

// Coverage for the experimental scroll tracking (#68, contributed by @Zatyp-Tema): exact motion
// estimation, sub-pixel movement, duplicate names with independent stack sizes, off-screen exit and
// return without new OCR, session invalidation of stale async OCR, explicit quantity reduction,
// async OCR mapped through its source offset, and uncertain frames hiding prices. Ported from the
// contributor's standalone replay checks into the app's test suite.
public class ScrollTrackingTests
{
    // A deterministic tall "document" of gradient noise; a Frame is the window starting at `offset` rows.
    private static ScrollFrame Frame(int offset)
    {
        const int width = 80, height = 600;
        var random = new Random(18);
        var world = new short[width * 1800];
        for (int i = 0; i < world.Length; i++) world[i] = (short)random.Next(-80, 81);
        return new ScrollFrame(width, height, 1, world.Skip(offset * width).Take(width * height).ToArray());
    }

    private static PriceRow Row(int y, string name, int qty = 1) =>
        new(y, name, 1, 100, true, qty, name, true, MultiplierExplicit: true);

    [Fact]
    public void Matcher_EstimatesScrollExactly()
    {
        var motion = VerticalScrollMatcher.Match(Frame(300), Frame(350));
        Assert.True(motion.Reliable);
        Assert.Equal(-50, motion.Pixels);
        Assert.Equal(-1, VerticalScrollMatcher.Match(Frame(300), Frame(301)).Pixels);
    }

    [Fact]
    public void Session_FollowsScroll_KeepsIndependentStacks_AndRemembersOffscreen()
    {
        var first = Frame(300);
        var shifted = Frame(350);
        var session = new ScrollTrackingSession();
        session.Observe(first);
        session.Accept([Row(200, "chaos", 2), Row(300, "chaos", 5)], first, 0, session.Epoch);
        Assert.Equal(2, session.Visible().Count);

        session.Observe(shifted);
        Assert.Contains(session.Visible(), r => r.CenterY == 150 && r.Multiplier == 2);   // first stack follows
        Assert.Contains(session.Visible(), r => r.CenterY == 250 && r.Multiplier == 5);   // second stays independent

        session.Observe(Frame(500));
        session.Observe(Frame(650));
        Assert.Empty(session.Visible());                                                  // scrolled off screen

        session.Observe(Frame(500));
        session.Observe(Frame(350));
        session.Observe(first);
        Assert.Equal(2, session.Visible().Count);                                         // return restores, no new OCR
    }

    [Fact]
    public void Session_DiscardsStaleEpoch_AndAppliesExplicitQuantityReduction()
    {
        var first = Frame(300);
        var session = new ScrollTrackingSession();
        session.Observe(first);

        int oldEpoch = session.Epoch;
        session.Reset();
        session.Observe(first);
        session.Accept([Row(200, "chaos")], first, 0, oldEpoch);
        Assert.Empty(session.Visible());                                                  // OCR from the closed session ignored

        session.Accept([Row(200, "chaos", 5)], first, 0, session.Epoch);
        session.Accept([Row(200, "chaos", 1)], first, 0, session.Epoch);
        Assert.Equal(1, session.Visible().Single().Multiplier);                           // explicit "1x" wins
    }

    [Fact]
    public void Session_AsyncOcr_MappedThroughSourceOffset()
    {
        var first = Frame(300);
        var shifted = Frame(350);
        var session = new ScrollTrackingSession();
        session.Observe(first);
        int savedEpoch = session.Epoch;
        session.Observe(shifted);
        session.Accept([Row(300, "exalted")], first, 0, savedEpoch);                      // OCR came from `first`
        Assert.Contains(session.Visible(), r => r.Name == "exalted" && r.CenterY == 250);
    }

    [Fact]
    public void Session_UncertainFrame_HidesPrices()
    {
        var first = Frame(300);
        var session = new ScrollTrackingSession();
        session.Observe(first);
        session.Accept([Row(200, "chaos", 5)], first, 0, session.Epoch);
        Assert.NotEmpty(session.Visible());
        session.Observe(new ScrollFrame(80, 600, 1, new short[48000]));                   // flat, unalignable
        Assert.Empty(session.Visible());
    }
}
