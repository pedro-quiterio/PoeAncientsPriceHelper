using PoeAncientsPriceHelper;
using SharpHook.Data;

namespace PoeAncientsPriceHelper.Tests;

public class HotkeyBindingTests
{
    [Theory]
    [InlineData("VcF7", KeyCode.VcF7)]
    [InlineData("VcA", KeyCode.VcA)]
    [InlineData("VcF5", KeyCode.VcF5)]
    public void ParseChord_BareKey_ReturnsUnmodifiedChord(string stored, KeyCode expected)
    {
        Assert.Equal(new Chord(expected), HotkeyBinding.ParseChord(stored));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-key")]
    [InlineData("12345")]
    [InlineData("Ctrl")]            // modifier-only is not a valid binding
    [InlineData("Ctrl+Shift")]      // still modifier-only
    [InlineData("Hyper+VcQ")]       // unknown modifier token
    public void ParseChord_Invalid_FallsBackToDefault(string? stored)
    {
        Assert.Equal(HotkeyBinding.Default, HotkeyBinding.ParseChord(stored));
    }

    [Theory]
    [InlineData("Ctrl+VcQ", KeyCode.VcQ, Modifiers.Ctrl)]
    [InlineData("Ctrl+Shift+VcQ", KeyCode.VcQ, Modifiers.Ctrl | Modifiers.Shift)]
    [InlineData("Alt+VcF5", KeyCode.VcF5, Modifiers.Alt)]
    [InlineData("ctrl+shift+vcq", KeyCode.VcQ, Modifiers.Ctrl | Modifiers.Shift)]   // case-insensitive
    [InlineData("Ctrl+Q", KeyCode.VcQ, Modifiers.Ctrl)]                              // bare key gets "Vc" prefix
    public void ParseChord_WithModifiers_Parses(string stored, KeyCode key, Modifiers mods)
    {
        Assert.Equal(new Chord(key, mods), HotkeyBinding.ParseChord(stored));
    }

    [Theory]
    [InlineData(KeyCode.VcF5, Modifiers.None)]
    [InlineData(KeyCode.VcP, Modifiers.None)]
    [InlineData(KeyCode.VcQ, Modifiers.Ctrl)]
    [InlineData(KeyCode.VcQ, Modifiers.Ctrl | Modifiers.Shift)]
    [InlineData(KeyCode.VcF5, Modifiers.Ctrl | Modifiers.Shift | Modifiers.Alt | Modifiers.Meta)]
    public void StorageRoundTrips(KeyCode key, Modifiers mods)
    {
        var chord = new Chord(key, mods);
        Assert.Equal(chord, HotkeyBinding.ParseChord(HotkeyBinding.ToStorage(chord)));
    }

    [Fact]
    public void ToStorage_BareKey_MatchesLegacyFormat()
    {
        // Old configs stored just the enum name; a modifier-less chord must serialize identically.
        Assert.Equal("VcF5", HotkeyBinding.ToStorage(new Chord(KeyCode.VcF5)));
    }

    [Theory]
    [InlineData(KeyCode.VcF5, Modifiers.None, "F5")]
    [InlineData(KeyCode.VcA, Modifiers.None, "A")]
    [InlineData(KeyCode.Vc1, Modifiers.None, "1")]
    [InlineData(KeyCode.VcQ, Modifiers.Ctrl, "Ctrl+Q")]
    [InlineData(KeyCode.VcQ, Modifiers.Ctrl | Modifiers.Shift, "Ctrl+Shift+Q")]
    [InlineData(KeyCode.VcF5, Modifiers.Alt, "Alt+F5")]
    public void Display_FormatsChord(KeyCode key, Modifiers mods, string expected)
    {
        Assert.Equal(expected, HotkeyBinding.Display(new Chord(key, mods)));
    }

    [Theory]
    [InlineData("Ctrl+Shift+VcQ")]
    [InlineData("Shift+Ctrl+VcQ")]   // stored in canonical Ctrl,Shift,... order regardless of input order
    public void ToStorage_UsesCanonicalModifierOrder(string stored)
    {
        var chord = HotkeyBinding.ParseChord(stored);
        Assert.Equal("Ctrl+Shift+VcQ", HotkeyBinding.ToStorage(chord));
    }

    [Fact]
    public void ExactMatch_BareKeyAndModifiedKeyAreDistinct()
    {
        // The whole point of #46: Q must not equal Ctrl+Q.
        Assert.NotEqual(new Chord(KeyCode.VcQ), new Chord(KeyCode.VcQ, Modifiers.Ctrl));
    }

    [Theory]
    [InlineData(KeyCode.VcLeftControl, Modifiers.Ctrl)]
    [InlineData(KeyCode.VcRightControl, Modifiers.Ctrl)]
    [InlineData(KeyCode.VcLeftShift, Modifiers.Shift)]
    [InlineData(KeyCode.VcLeftAlt, Modifiers.Alt)]
    [InlineData(KeyCode.VcLeftMeta, Modifiers.Meta)]
    [InlineData(KeyCode.VcQ, Modifiers.None)]
    [InlineData(KeyCode.VcF5, Modifiers.None)]
    public void ModifierOf_MapsModifierKeys(KeyCode key, Modifiers expected)
    {
        Assert.Equal(expected, HotkeyBinding.ModifierOf(key));
    }

    [Theory]
    [InlineData(KeyCode.VcEscape, true)]
    [InlineData(KeyCode.VcLeftControl, true)]
    [InlineData(KeyCode.VcRightControl, true)]
    // F3/F4 are ordinary rebindable defaults, not reserved gestures.
    [InlineData(KeyCode.VcF3, false)]
    [InlineData(KeyCode.VcF4, false)]
    [InlineData(KeyCode.VcF5, false)]
    [InlineData(KeyCode.VcP, false)]
    public void IsReserved_FlagsOnlyFixedGestures(KeyCode key, bool reserved)
    {
        Assert.Equal(reserved, HotkeyBinding.IsReserved(key));
    }
}
