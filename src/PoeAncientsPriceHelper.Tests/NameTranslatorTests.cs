using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class NameTranslatorTests
{
    // Real German names from issue #29's debug log (verified against poe2db.tw). The OCR'd,
    // normalized German name must resolve to the English price key.
    private static NameTranslator German() => NameTranslator.FromPairs(
    [
        ("Chaos Orb", "Chaossphäre"),
        ("Divine Orb", "Göttliche Sphäre"),
        ("Exalted Orb", "Erhabene Sphäre"),
        ("Greater Jeweller's Orb", "Große Sphäre des Goldschmieds"),
        ("Orb of Alchemy", "Sphäre der Alchemie"),
    ]);

    // Real zh-TW names from the bundled zh-TW.json seed (poe2db.tw). All 232 entries are pure CJK
    // and ≥3 ideographs, which is what the CJK weighting and the ≤1-edit rescue are tuned against.
    private static NameTranslator Chinese() => NameTranslator.FromPairs(
    [
        ("Chaos Orb", "混沌石"),
        ("Divine Orb", "神聖石"),
        ("Exalted Orb", "崇高石"),
        ("Ancient Rune of Decay", "遠古腐朽符文"),
        ("Ancient Rune of Decay II", "遠古腐朽符文二"),   // for the ambiguity test below
    ]);

    [Theory]
    [InlineData("chaossphäre", "chaos orb")]
    [InlineData("göttliche sphäre", "divine orb")]
    [InlineData("große sphäre des goldschmieds", "greater jeweller s orb")] // the #29 reporter's item
    public void Translate_ExactLocalizedName_ReturnsEnglishKey(string normalizedLocalized, string expectedKey)
    {
        Assert.Equal(expectedKey, German().Translate(normalizedLocalized));
    }

    // OCR commonly drops or mangles umlauts on the stylised panel font; diacritic folding must still
    // resolve the name (e.g. "chaossphare" with no umlaut → chaos orb).
    [Theory]
    [InlineData("chaossphare")]              // ä read as a
    [InlineData("gottliche sphare")]         // ö, ä both flattened
    [InlineData("grosse sphare des goldschmieds")] // ß→ss + ä→a
    public void Translate_DiacriticDroppedByOcr_StillResolves(string normalizedLocalized)
    {
        var en = German().Translate(normalizedLocalized);
        Assert.NotEqual(normalizedLocalized, en); // it translated to *something* English
    }

    // An English client reads English names directly — there must be no mapping that mangles them.
    [Theory]
    [InlineData("chaos orb")]
    [InlineData("greater vision rune")]
    public void Translate_AlreadyEnglish_PassesThroughUnchanged(string english)
    {
        Assert.Equal(english, German().Translate(english));
    }

    // An unknown name (not in any locale file) is returned verbatim so the English matcher can still
    // try its fuzzy chain on it.
    [Fact]
    public void Translate_UnknownName_ReturnsInputUnchanged()
    {
        Assert.Equal("völlig unbekannt", German().Translate("völlig unbekannt"));
    }

    // The empty translator (English client / no locale files) is a pure pass-through.
    [Fact]
    public void Empty_IsIdentity()
    {
        Assert.False(NameTranslator.Empty.HasEntries);
        Assert.Equal("chaossphäre", NameTranslator.Empty.Translate("chaossphäre"));
    }

    // Later locales override earlier ones key-by-key (a user file correcting a bundled entry).
    [Fact]
    public void FromPairs_LastWriterWins_OnLocalizedCollision()
    {
        var t = NameTranslator.FromPairs(
        [
            ("Wrong Item", "Testsphäre"),
            ("Chaos Orb", "Testsphäre"),
        ]);
        Assert.Equal("chaos orb", t.Translate("testsphäre"));
    }

    // Localized files for different languages merge into one translator (a Russian Cyrillic name and a
    // German name both resolve through the same instance).
    [Fact]
    public void FromPairs_MergesMultipleLanguages()
    {
        var t = NameTranslator.FromPairs(
        [
            ("Chaos Orb", "Chaossphäre"),     // de
            ("Chaos Orb", "Сфера хаоса"),     // ru (Cyrillic, untouched by folding)
        ]);
        Assert.Equal("chaos orb", t.Translate("chaossphäre"));
        Assert.Equal("chaos orb", t.Translate("сфера хаоса"));
    }

    [Theory]
    // A clean zh-TW read must hit its zh-TW.json entry exactly and come back as the English key.
    [InlineData("混沌石", "chaos orb")]
    [InlineData("神聖石", "divine orb")]
    [InlineData("遠古腐朽符文", "ancient rune of decay")]
    public void Translate_ExactTraditionalChinese_ReturnsEnglishKey(string normalizedLocalized, string expectedKey)
    {
        Assert.Equal(expectedKey, Chinese().Translate(normalizedLocalized));
    }

    // Windows OCR's zh-TW recognizer spaces out CJK characters; Normalize folds the gaps, and the
    // folded string must then hit the locale's contiguous key exactly.
    [Fact]
    public void Translate_SpacedCjkRead_AfterNormalize_HitsExactKey()
    {
        Assert.Equal("混沌石", NameNormalizer.Normalize("混 沌 石"));
        Assert.Equal("chaos orb", Chinese().Translate(NameNormalizer.Normalize("混 沌 石")));
    }

    // CJK has no downstream fuzzy rescue (Levenshtein against English keys is meaningless), so the
    // translator itself rescues a ONE-ideograph OCR misread: "混沌石" read with 沚→濁 still resolves,
    // unambiguously (every other key here is ≥2 edits away).
    [Fact]
    public void Translate_SingleCharCjkMisread_RescuedByFuzzy()
    {
        Assert.Equal("chaos orb", Chinese().Translate("混濁石"));
        Assert.Equal("ancient rune of decay", Chinese().Translate("遠古腐杇符文"));
    }

    // Ambiguity discipline: when TWO keys sit 1 edit from the read, there is no confident winner —
    // the input is returned unchanged (a visible miss beats a confident wrong price).
    [Fact]
    public void Translate_AmbiguousCjkMisread_ReturnsInputUnchanged()
    {
        // "遠古腐朽符文" and "遠古腐朽符文二" are both 1 edit from the fragment (insert/lose the tail).
        Assert.Equal("遠古腐朽符文三", Chinese().Translate("遠古腐朽符文三"));
    }

    // Latin locales have no CJK keys: a CJK read they can't translate passes through untouched
    // (no rescue layer fires, no exception) so the English matcher can try its chain.
    [Fact]
    public void Translate_CjkInput_NoCjkKeys_PassesThrough()
    {
        Assert.Equal("混沌石", German().Translate("混沌石"));
    }

    // The full string pipeline a live zh-TW row goes through — stack-count strip → normalize (which
    // folds the OCR'd CJK spacing) → leading-noise strip → translate — must land on the English key.
    // ("3x 混 沌 石（3）" is how the zh-TW exchange panel actually renders "3x Chaos Orb (3)".)
    [Fact]
    public void ZhTwRow_FullStringPipeline_ResolvesEnglishKey()
    {
        var raw = "3x 混 沌 石（3）";
        var normalized = OcrScanner.StripLeadingNoise(
            NameNormalizer.Normalize(OcrScanner.StripTrailingStackCount(raw)));
        Assert.Equal("混沌石", normalized);
        Assert.Equal("chaos orb", Chinese().Translate(normalized));
    }
}
