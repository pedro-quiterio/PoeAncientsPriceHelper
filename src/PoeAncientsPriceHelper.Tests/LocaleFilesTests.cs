using System.Text.Json;
using System.Text.RegularExpressions;
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

    // ---- Seed data-integrity guards -------------------------------------------------------------
    // The bundled locale seeds were produced by an external poe2db bulk-scrape whose output was
    // committed as-is; that scrape misaligned rows, leaking values onto the wrong keys and across
    // languages (Spanish text in de.json, German in sp.json, a literal English item name as an sp
    // value, un-translated "Key_With_Underscores" fallbacks…). Every instance found was fixed in
    // v3.5.2–v3.5.5. These four checks encode the scans that caught them so a future re-generation
    // or hand-edit that reintroduces the same class of corruption fails CI instead of shipping — the
    // spot-check translation tests above never would have. A legitimate future collision can be
    // carved out explicitly; treat a failure as "prove this isn't a leak" first.

    // code -> (English key -> localized value), read straight from the shipped JSON.
    private static Dictionary<string, Dictionary<string, string>> LoadRawLocales()
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        foreach (var path in Directory.EnumerateFiles(LocalesDir, "*.json"))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var code = doc.RootElement.GetProperty("code").GetString()!;
            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var e in doc.RootElement.GetProperty("entries").EnumerateObject())
                entries[e.Name] = e.Value.GetString()!;
            result[code] = entries;
        }
        return result;
    }

    // Guard the guards: the four data-integrity checks below all pass vacuously if LoadRawLocales
    // reads nothing (a broken glob/parse), so assert it actually sees the five populated seed files.
    [Fact]
    public void LoadRawLocales_SeesAllFiveSeedFilesWithEntries()
    {
        var locales = LoadRawLocales();
        Assert.Equal(new[] { "de", "fr", "pt", "ru", "sp" }, locales.Keys.OrderBy(c => c, StringComparer.Ordinal));
        Assert.All(locales.Values, e => Assert.True(e.Count > 100, $"seed file unexpectedly small ({e.Count} entries)"));
    }

    // A value like "Aldurs_Saga" or "Liquid_Verisium" is the mangled English key the scrape emitted
    // when it had no translation — never a real localized name.
    [Fact]
    public void BundledLocales_HaveNoUnderscoreFallbackValues()
    {
        var fallback = new Regex(@"^[A-Za-z]+(_[A-Za-z']+)+$");
        var offenders =
            (from kv in LoadRawLocales()
             from e in kv.Value
             where fallback.IsMatch(e.Value)
             select $"{kv.Key}: '{e.Key}' = '{e.Value}'").ToList();

        Assert.True(offenders.Count == 0,
            "Locale values look like un-translated 'Key_With_Underscores' fallbacks:\n" +
            string.Join("\n", offenders));
    }

    // A value that is verbatim the English name of a DIFFERENT item is contamination, not a
    // translation (e.g. sp "Orb of Chance" once held the literal string "Perfect Flux"). A value that
    // equals its OWN English key is fine — that's a legitimate untranslated passthrough.
    [Fact]
    public void BundledLocales_NoValueIsAnotherItemsEnglishName()
    {
        var locales = LoadRawLocales();
        var englishKeys = locales.Values.SelectMany(e => e.Keys).ToHashSet(StringComparer.Ordinal);
        var offenders =
            (from kv in locales
             from e in kv.Value
             where e.Value != e.Key && englishKeys.Contains(e.Value)
             select $"{kv.Key}: '{e.Key}' = '{e.Value}'  (that is another item's English name)").ToList();

        Assert.True(offenders.Count == 0,
            "Locale values contain a literal English item name copied from a different key:\n" +
            string.Join("\n", offenders));
    }

    // If one localized string is mapped to two DIFFERENT English items, one of them is a leaked value
    // from the wrong row (this caught the Spanish-in-German and Portuguese-in-Spanish cases: e.g. de
    // "Robust Rune" held "Orbe de transmutación", the sp value for "Orb of Transmutation").
    [Fact]
    public void BundledLocales_NoValueMapsToTwoDifferentItems()
    {
        var keysByValue = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var siteByValue = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (code, entries) in LoadRawLocales())
            foreach (var (key, value) in entries)
            {
                if (!keysByValue.TryGetValue(value, out var keys)) keysByValue[value] = keys = new(StringComparer.Ordinal);
                keys.Add(key);
                if (!siteByValue.TryGetValue(value, out var sites)) siteByValue[value] = sites = new();
                sites.Add($"{code}:{key}");
            }
        var offenders = keysByValue
            .Where(kv => kv.Value.Count > 1)
            .Select(kv => $"'{kv.Key}' -> {kv.Value.Count} different items: {string.Join(", ", siteByValue[kv.Key])}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "One localized value is mapped to two different English items (row misalignment / cross-contamination):\n" +
            string.Join("\n", offenders));
    }

    // Russian is written in Cyrillic; a ru value with no Cyrillic letter is a wrong-language leak
    // (this caught the Spanish strings that landed in ru.json). An untranslated English passthrough
    // (value == its own key) is allowed.
    [Fact]
    public void BundledRu_ValuesAreCyrillicOrUntranslatedPassthrough()
    {
        static bool HasCyrillic(string s) => s.Any(c => c is >= 'Ѐ' and <= 'ӿ');
        var offenders =
            (from e in LoadRawLocales()["ru"]
             where e.Value != e.Key && !HasCyrillic(e.Value)
             select $"ru: '{e.Key}' = '{e.Value}'").ToList();

        Assert.True(offenders.Count == 0,
            "Russian values have no Cyrillic and aren't an untranslated English passthrough (likely a wrong-language leak):\n" +
            string.Join("\n", offenders));
    }
}
