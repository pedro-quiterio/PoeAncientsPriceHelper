using Newtonsoft.Json;

namespace PoeAncientsPriceHelper;

// Maps a localized item name (read off a non-English PoE 2 client) to its canonical English name,
// so the price lookup — whose keys come from poe.ninja's English-only API — works for German,
// Portuguese, Russian, Spanish, … clients. Without this, every row on a localized client is a MISS
// (issue #29: "Chaossphäre" never matches "chaos orb").
//
// Locale files are English-keyed JSON (English name → localized name); the translator inverts them
// into normalized localized → English-key. Matching is exact → diacritic-folded: folding rescues the
// common OCR slip of dropping/mangling an accent ("Chaossphäre" read as "chaossphare"). A name with
// no translation is returned unchanged, so an English client (or an already-English row) is a no-op
// pass-through.
//
// Only the ONE language the user selects (Settings → Game language) is loaded; English is the default
// and loads nothing, so an English client does zero work. Deliberately NO glyph-skeleton step here:
// folding (o↔c↔e, n↔m↔u …) is aggressive enough to map one item onto another, and the accent-drop case
// it would catch is already covered by Fold. For LATIN scripts that discipline is safe because
// ScanEngine's fuzzy matcher absorbs glyph-level OCR slips on the RESOLVED ENGLISH key. CJK has no
// such downstream rescue — a Levenshtein distance between a Chinese string and an English key is
// meaningless — so the translator itself carries the one rescue layer CJK gets: a ≤1-edit match
// against the locale's CJK keys (see CjkFuzzyKey), with the same ambiguity-drops discipline as
// BuildCollapsed.
internal sealed class NameTranslator
{
    // All keys are NameNormalizer.Normalize()d; values are the English price KEY (also normalized,
    // so they match PriceRepository's dictionary directly).
    private readonly Dictionary<string, string> _exact;      // normalized localized → english key
    private readonly Dictionary<string, string> _folded;     // Fold(localized)      → english key
    // The subset of exact keys made of CJK ideographs, for the ≤1-edit OCR-misread rescue. Null when
    // the locale has no CJK entries (every Latin locale) so Translate skips the scan entirely.
    private readonly List<string>? _cjkKeys;

    public int EntryCount => _exact.Count;
    public bool HasEntries => _exact.Count > 0;

    // An empty translator: Translate() is an identity pass-through. Used on English clients / when no
    // locale files are present.
    public static NameTranslator Empty { get; } = new(new Dictionary<string, string>());

    // Where locale files live: the bundled locales\ folder (shipped next to the exe) and the user's
    // %LocalAppData%\PoeAncientsPriceHelper\locales\ (drop-in contributions / overrides, loaded last).
    private static IEnumerable<string> DefaultDirectories =>
        [Path.Combine(AppContext.BaseDirectory, "locales"), Path.Combine(AppPaths.DataDir, "locales")];

    // A locale offered in the settings "Game language" dropdown.
    public sealed record LocaleInfo(string Code, string DisplayName);

    // Build the translator for ONE selected client language. English ("en"), empty, or an unknown code
    // → the identity Empty translator (no translation), so the default English client does zero work and
    // carries no risk of a foreign entry shadowing an English row. Only that language's file is loaded
    // (bundled + any user override of the same code), which keeps the match set small and predictable —
    // unlike merging every language at once.
    public static NameTranslator ForLanguage(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Equals("en", StringComparison.OrdinalIgnoreCase))
            return Empty;

