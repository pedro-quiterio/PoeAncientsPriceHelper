namespace PoeAncientsPriceHelper.Tests;

// Regression coverage for the focus gate (#62): with "only scan while PoE is active" on, the overlay
// kept scanning over other apps when the game was CLOSED, because a fail-open window lookup couldn't
// tell "game shut" from "window momentarily not found". ShouldPauseForFocus encodes the fixed decision.
public class ScanEngineFocusGateTests
{
    [Theory]
    // gate disabled → never pause, whatever the window/foreground/running state.
    [InlineData(false, false, false, false, false)]
    [InlineData(false, true, false, false, false)]
    // window found + foreground → scanning, don't pause.
    [InlineData(true, true, true, true, false)]
    // window found + NOT foreground (alt-tabbed) → pause.
    [InlineData(true, true, false, true, true)]
    // window NOT found but a PoE process is running (launching / transient miss) → fail-open, don't pause.
    [InlineData(true, false, false, true, false)]
    // window NOT found AND no PoE process (game closed) → pause (the #62 fix).
    [InlineData(true, false, false, false, true)]
    public void ShouldPauseForFocus_TruthTable(
        bool gateEnabled, bool windowFound, bool gameIsForeground, bool gameIsRunning, bool expected)
    {
        Assert.Equal(expected, ScanEngine.ShouldPauseForFocus(gateEnabled, windowFound, gameIsForeground, gameIsRunning));
    }
}
