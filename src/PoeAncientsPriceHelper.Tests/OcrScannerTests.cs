using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class OcrScannerTests
{
    [Theory]
    [InlineData("Support: Scattering Flame", "support scattering flame")]
    [InlineData("Chilling Flux", "chilling flux")]
    [InlineData("Skill: Grip Filters", "skill grip filters")]
    [InlineData("  VERISIUM FLUX  ", "verisium flux")]
    [InlineData("Rune-of-Aldur", "rune of aldur")]
    public void NormalizeName_ProducesExpectedKey(string input, string expected)
    {
        Assert.Equal(expected, NameNormalizer.Normalize(input));
    }

    [Fact]
    public void NormalizeName_EmptyAfterStrip_ReturnsEmpty()
    {
        Assert.Equal("", NameNormalizer.Normalize(":::---"));
    }

    [Fact]
    public void NormalizeName_CollapseWhitespace()
    {
        Assert.Equal("a b c", NameNormalizer.Normalize("a   b   c"));
    }

    [Theory]
    // Windows OCR's zh-TW recognizer emits a space between many CJK characters ("混 沽 石" for
    // 混沌石). Whitespace between two ideographs is never meaningful, so Normalize folds it away —
    // that is what lets the spaced-out read match the zh-TW.json key (which is contiguous) exactly.
    // Only CJK↔CJK gaps close: the "Nx" stack marker keeps its space, Latin text is untouched.
    [InlineData("混 沌 石", "混沌石")]
    [InlineData("混  沌  石", "混沌石")]
    [InlineData("3x 混 沌 石", "3x 混沌石")]
    [InlineData("未切割的技能寶石 等級 19", "未切割的技能寶石等級 19")]
    [InlineData("混沌石", "混沌石")]
    [InlineData("chaos orb", "chaos orb")]
    [InlineData("a b c", "a b c")]
    public void NormalizeName_CollapsesWhitespaceBetweenCjkChars(string input, string expected)
    {
        Assert.Equal(expected, NameNormalizer.Normalize(input));
    }

    [Theory]
    [InlineData("14x adaptive alloy", "adaptive alloy")]
    [InlineData("1 mystic alloy", "mystic alloy")]
    [InlineData("3x rune of aldur", "rune of aldur")]
    [InlineData("adaptive alloy", "adaptive alloy")]
    [InlineData("1 1 adaptive alloy", "adaptive alloy")]
    [InlineData("e l8 n 1x the greatwolf s rune of willpower", "the greatwolf s rune of willpower")]
    [InlineData("oa a 1x greater orb of transmutation", "greater orb of transmutation")]
    [InlineData("b l38 unique quarterstaff", "unique quarterstaff")]
    // A real first word whose letters OCR read as digits ("Olroth's" → "01roth's") keeps a 3+ letter
    // run, so it must NOT be stripped as leading junk — the digit-fold resolver recovers it (#43).
    [InlineData("01roth s saga", "01roth s saga")]
    [InlineData("01roth s crest of the sun", "01roth s crest of the sun")]
    // Pure junk with a digit but no letter run is still stripped, even ahead of a kept digit-word.
    [InlineData("l8 01roth s saga", "01roth s saga")]
    [InlineData("krogin 1x ancient rune of decay", "ancient rune of decay")]
    [InlineData("hefod 1x ancient rune of the titan", "ancient rune of the titan")]
    [InlineData("nerog 11x ancient rune of discovery", "ancient rune of discovery")]
    [InlineData("ancient rune of shattering", "ancient rune of shattering")]
    // OCR drops the space between the stack marker and the name ("6xArcanist's Etcher"): the glued
    // "6xarcanist" token must not be eaten whole, leaving the name intact. (error what.png)
    [InlineData("6xarcanist s etcher", "arcanist s etcher")]
    [InlineData("6x arcanist s etcher", "arcanist s etcher")]
    [InlineData("14xadaptive alloy", "adaptive alloy")]
    // Non-Latin names must survive: the leading-noise strip is letter-class aware, not [a-z]-only.
    // An ASCII-only strip ate whole Cyrillic names → "" → REJ:short on Russian clients (#39).
    [InlineData("совершенная сфера усиления 3", "совершенная сфера усиления 3")]
    [InlineData("сфера отмены 2", "сфера отмены 2")]
    [InlineData("чародейский расплав уровень 20 1", "чародейский расплав уровень 20 1")]
    // CJK names must survive too: the short-token rule excludes ideographs, so neither a stack
    // marker nor leading junk may eat a zh-TW name (Normalize has already merged any per-character
    // spacing before this runs).
    [InlineData("3x 混沌石", "混沌石")]
    [InlineData("14x崇高石", "崇高石")]
    [InlineData("e l8 混沌石", "混沌石")]
    [InlineData("混沌石", "混沌石")]
    public void StripLeadingNoise_RemovesQuantityPrefix(string input, string expected)
    {
        Assert.Equal(expected, OcrScanner.StripLeadingNoise(input));
    }

    [Theory]
    // The exchange panel appends a bracketed stack count after the name; it must be stripped so the
    // localized→English translation lookup matches exactly (#40). English cases too, for good measure.
    [InlineData("Perfect Chaos Orb (3)", "Perfect Chaos Orb")]
    [InlineData("Сфера отмены (3)", "Сфера отмены")]
    // The count digit is often OCR-misread as a letter (Cyrillic "З" for 3) — still a stack marker.
    [InlineData("Совершенная сфера хаоса (З)", "Совершенная сфера хаоса")]
    [InlineData("Совершенная сфера усиления (З)", "Совершенная сфера усиления")]
    [InlineData("Greater Orb of Augmentation (12)", "Greater Orb of Augmentation")]
    // Bracket variants: OCR sometimes reads ( as [ or {.
    [InlineData("Orb of Alchemy [3]", "Orb of Alchemy")]
    // Only the LAST bracketed group goes — a gem's "(Level 19)" is longer/has a space, so it stays.
    [InlineData("Uncut Skill Gem (Level 19) (1)", "Uncut Skill Gem (Level 19)")]
    // The rune-shape-combination panel (#48) shows a BARE, un-bracketed "xN" stack count — and OCR
    // reads the "1" as its look-alike "l" ("x1" → "xl"). Stripped so the Spanish Saqawal runes match.
    [InlineData("Runa de erosión de Saqawal xl", "Runa de erosión de Saqawal")]
    [InlineData("Saqawal's Rune of Erosion x1", "Saqawal's Rune of Erosion")]
    [InlineData("Adaptive Alloy x14", "Adaptive Alloy")]
    // The "x" must be spaced off the name — a word ending in x ("...flux") is never a stack marker.
    [InlineData("Verisium Flux", "Verisium Flux")]
    // A zh-TW client writes the stack count in FULL-WIDTH brackets — "混沌石（3）" — which must
    // strip like the half-width form or the zh-TW.json exact lookup breaks on a stray " 3".
    [InlineData("混沌石（3）", "混沌石")]
    [InlineData("神性石（12）", "神性石")]
    [InlineData("崇高石 （З）", "崇高石")]   // count digit OCR-misread as a letter, full-width brackets
    // Full-width gem group is too long (holds a space) to be a stack count — only the LAST short
    // group goes, exactly like the half-width "(Level 19)" case.
    [InlineData("未切割的技能寶石（等級 19）", "未切割的技能寶石（等級 19）")]
    [InlineData("未切割的技能寶石（等級 19）（1）", "未切割的技能寶石（等級 19）")]
    // No trailing marker → unchanged.
    [InlineData("Perfect Chaos Orb", "Perfect Chaos Orb")]
    [InlineData("Chaos Orb", "Chaos Orb")]
    [InlineData("混沌石", "混沌石")]
    public void StripTrailingStackCount_RemovesBracketedMarker(string input, string expected)
    {
        Assert.Equal(expected, OcrScanner.StripTrailingStackCount(input));
    }

    [Theory]
    // The Game-language code drives which OCR recognizer is selected (#41). English (and empty/unset)
    // returns null → use the Windows profile default; every other language pins its own recognizer.
    [InlineData("ru", "ru")]
    [InlineData("de", "de")]
    [InlineData("fr", "fr")]
    [InlineData("pt", "pt")]
    // The app spells Spanish "sp" but Windows/BCP-47 uses "es".
    [InlineData("sp", "es")]
    [InlineData("SP", "es")]           // case-insensitive
    [InlineData("  ru  ", "ru")]       // trimmed
    [InlineData("en", null)]           // English → profile default, no override
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void OcrLanguageTag_MapsGameLanguageToRecognizerTag(string? gameLanguage, string? expected)
    {
        Assert.Equal(expected, OcrScanner.OcrLanguageTag(gameLanguage));
    }

    [Theory]
    [InlineData("14x adaptive alloy", 14)]
    [InlineData("3x rune of aldur", 3)]
    [InlineData("1 mystic alloy", 1)]              // no x marker → default 1
    [InlineData("adaptive alloy", 1)]             // no quantity → default 1
    [InlineData("e l8 n 1x the greatwolf", 1)]
    [InlineData("krogin 2x ancient rune of decay", 2)]
    [InlineData("nerog 11x ancient rune of discovery", 11)]
    [InlineData("oa a 1x greater orb of transmutation", 1)]
    [InlineData("warding rune of protection i", 1)] // roman numeral, not a multiplier
    [InlineData("6xarcanist s etcher", 6)]          // marker glued to the name (OCR dropped the space)
    public void ExtractMultiplier_ReadsQuantity(string input, int expected)
    {
        Assert.Equal(expected, OcrScanner.ExtractMultiplier(input));
    }

    [Theory]
    [InlineData("3x orb of alchemy", 3, true)]      // explicit Nx marker → Explicit
    [InlineData("1x orb of alchemy", 1, true)]      // explicit 1x is still an explicit read
    [InlineData("orb of alchemy", 1, false)]        // no marker → assumed single, not explicit
    [InlineData("warding rune of protection i", 1, false)]
    public void ExtractMultiplierWithConfidence_TracksExplicitMarker(string input, int expectedMultiplier, bool expectedExplicit)
    {
        var (multiplier, explicitHit) = OcrScanner.ExtractMultiplierWithConfidence(input);
        Assert.Equal(expectedMultiplier, multiplier);
        Assert.Equal(expectedExplicit, explicitHit);
    }

    [Theory]
    // The row-admission length gates are CJK-weighted: one zh-TW ideograph counts as 2 (it carries
    // about a Latin word of meaning), so the 3-char currency names of zh-TW.json ("混沌石" = Chaos
    // Orb) pass what a 3-letter Latin fragment cannot. Before the weighting, every one of those
    // rows died as REJ:short — OCR caught the Chinese and the gates threw it away before the
    // translation lookup ever ran. A lone ideograph, or one ideograph amid Latin junk, is still
    // fragment noise (no ≥4 weighted run) → "noword".
    [InlineData("混沌石", null)]
    [InlineData("神性石", null)]
    [InlineData("遠古腐朽符文", null)]
    [InlineData("未切割的技能寶石", null)]
    [InlineData("chaos orb", null)]        // Latin behaviour unchanged
    [InlineData("void flux", null)]
    [InlineData("石", "short")]            // a lone ideograph is still OCR debris
    [InlineData("ab", "short")]
    [InlineData("e l8", "noword")]
    [InlineData("石 e", "noword")]         // spaced junk around one ideograph: no long run
    public void NameGateRejectReason_CjkWeighted(string normalized, string? expected)
    {
        Assert.Equal(expected, OcrScanner.NameGateRejectReason(normalized));
    }

    [Theory]
    // One CJK ideograph weighs 2 Latin characters in the effective length.
    [InlineData("", 0)]
    [InlineData("ab", 2)]
    [InlineData("chaos orb", 9)]
    [InlineData("石", 2)]
    [InlineData("混沌石", 6)]
    [InlineData("3x混沌石", 8)]           // digits/letters weigh 1, ideographs 2
    public void EffectiveLength_WeightsCjkChars(string normalized, int expected)
    {
        Assert.Equal(expected, OcrScanner.EffectiveLength(normalized));
    }
}
