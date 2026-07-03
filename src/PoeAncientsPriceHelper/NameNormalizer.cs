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

    public static string Normalize(string text)
    {
        var s = text.ToLowerInvariant();
        s = NonWordSpace.Replace(s, " ");
        s = MultiSpace.Replace(s, " ");
        return s.Trim();
    }

    // Fold Latin diacritics to their ASCII base (ä→a, ß→ss, é→e, ñ→n, ç→c, …) so a localized name
    // still matches when OCR drops or mangles the accent — a very common failure on the stylised
    // panel font (e.g. "Chaossphäre" read as "chaossphare", "Große" as "grosse"). Cyrillic/Greek and
    // other non-Latin scripts pass through untouched (their OCR either reads cleanly or not at all).
    // Input should already be Normalize()d. Used by NameTranslator's localized→English matching.
    public static string Fold(string normalized)
    {
        var sb = new StringBuilder(normalized.Length);
        foreach (char c in normalized)
        {
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
