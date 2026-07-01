using PoeAncientsPriceHelper;

namespace PoeAncientsPriceHelper.Tests;

// End-to-end checks against the REAL shipped locale files (mirrored into the test output by the
// csproj). Guards against a malformed/renamed file or a regression in the loader — the unit tests in
// NameTranslatorTests cover the matching logic with synthetic data.
public class LocaleFilesTests
{
    private static string LocalesDir => Path.Combine(AppContext.BaseDirectory, "locales");

    private static NameTranslator Load() => NameTranslator.LoadFromDirectories([LocalesDir]);

    [Fact]
    public void BundledLocales_LoadWithoutError_AndHaveEntries()
    {
        var t = Load();
        Assert.True(t.HasEntries);
        Assert.True(t.EntryCount > 400, $"expected a few hundred merged entries, got {t.EntryCount}");
    }

    // The exact items from issue #29's German debug log must now resolve to their English price keys.
    [Theory]
    [InlineData("chaossphäre", "chaos orb")]
    [InlineData("große sphäre des goldschmieds", "greater jeweller s orb")]
    public void BundledLocales_ResolveIssue29GermanItems(string localized, string expectedKey)
    {
        Assert.Equal(expectedKey, Load().Translate(localized));
    }

    // One verified name per shipped language resolves (de/pt/ru/sp all loaded and merged).
    [Theory]
    [InlineData("chaossphäre")]        // de
    [InlineData("orbe du chaos")]      // fr
    [InlineData("orbe do caos")]       // pt
    [InlineData("сфера хаоса")]        // ru (Cyrillic)
    [InlineData("orbe de caos")]       // sp
    public void BundledLocales_EachLanguageResolvesChaosOrb(string localizedChaosOrb)
    {
        Assert.Equal("chaos orb", Load().Translate(localizedChaosOrb));
    }

    // Issue #40: the OP's Russian client OCR'd clean Cyrillic names but every row MISSed because the
    // exchange panel's trailing stack marker "(N)" — with the count digit misread as a Cyrillic "З" —
    // survived into the name and broke the EXACT translation lookup. The full raw→strip→normalize→
    // translate pipeline must now resolve these to their English price keys. Raw lines are verbatim
    // from the issue's debug log.
    [Theory]
    [InlineData("Совершенная сфера хаоса (З)", "perfect chaos orb")]
    [InlineData("Совершенная сфера возвышения (З)", "perfect exalted orb")]
    [InlineData("Сфера отмены (3)", "orb of annulment")]
    [InlineData("Совершенная сфера царей (З)", "perfect regal orb")]
    [InlineData("Совершенная сфера превращения (З)", "perfect orb of transmutation")]
    [InlineData("Совершенная сфера усиления (З)", "perfect orb of augmentation")]
    public void BundledRu_ResolvesIssue40RowsThroughFullPipeline(string rawOcrLine, string expectedKey)
    {
        var normalized = OcrScanner.StripLeadingNoise(
            NameNormalizer.Normalize(OcrScanner.StripTrailingStackCount(rawOcrLine)));
        Assert.Equal(expectedKey, NameTranslator.ForLanguage("ru").Translate(normalized));
    }

    // The settings dropdown is populated from the files actually present — de/fr/pt/ru/sp, never "en".
    [Fact]
    public void AvailableLocales_ListsTheSeededLanguages()
    {
        var locales = NameTranslator.AvailableLocales([LocalesDir]);
        var codes = locales.Select(l => l.Code).ToHashSet();
        Assert.Contains("de", codes);
        Assert.Contains("fr", codes);
        Assert.Contains("pt", codes);
        Assert.Contains("ru", codes);
        Assert.Contains("sp", codes);
        Assert.DoesNotContain("en", codes);
        Assert.All(locales, l => Assert.False(string.IsNullOrWhiteSpace(l.DisplayName)));
    }

    // ForLanguage loads ONLY the selected language; "en" / unknown codes are a no-op identity.
    [Fact]
    public void ForLanguage_LoadsSelectedLanguageOnly()
    {
        Assert.Equal("chaos orb", NameTranslator.ForLanguage("de").Translate("chaossphäre"));
        Assert.False(NameTranslator.ForLanguage("en").HasEntries);
        Assert.False(NameTranslator.ForLanguage("zz").HasEntries);   // no such file
        Assert.Equal("chaossphäre", NameTranslator.ForLanguage("en").Translate("chaossphäre")); // identity
    }
}