        var pairs = new List<(string, string)>();
        foreach (var dir in DefaultDirectories)
        {
            var file = Path.Combine(dir, code + ".json");
            if (!File.Exists(file)) continue;
            try
            {
                var locale = JsonConvert.DeserializeObject<LocaleFile>(File.ReadAllText(file));
                if (locale?.Entries is null) continue;
                foreach (var (english, localized) in locale.Entries)
                    if (!string.IsNullOrWhiteSpace(localized))
                        pairs.Add((english, localized));
            }
            catch { /* a broken contribution must never crash scanning — skip it */ }
        }
        return FromPairs(pairs);
    }

    // Discover the locale files present (bundled + user) so the settings UI can list the languages the
    // app can actually translate. Deduped by code (a user file shadows the bundled one). English is NOT
    // returned — it's the no-translation default the UI prepends itself.
    public static IReadOnlyList<LocaleInfo> AvailableLocales() => AvailableLocales(DefaultDirectories);

    public static IReadOnlyList<LocaleInfo> AvailableLocales(IEnumerable<string> directories)
    {
        var byCode = new Dictionary<string, LocaleInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in directories)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var locale = JsonConvert.DeserializeObject<LocaleFile>(File.ReadAllText(file));
                    var code = locale?.Code ?? Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(code) || code.Equals("en", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var name = string.IsNullOrWhiteSpace(locale?.Language) ? code : locale!.Language!;
                    byCode[code] = new LocaleInfo(code, name);
                }
                catch { /* skip malformed files */ }
            }
        }
        return byCode.Values.OrderBy(l => l.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private NameTranslator(Dictionary<string, string> exact)
    {
        _exact = exact;

        // Diacritic-folded index. A folded key that two DIFFERENT English items collapse onto is
        // ambiguous — drop it rather than risk a confident wrong match (the exact map still covers
        // the clean read). Same-target collisions are harmless and kept.
        _folded = BuildCollapsed(exact, NameNormalizer.Fold);

        // CJK keys for the ≤1-edit misread rescue. 3+ ideographs required: a 2-char key one edit
        // away from a random fragment is too loose a race to win safely.
        var cjk = exact.Keys.Where(k => k.Count(NameNormalizer.IsCjkIdeograph) >= 3).ToList();
        _cjkKeys = cjk.Count > 0 ? cjk : null;
    }

    private static Dictionary<string, string> BuildCollapsed(
        Dictionary<string, string> exact, Func<string, string> collapse)
    {
        var result = new Dictionary<string, string>(exact.Count);
        var ambiguous = new HashSet<string>();
        foreach (var (loc, en) in exact)
        {
            var key = collapse(loc);
            if (ambiguous.Contains(key)) continue;
            if (result.TryGetValue(key, out var existing))
            {
                if (existing != en) { result.Remove(key); ambiguous.Add(key); }
            }
            else result[key] = en;
        }
        return result;
    }

    // Resolve an OCR'd, already-normalized name to its English price key. Returns the input unchanged
    // when no translation applies (English client, or an item we have no mapping for) so the caller
    // can feed the result straight into the existing English matcher.
    public string Translate(string normalizedName)
    {
        if (_exact.Count == 0 || string.IsNullOrEmpty(normalizedName)) return normalizedName;
        if (_exact.TryGetValue(normalizedName, out var en)) return en;
        if (_folded.TryGetValue(NameNormalizer.Fold(normalizedName), out en)) return en;
        if (_cjkKeys is not null && normalizedName.Any(NameNormalizer.IsCjkIdeograph) &&
            CjkFuzzyKey(normalizedName) is { } rescued)
            return _exact[rescued];
        return normalizedName;
    }

    // Max Levenshtein edits accepted between an OCR'd CJK name and a locale key. 1 — a single
    // substitution/insertion/deletion, the typical stylised-font misread ("混沌石" read with one
    // wrong ideograph). Unlike the Latin fuzzy (a similarity ratio), a raw edit budget is the right
    // measure here: 1 edit on a 3-char CJK name is a similarity of 0.67, far below the Latin
    // thresholds, yet with only a few hundred CJK keys an unambiguous 1-edit hit is conclusive.
    private const int CjkFuzzyMaxDistance = 1;

    // Closest CJK locale key to an OCR'd name, or null. A hit must be UNIQUE: if two keys sit at the
    // same minimal distance (or two different keys are both ≤1 edit away) the read is ambiguous and
    // nothing is returned — same discipline as BuildCollapsed, a confident wrong match is worse than
    // a visible miss.
    private string? CjkFuzzyKey(string normalized)
    {
        string? best = null;
        int bestDist = CjkFuzzyMaxDistance + 1;
        bool ambiguous = false;
        foreach (var key in _cjkKeys!)
        {
            // Levenshtein ≤1 requires near-equal lengths; skip the scan for the far buckets.
            if (Math.Abs(key.Length - normalized.Length) > CjkFuzzyMaxDistance) continue;
            int dist = ScanEngine.Levenshtein(normalized, key);
            if (dist > CjkFuzzyMaxDistance) continue;
            if (dist < bestDist)
            {
                bestDist = dist;
                best = key;
                ambiguous = false;
            }
            else ambiguous = true;
        }
        return ambiguous ? null : best;
    }

    // Build directly from English→localized pairs (used by tests and as the merge primitive).
    public static NameTranslator FromPairs(IEnumerable<(string English, string Localized)> pairs)
    {
        var exact = new Dictionary<string, string>();
        foreach (var (english, localized) in pairs)
        {
            var enKey = NameNormalizer.Normalize(english);
            var locKey = NameNormalizer.Normalize(localized);
            if (enKey.Length == 0 || locKey.Length == 0) continue;
            // Last writer wins on a localized-key collision (e.g. a user file overriding a bundled
            // one). Distinct items practically never share a localized name.
            exact[locKey] = enKey;
        }
        return exact.Count == 0 ? Empty : new NameTranslator(exact);
    }

    // Load every <code>.json locale file found under the given directories and merge them into one
    // translator. Later directories override earlier ones key-by-key, so a user file in DataDir\locales
    // can correct a bundled entry. Missing directories and malformed files are skipped (best-effort:
    // a broken contribution must never crash scanning).
    public static NameTranslator LoadFromDirectories(IEnumerable<string> directories, Action<string>? log = null)
    {
        var pairs = new List<(string, string)>();
        foreach (var dir in directories)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    var locale = JsonConvert.DeserializeObject<LocaleFile>(File.ReadAllText(file));
                    if (locale?.Entries is null) continue;
                    foreach (var (english, localized) in locale.Entries)
                        if (!string.IsNullOrWhiteSpace(localized))
                            pairs.Add((english, localized));
                    log?.Invoke($"locale {Path.GetFileName(file)}: {locale.Entries.Count} entries");
                }
                catch (Exception ex)
                {
                    log?.Invoke($"locale {Path.GetFileName(file)} skipped: {ex.Message}");
                }
            }
        }
        return FromPairs(pairs);
    }

    // The on-disk locale schema. English-keyed so a contributor can diff a file against the English
    // reference list and see at a glance which items still need a translation.
    private sealed class LocaleFile
    {
        public string? Language { get; set; }
        public string? Code { get; set; }
        public string? Source { get; set; }
        public string? Note { get; set; }
        public Dictionary<string, string>? Entries { get; set; }
    }
}
