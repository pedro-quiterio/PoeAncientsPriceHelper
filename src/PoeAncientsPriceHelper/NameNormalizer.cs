using System.Text;
using System.Text.RegularExpressions;

namespace PoeAncientsPriceHelper;

// Shared name normalization used by both OcrScanner (OCR text → key) and PriceRepository
// (API name → key). Extracted so both paths use identical logic and a single set of
// pre-compiled regex instances.
internal static class NameNormalizer
{
    private static readonly Regex NonWordSpace = new(@"[^\w\s]", RegexOptions.Compiled);
    private static readonly Regex MultiSpace = new(@"\s+", RegexOptions.Compiled);
    // The multiplication sign "×" is a stack marker on a localized client ("14× Сфера хаоса"), but it
    // is a non-word char, so NonWordSpace below would turn it into a space and lose the quantity before
    // the multiplier parser ever sees it. When it sits next to a digit, rewrite it to "x" first so the
    // marker survives. (Cyrillic "х" is a letter and passes through NonWordSpace on its own.)
    private static readonly Regex MultSign = new(@"(?<=\d)\s*×|×(?=\s*\d)", RegexOptions.Compiled);
    // Whitespace between two CJK ideographs is never meaningful (CJK names carry no spaces), but
    // Windows OCR's zh-TW recognizer often emits one anyway — "混 沌 石" for 混沌石. Left in, the
    // gaps break the exact localized→English translation lookup (the zh-TW.json keys are contiguous)
    // and shred the name for OcrScanner's leading-noise strip. Folded away here so a spaced-out CJK
    // read lines up with the locale file. Only CJK↔CJK gaps close: "3x 混 沌 石" keeps its "3x ".
    private static readonly Regex CjkGap = new(@"(?<=[\u4E00-\u9FFF])\s+(?=[\u4E00-\u9FFF])", RegexOptions.Compiled);

    public static string Normalize(string text)
    {
        // Compose to a canonical form first so a combining accent OCR emits separately (e.g. Russian
        // "ё" as е + U+0308) collapses to the single precomposed character the locale keys use.
        var s = text.Normalize(NormalizationForm.FormC).ToLowerInvariant();
        s = MultSign.Replace(s, "x");
        s = NonWordSpace.Replace(s, " ");
        s = MultiSpace.Replace(s, " ");
        s = CjkGap.Replace(s, "");
        return s.Trim();
    }

    // True for a CJK unified ideograph (U+4E00–U+9FFF), the range every zh-TW item name draws from.
    // Used by the OCR row gates (one ideograph weighs about a Latin word of meaning) and to spot
    // CJK input that needs the translator's fuzzy rescue.
    internal static bool IsCjkIdeograph(char c) => c is >= '\u4E00' and <= '\u9FFF';

    // Fold Latin diacritics to their ASCII base (ä→a, ß→ss, é→e, ñ→n, ç→c, …) so a localized name
    // still matches when OCR drops or mangles the accent — a very common failure on the stylised
    // panel font (e.g. "Chaossphäre" read as "chaossphare", "Große" as "grosse"). For a Cyrillic name,
    // instead fold "ё"→"е" and the Latin glyphs OCR substitutes for identical-looking Cyrillic letters
    // (a→а, c→с, x→х, …), so a mixed-script misread like "Cфера xаоcа" still lines up with its Russian
    // key. The folded index is collision-checked in NameTranslator (ambiguous folds are dropped), so no
    // approximate item guessing happens. Input should already be Normalize()d. Used by NameTranslator's
    // localized→English matching.
    public static string Fold(string normalized)
    {
        var sb = new StringBuilder(normalized.Length);
        // A single Cyrillic letter marks the whole name as Cyrillic: OCR reads most of it in Cyrillic
        // and only slips the odd letter to its Latin twin, so fold the Latin twins back to Cyrillic.
        bool cyrillic = normalized.Any(c => c is >= 'Ѐ' and <= 'ӿ');
        foreach (char c in normalized)
        {
            if (cyrillic)
            {
                sb.Append(c switch
                {
                    'ё' => 'е', 'a' => 'а', 'b' => 'в', 'c' => 'с',
                    'e' => 'е', 'h' => 'н', 'k' => 'к', 'm' => 'м',
                    'o' => 'о', 'p' => 'р', 't' => 'т', 'x' => 'х', 'y' => 'у',
                    _ => c,
                });
                continue;
            }
            switch (c)
            {
                case 'ä': case 'à': case 'á': case 'â': case 'ã': case 'å': sb.Append('a'); break;
                case 'ö': case 'ò': case 'ó': case 'ô': case 'õ': case 'ø': sb.Append('o'); break;
                case 'ü': case 'ù': case 'ú': case 'û': sb.Append('u'); break;
                case 'é': case 'è': case 'ê': case 'ë': sb.Append('e'); break;
                case 'í': case 'ì': case 'î': case 'ï': sb.Append('i'); break;
                case 'ñ': sb.Append('n'); break;
                case 'ç': sb.Append('c'); break;
                case 'ý': case 'ÿ': sb.Append('y'); break;
                case 'ß': sb.Append("ss"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    // Map digits that Windows OCR substitutes for visually identical letters in the stylised panel
    // font (O→0, l/I→1, S→5, B→8) back to those letters, so a name whose leading letters were read as
    // digits still resolves — e.g. "Olroth's" read as "01roth's" (#43). Price keys carry no digits at
    // resolution time (stack markers are stripped and gem levels pinned upstream), so folding every
    // such digit can't corrupt a legitimate one. Input should already be Normalize()d. Used by the
    // price resolver as a fallback for the exact + fuzzy lookups.
    public static string DigitFold(string normalized)
    {
        var sb = new StringBuilder(normalized.Length);
        foreach (char c in normalized)
            sb.Append(c switch
            {
                '0' => 'o',
                '1' => 'l',
                '5' => 's',
                '8' => 'b',
                _ => c,
            });
        return sb.ToString();
    }

    // Collapse glyphs the stylised PoE panel font / Windows OCR confuse into canonical classes
    // (n/m/u→n, r/v→r, …), so a systematically garbled read still lines up with the true text. Used by
    // the rumour matcher (name → key) and the panel detector (excluding garbled boilerplate). Input
    // should already be Normalize()d.
    public static string Skeleton(string normalized)
    {
        var sb = new StringBuilder(normalized.Length);
        foreach (char c in normalized)
            sb.Append(c switch
            {
                'w' or 'm' or 'n' or 'u' => 'n',
                'r' or 'v' => 'r',
                'i' or 'l' or 'j' or 't' => 'i',
                'o' or '0' or 'e' or 'c' => 'o',
                '4' or 'a' => 'a',
                _ => c,
            });
        return sb.ToString();
    }
}
