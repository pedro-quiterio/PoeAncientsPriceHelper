using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

// Regression coverage for the Russian OCR matching fixes (#66): Cyrillic and "×" stack markers,
// composed vs decomposed "ё", mixed Latin/Cyrillic lookalikes, and a localized uncut-gem name whose
// exact level must be pinned separately from its base name. Contributed by @Zatyp-Tema.
public class RussianOcrRegressionTests
{
    private static NameTranslator Russian() => NameTranslator.FromPairs(
    [
        ("Chaos Orb", "Сфера хаоса"),
        ("Chilling Flux", "Студёный расплав"),
        ("Uncut Spirit Gem", "Неогранённый камень духа"),
    ]);

    private static string Resolve(string raw) => Russian().Translate(
        OcrScanner.StripLeadingNoise(NameNormalizer.Normalize(OcrScanner.StripTrailingStackCount(raw))));

    [Theory]
    [InlineData("Студеный расплав", "chilling flux")]          // е read for ё
    [InlineData("Студёный расплав", "chilling flux")]    // decomposed ё (е + combining diaeresis)
    [InlineData("Cфера xаоcа", "chaos orb")]                   // Latin C/x/c read for Cyrillic С/х/с
    [InlineData("14хСфера хаоса", "chaos orb")]                // glued Cyrillic marker
    [InlineData("14× Сфера хаоса (З)", "chaos orb")]           // × marker + bracketed stack count
    [InlineData("Сфера хаоса х1", "chaos orb")]                // trailing Cyrillic marker
    [InlineData("Сфера хаоса хЗ", "chaos orb")]                // trailing marker, count digit read as З
    public void LocalizedOcrVariantsResolve(string raw, string expected) => Assert.Equal(expected, Resolve(raw));

    [Theory]
    [InlineData("14x Сфера хаоса")]
    [InlineData("14х Сфера хаоса")]
    [InlineData("14× Сфера хаоса")]
    [InlineData("Сфера хаоса х14")]
    [InlineData("Сфера хаоса ×14")]
    public void QuantityVariantsPreserveCount(string raw) =>
        Assert.Equal((14, true), OcrScanner.ExtractMultiplierWithConfidence(NameNormalizer.Normalize(raw)));

    [Theory]
    [InlineData("(Уровень 19)")]
    [InlineData("(Ур. 19)")]
    [InlineData("19 уровня")]
    [InlineData("(Level 19)")]
    public void LocalizedGemLevelIsPreserved(string suffix)
    {
        var translated = Resolve($"2х Неограненный камень духа {suffix} (1)");
        Assert.True(ScanEngine.TryResolveGemKey(translated, out var key));
        Assert.Equal("uncut spirit gem level 19", key);
    }

    [Fact]
    public void UnreadableGemLevelIsNotGuessed()
    {
        Assert.True(ScanEngine.TryResolveGemKey(Resolve("Неограненный камень духа (Уровень l9)"), out var key));
        Assert.Null(key);
    }

    [Fact]
    public void AmbiguousFoldedNameIsNotGuessed()
    {
        var translator = NameTranslator.FromPairs([("Item A", "лёд"), ("Item B", "лед")]);
        Assert.Equal("лeд", translator.Translate("лeд"));
    }
}
