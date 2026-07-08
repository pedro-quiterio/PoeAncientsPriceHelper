using SharpHook.Data;

namespace PoeAncientsPriceHelper;

// Modifier keys that may prefix a hotkey. Matched EXACTLY, so `Q` and `Ctrl+Q` are distinct bindings
// that never fire each other (#46). Left/right variants collapse to a single flag.
[Flags]
public enum Modifiers { None = 0, Ctrl = 1, Shift = 2, Alt = 4, Meta = 8 }

// A hotkey binding: one non-modifier key plus the modifiers that must be held with it. A bare key
// (Modifiers.None) is the historical single-key binding, so old configs ("VcF5") keep working verbatim.
public readonly record struct Chord(KeyCode Key, Modifiers Modifiers = Modifiers.None);

// Pure (no-WPF, no-hook) helpers for the configurable hotkeys, kept separate so the
// parsing/display/reserved logic is unit-testable without standing up the window or global hook.
//
// A binding is stored, captured, and matched as a Chord — the same KeyCode the hook reports, plus the
// modifier flags — so there is no WPF-Key ↔ KeyCode mapping to maintain.
internal static class HotkeyBinding
{
    // The three rebindable actions. Used to tell capture which binding it's replacing so it can reject
    // a chord already taken by one of the *other two* (a collision check that lives in App, where the
    // current bindings are held).
    public enum Action { StartStop, Debug, Calibrate }

    public static readonly Chord DefaultStartStop = new(KeyCode.VcF5);
    public static readonly Chord DefaultDebug = new(KeyCode.VcF3);
    public static readonly Chord DefaultCalibrate = new(KeyCode.VcF4);
    public static readonly Chord Default = DefaultStartStop;

    // Keys hard-wired to fixed gestures that mirror in-game actions (Esc closes the panel, L/R-Ctrl is
    // the buy modifier). These can never be the *final* key of a rebindable chord — a single press would
    // fire two things. Esc additionally doubles as "cancel capture". Modifier-only chords (e.g. "Ctrl")
    // are rejected elsewhere: capture never completes on a modifier release. F3/F4 are ordinary defaults.
    public static readonly IReadOnlyList<KeyCode> Reserved =
    [
        KeyCode.VcEscape, KeyCode.VcLeftControl, KeyCode.VcRightControl,
    ];

    public static bool IsReserved(KeyCode key) => Reserved.Contains(key);

    // The SharpHook modifier keycodes mapped to our flag (None for any non-modifier key). Lets the hook
    // track live modifier state and lets parsing reject a modifier as a binding's final key.
    public static Modifiers ModifierOf(KeyCode key) => key switch
    {
        KeyCode.VcLeftControl or KeyCode.VcRightControl => Modifiers.Ctrl,
        KeyCode.VcLeftShift or KeyCode.VcRightShift => Modifiers.Shift,
        KeyCode.VcLeftAlt or KeyCode.VcRightAlt => Modifiers.Alt,
        KeyCode.VcLeftMeta or KeyCode.VcRightMeta => Modifiers.Meta,
        _ => Modifiers.None,
    };

    public static bool IsModifierKey(KeyCode key) => ModifierOf(key) != Modifiers.None;

    // config.json round-trip. No modifier ⇒ the bare enum name ("VcF5"), byte-for-byte what older builds
    // wrote (forward/backward compatible). With modifiers ⇒ "Ctrl+VcQ", "Ctrl+Shift+VcQ" — a fixed
    // Ctrl,Shift,Alt,Meta order so the stored string is stable regardless of press order.
    public static string ToStorage(Chord chord)
    {
        if (chord.Modifiers == Modifiers.None) return chord.Key.ToString();
        var parts = new List<string>(5);
        AppendModifierNames(chord.Modifiers, parts);
        parts.Add(chord.Key.ToString());
        return string.Join("+", parts);
    }

    // Parse a stored chord. Tokens are "+"-separated: leading tokens are modifiers (Ctrl/Shift/Alt/Meta,
    // case-insensitive), the last is the key. The key accepts both the stored "VcQ" form and a bare "Q"
    // (we prefix "Vc"). A modifier-only string, an unknown token, or anything unparseable ⇒ the default.
    public static Chord ParseChord(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return Default;
        var tokens = stored.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return Default;

        var mods = Modifiers.None;
        for (int i = 0; i < tokens.Length - 1; i++)
        {
            switch (tokens[i].ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Modifiers.Ctrl; break;
                case "shift": mods |= Modifiers.Shift; break;
                case "alt": mods |= Modifiers.Alt; break;
                case "meta" or "win" or "super": mods |= Modifiers.Meta; break;
                default: return Default;   // unknown modifier token ⇒ reject the whole binding
            }
        }

        if (!TryParseKey(tokens[^1], out var key) || IsModifierKey(key)) return Default;
        return new Chord(key, mods);
    }

    // Friendly label for the UI: "F5", "Ctrl+Q", "Ctrl+Shift+Q". SharpHook key names are "Vc"-prefixed.
    public static string Display(Chord chord)
    {
        var parts = new List<string>(5);
        AppendModifierNames(chord.Modifiers, parts);
        parts.Add(DisplayKey(chord.Key));
        return string.Join("+", parts);
    }

    private static void AppendModifierNames(Modifiers mods, List<string> parts)
    {
        if (mods.HasFlag(Modifiers.Ctrl)) parts.Add("Ctrl");
        if (mods.HasFlag(Modifiers.Shift)) parts.Add("Shift");
        if (mods.HasFlag(Modifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(Modifiers.Meta)) parts.Add("Meta");
    }

    private static string DisplayKey(KeyCode key)
    {
        var name = key.ToString();
        return name.StartsWith("Vc", StringComparison.Ordinal) ? name[2..] : name;
    }

    private static bool TryParseKey(string token, out KeyCode key)
    {
        if (Enum.TryParse(token, ignoreCase: true, out key) && Enum.IsDefined(key)) return true;
        if (Enum.TryParse("Vc" + token, ignoreCase: true, out key) && Enum.IsDefined(key)) return true;
        key = default;
        return false;
    }
}
