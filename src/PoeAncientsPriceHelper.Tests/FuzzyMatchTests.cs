using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

public class FuzzyMatchTests
{
    [Theory]
    [InlineData("", "", 0)]
    [InlineData("abc", "abc", 0)]
    [InlineData("abc", "abd", 1)]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("vision", "viswn", 2)]
    public void Levenshtein_ComputesEditDistance(string a, string b, int expected)
    {
        Assert.Equal(expected, ScanEngine.Levenshtein(a, b));
    }

    // Real misreads from the scan log should clear the 0.84 fuzzy threshold against the
    // correct key, while an unrelated item should not.
    [Theory]
    [InlineData("greater viswn rune", "greater vision rune", true)]
    [InlineData("greater reblrth rune", "greater rebirth rune", true)]
    [InlineData("grgater inspiration rune", "greater inspiration rune", true)]
    [InlineData("greater vision rune", "greater rebirth rune", false)] // different item, must NOT match
    public void Similarity_AbsorbsMisreadsButNotWrongItems(string ocr, string key, bool shouldMatch)
    {
        int dist = ScanEngine.Levenshtein(ocr, key);
        double score = 1.0 - (double)dist / System.Math.Max(ocr.Length, key.Length);
        Assert.Equal(shouldMatch, score > 0.84);
    }

    // Digits OCR substituted for letters (O→0, l→1, S→5, B→8) fold back so the name matches its key.
    [Theory]
    [InlineData("01roth s saga", "olroth s saga")]                       // O→0, l→1
    [InlineData("01roth s crest of the sun", "olroth s crest of the sun")]
    [InlineData("5igil of power", "sigil of power")]                     // S→5
    [InlineData("8reach ring", "breach ring")]                          // B→8
    [InlineData("no digits at all", "no digits at all")]                 // unchanged when clean
    public void DigitFold_RecoversLettersMisreadAsDigits(string ocr, string expected)
    {
        Assert.Equal(expected, NameNormalizer.DigitFold(ocr));
    }

    // Uncut gems are pinned by type + level (no fuzzy), so the canonical key must carry both exactly.
    [Theory]
    [InlineData("uncut spirit gem level 19", "uncut spirit gem level 19")]
    [InlineData("uncut skill gem level 7", "uncut skill gem level 7")]
    [InlineData("uncut support gem level 3", "uncut support gem level 3")]
    // Boilerplate slips ("uncot", "ger", "levei") don't hide a gem or change the pinned key.
    [InlineData("uncot spirit gem level 19", "uncut spirit gem level 19")]
    public void TryResolveGemKey_PinsTypeAndLevel(string ocr, string expectedKey)
    {
        Assert.True(ScanEngine.TryResolveGemKey(ocr, out var key));
        Assert.Equal(expectedKey, key);
    }

    // The rune-combination panel spells an uncut-skill-gem reward as "Skill Level N: <skill>" (no "gem"
    // word), so it must pin to the same uncut-skill-gem key as the exchange panel's "uncut skill gem
    // level N". Every skill at a level is the same uncut gem, so the trailing skill name is ignored.
    // Strings below are the exact OCR-normalized forms produced from the #59 report screenshots (gkabos).
    [Theory]
    [InlineData("skill level 20 skyfall", "uncut skill gem level 20")]
    [InlineData("skill level 20 triskelion cascade", "uncut skill gem level 20")]
    [InlineData("skill level 20 animus exchange", "uncut skill gem level 20")]
    [InlineData("skill level 7 leylines", "uncut skill gem level 7")]
    public void TryResolveGemKey_RuneCombinationSkillReward_PinsUncutSkillGem(string ocr, string expectedKey)
    {
        Assert.True(ScanEngine.TryResolveGemKey(ocr, out var key));
        Assert.Equal(expectedKey, key);
    }

    // A gem whose level can't be read is still recognised as a gem (so it never falls through to
    // fuzzy), but yields no key → the row shows '?' instead of guessing a neighbouring level.
    [Fact]
    public void TryResolveGemKey_GemWithoutLevel_RecognisedButNoKey()
    {
        Assert.True(ScanEngine.TryResolveGemKey("uncut spirit gem", out var key));
        Assert.Null(key);
    }

    // Non-gem names are left for the normal exact/prefix/fuzzy path. "support healing runes" is the
    // rune-combination SUPPORT reward — deliberately not pinned: it carries no level, and poe.ninja
    // prices support gems only per level, so there's nothing to pin (stays unhandled, not a skill gem).
    [Theory]
    [InlineData("greater vision rune")]
    [InlineData("exalted orb")]
    [InlineData("support healing runes")]
    public void TryResolveGemKey_NonGem_ReturnsFalse(string ocr)
    {
        Assert.False(ScanEngine.TryResolveGemKey(ocr, out var key));
        Assert.Null(key);
    }
}
