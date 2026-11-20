using System.Text;

namespace Managerr.Metadata;

/// <summary>
/// Enforces English or Latin-script (Romaji / transliterated) titles across metadata search and work hydration,
/// with script language inference and deterministic Kana, Hangul, Cyrillic, and Greek romanization.
/// </summary>
public static class LatinTitleNormalizer
{
    private static readonly string[] HangulLead =
    [
        "g", "kk", "n", "d", "tt", "r", "m", "b", "pp",
        "s", "ss", "", "j", "jj", "ch", "k", "t", "p", "h",
    ];

    private static readonly string[] HangulVowel =
    [
        "a", "ae", "ya", "yae", "eo", "e", "yeo", "ye", "o", "wa",
        "wae", "oe", "yo", "u", "wo", "we", "wi", "yu", "eu", "ui", "i",
    ];

    private static readonly string[] HangulTail =
    [
        "", "k", "k", "ks", "n", "nj", "nh", "t", "l", "lk", "lm", "lb",
        "ls", "lt", "lp", "lh", "m", "p", "ps", "t", "t", "ng", "t", "t", "k", "t", "p", "t",
    ];

    private static readonly Dictionary<string, string> HiraganaDigraphs = new(StringComparer.Ordinal)
    {
        ["きゃ"] = "kya", ["きゅ"] = "kyu", ["きょ"] = "kyo",
        ["しゃ"] = "sha", ["しゅ"] = "shu", ["しょ"] = "sho", ["しぇ"] = "she",
        ["ちゃ"] = "cha", ["ちゅ"] = "chu", ["ちょ"] = "cho", ["ちぇ"] = "che",
        ["にゃ"] = "nya", ["にゅ"] = "nyu", ["にょ"] = "nyo",
        ["ひゃ"] = "hya", ["ひゅ"] = "hyu", ["ひょ"] = "hyo",
        ["みゃ"] = "mya", ["みゅ"] = "myu", ["みょ"] = "myo",
        ["りゃ"] = "rya", ["りゅ"] = "ryu", ["りょ"] = "ryo",
        ["ぎゃ"] = "gya", ["ぎゅ"] = "gyu", ["ぎょ"] = "gyo",
        ["じゃ"] = "ja", ["じゅ"] = "ju", ["じょ"] = "jo", ["じぇ"] = "je",
        ["ぢゃ"] = "ja", ["ぢゅ"] = "ju", ["ぢょ"] = "jo",
        ["びゃ"] = "bya", ["びゅ"] = "byu", ["びょ"] = "byo",
        ["ぴゃ"] = "pya", ["ぴゅ"] = "pyu", ["ぴょ"] = "pyo",
        ["てぃ"] = "ti", ["でぃ"] = "di", ["とぅ"] = "tu", ["どぅ"] = "du",
        ["ふぁ"] = "fa", ["ふぃ"] = "fi", ["ふぇ"] = "fe", ["ふぉ"] = "fo",
        ["うぃ"] = "wi", ["うぇ"] = "we", ["うぉ"] = "wo",
        ["ゔぁ"] = "va", ["ゔぃ"] = "vi", ["ゔぇ"] = "ve", ["ゔぉ"] = "vo", ["ゔゅ"] = "vyu",
    };

    private static readonly Dictionary<char, string> HiraganaMonographs = new()
    {
        ['あ'] = "a", ['い'] = "i", ['う'] = "u", ['え'] = "e", ['お'] = "o",
        ['ぁ'] = "a", ['ぃ'] = "i", ['ぅ'] = "u", ['ぇ'] = "e", ['ぉ'] = "o",
        ['か'] = "ka", ['き'] = "ki", ['く'] = "ku", ['け'] = "ke", ['こ'] = "ko",
        ['さ'] = "sa", ['し'] = "shi", ['す'] = "su", ['せ'] = "se", ['そ'] = "so",
        ['た'] = "ta", ['ち'] = "chi", ['つ'] = "tsu", ['て'] = "te", ['と'] = "to",
        ['な'] = "na", ['に'] = "ni", ['ぬ'] = "nu", ['ね'] = "ne", ['の'] = "no",
        ['は'] = "ha", ['ひ'] = "hi", ['ふ'] = "fu", ['へ'] = "he", ['ほ'] = "ho",
        ['ま'] = "ma", ['み'] = "mi", ['む'] = "mu", ['め'] = "me", ['も'] = "mo",
        ['や'] = "ya", ['ゆ'] = "yu", ['よ'] = "yo",
        ['ゃ'] = "ya", ['ゅ'] = "yu", ['ょ'] = "yo",
        ['ら'] = "ra", ['り'] = "ri", ['る'] = "ru", ['れ'] = "re", ['ろ'] = "ro",
        ['わ'] = "wa", ['ゐ'] = "wi", ['ゑ'] = "we", ['を'] = "o", ['ん'] = "n", ['ゎ'] = "wa",
        ['が'] = "ga", ['ぎ'] = "gi", ['ぐ'] = "gu", ['げ'] = "ge", ['ご'] = "go",
        ['ざ'] = "za", ['じ'] = "ji", ['ず'] = "zu", ['ぜ'] = "ze", ['ぞ'] = "zo",
        ['だ'] = "da", ['ぢ'] = "ji", ['づ'] = "zu", ['で'] = "de", ['ど'] = "do",
        ['ば'] = "ba", ['び'] = "bi", ['ぶ'] = "bu", ['べ'] = "be", ['ぼ'] = "bo",
        ['ぱ'] = "pa", ['ぴ'] = "pi", ['ぷ'] = "pu", ['ぺ'] = "pe", ['ぽ'] = "po",
        ['ゔ'] = "vu", ['ゕ'] = "ka", ['ゖ'] = "ke",
    };

    private static readonly Dictionary<char, string> CyrillicAndGreekMap = new()
    {
        // Cyrillic
        ['А'] = "A", ['а'] = "a", ['Б'] = "B", ['б'] = "b", ['В'] = "V", ['в'] = "v",
        ['Г'] = "G", ['г'] = "g", ['Ґ'] = "G", ['ґ'] = "g", ['Д'] = "D", ['д'] = "d",
        ['Е'] = "E", ['е'] = "e", ['Ё'] = "Yo", ['ё'] = "yo", ['Є'] = "Ye", ['є'] = "ye",
        ['Ж'] = "Zh", ['ж'] = "zh", ['З'] = "Z", ['з'] = "z", ['И'] = "I", ['и'] = "i",
        ['І'] = "I", ['і'] = "i", ['Ї'] = "Yi", ['ї'] = "yi", ['Й'] = "Y", ['й'] = "y",
        ['К'] = "K", ['к'] = "k", ['Л'] = "L", ['л'] = "l", ['М'] = "M", ['м'] = "m",
        ['Н'] = "N", ['н'] = "n", ['О'] = "O", ['о'] = "o", ['П'] = "P", ['п'] = "p",
        ['Р'] = "R", ['р'] = "r", ['С'] = "S", ['с'] = "s", ['Т'] = "T", ['т'] = "t",
        ['У'] = "U", ['у'] = "u", ['Ф'] = "F", ['ф'] = "f", ['Х'] = "Kh", ['х'] = "kh",
        ['Ц'] = "Ts", ['ц'] = "ts", ['Ч'] = "Ch", ['ч'] = "ch", ['Ш'] = "Sh", ['ш'] = "sh",
        ['Щ'] = "Shch", ['щ'] = "shch", ['Ъ'] = "", ['ъ'] = "", ['Ы'] = "Y", ['ы'] = "y",
        ['Ь'] = "", ['ь'] = "", ['Э'] = "E", ['э'] = "e", ['Ю'] = "Yu", ['ю'] = "yu",
        ['Я'] = "Ya", ['я'] = "ya",
        // Greek
        ['Α'] = "A", ['α'] = "a", ['Ά'] = "A", ['ά'] = "a", ['Β'] = "V", ['β'] = "v",
        ['Γ'] = "G", ['γ'] = "g", ['Δ'] = "D", ['δ'] = "d", ['Ε'] = "E", ['ε'] = "e",
        ['Έ'] = "E", ['έ'] = "e", ['Ζ'] = "Z", ['ζ'] = "z", ['Η'] = "I", ['η'] = "i",
        ['Ή'] = "I", ['ή'] = "i", ['Θ'] = "Th", ['θ'] = "th", ['Ι'] = "I", ['ι'] = "i",
        ['Ί'] = "I", ['ί'] = "i", ['Κ'] = "K", ['κ'] = "k", ['Λ'] = "L", ['λ'] = "l",
        ['Μ'] = "M", ['μ'] = "m", ['Ν'] = "N", ['ν'] = "n", ['Ξ'] = "X", ['ξ'] = "x",
        ['Ο'] = "O", ['ο'] = "o", ['Ό'] = "O", ['ό'] = "o", ['Π'] = "P", ['π'] = "p",
        ['Ρ'] = "R", ['ρ'] = "r", ['Σ'] = "S", ['σ'] = "s", ['ς'] = "s", ['Τ'] = "T",
        ['τ'] = "t", ['Υ'] = "Y", ['υ'] = "y", ['Ύ'] = "Y", ['ύ'] = "y", ['Φ'] = "F",
        ['φ'] = "f", ['Χ'] = "Ch", ['χ'] = "ch", ['Ψ'] = "Ps", ['ψ'] = "ps", ['Ω'] = "O",
        ['ω'] = "o", ['Ώ'] = "O", ['ώ'] = "o",
    };

    public static bool ContainsLatinLetters(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (IsLatinLetter(ch))
            {
                return true;
            }
        }

        return false;
    }

    public static bool ContainsNonLatinScript(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (var ch in text)
        {
            if (IsNonLatinScriptChar(ch))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsCleanLatinText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = NormalizeFullWidthAndPunctuation(text);
        if (ContainsNonLatinScript(normalized))
        {
            return false;
        }

        return ContainsLatinLetters(normalized) || normalized.Any(char.IsDigit);
    }

    public static string? InferLanguageFromScript(string? text, string? countryHint = null)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var hasKana = false;
        var hasHangul = false;
        var hasCjk = false;
        var hasCyrillic = false;
        var hasArabic = false;
        var hasHebrew = false;
        var hasGreek = false;
        var hasThai = false;

        foreach (var ch in text)
        {
            if (ch is (>= '\u3040' and <= '\u30FF') or (>= '\u31F0' and <= '\u31FF') or (>= '\uFF65' and <= '\uFF9F'))
            {
                hasKana = true;
            }
            else if (ch is (>= '\uAC00' and <= '\uD7AF') or (>= '\u1100' and <= '\u11FF') or (>= '\u3130' and <= '\u318F'))
            {
                hasHangul = true;
            }
            else if (ch is (>= '\u3400' and <= '\u4DBF') or (>= '\u4E00' and <= '\u9FFF') or (>= '\uF900' and <= '\uFAFF'))
            {
                hasCjk = true;
            }
            else if (ch is >= '\u0400' and <= '\u052F')
            {
                hasCyrillic = true;
            }
            else if (ch is (>= '\u0600' and <= '\u06FF') or (>= '\u0750' and <= '\u077F'))
            {
                hasArabic = true;
            }
            else if (ch is >= '\u0590' and <= '\u05FF')
            {
                hasHebrew = true;
            }
            else if (ch is >= '\u0370' and <= '\u03FF')
            {
                hasGreek = true;
            }
            else if (ch is >= '\u0E00' and <= '\u0E7F')
            {
                hasThai = true;
            }
        }

        if (hasKana) return "ja";
        if (hasHangul) return "ko";
        if (hasCjk)
        {
            if (string.Equals(countryHint, "JP", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(countryHint, "JPN", StringComparison.OrdinalIgnoreCase) ||
                text.Contains('々') ||
                text.Contains('・'))
            {
                return "ja";
            }

            return "zh";
        }

        if (hasCyrillic) return "ru";
        if (hasArabic) return "ar";
        if (hasHebrew) return "he";
        if (hasGreek) return "el";
        if (hasThai) return "th";

        return null;
    }

    public static string? NormalizeMusicBrainzSortName(string? sortName)
    {
        if (string.IsNullOrWhiteSpace(sortName))
        {
            return null;
        }

        var clean = NormalizeFullWidthAndPunctuation(sortName);
        if (!IsCleanLatinText(clean))
        {
            clean = TransliterateToLatin(clean);
            if (!IsCleanLatinText(clean))
            {
                return null;
            }
        }

        var commaParts = clean.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (commaParts.Length == 2 && !commaParts[0].Contains('&') && !commaParts[1].Contains('&'))
        {
            // "Beatles, The" -> "The Beatles"; "Mikadono, Kazuki" -> "Kazuki Mikadono"
            return $"{commaParts[1]} {commaParts[0]}".Trim();
        }

        return clean;
    }

    public static string SelectBestEnglishOrLatinTitle(
        IEnumerable<string?> englishCandidates,
        IEnumerable<string?> latinOrRomajiCandidates,
        IEnumerable<string?> fallbackCandidates,
        string fallbackLabel)
    {
        var engList = englishCandidates.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => NormalizeFullWidthAndPunctuation(s!)).ToList();
        var latList = latinOrRomajiCandidates.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => NormalizeFullWidthAndPunctuation(s!)).ToList();
        var anyList = fallbackCandidates.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => NormalizeFullWidthAndPunctuation(s!)).ToList();

        foreach (var c in engList)
        {
            if (IsCleanLatinText(c))
            {
                return c;
            }
        }

        foreach (var c in latList)
        {
            if (IsCleanLatinText(c))
            {
                return c;
            }
        }

        foreach (var c in anyList)
        {
            if (IsCleanLatinText(c))
            {
                return c;
            }
        }

        // Deterministic romanization fallback for Kana, Hangul, Cyrillic, Greek, or mixed Latin+Kana titles
        foreach (var raw in engList.Concat(latList).Concat(anyList))
        {
            var sourceLetters = raw.Count(char.IsLetter);
            var romanized = TransliterateToLatin(raw);
            var romanLetters = romanized.Count(IsLatinLetter);
            if (romanLetters >= 2 && (sourceLetters == 0 || romanLetters * 2 >= sourceLetters))
            {
                return CapitalizeRomanizedWords(romanized);
            }
        }

        return string.IsNullOrWhiteSpace(fallbackLabel) ? "Untitled" : fallbackLabel.Trim();
    }

    public static IReadOnlyList<string> OrderAndCleanAltTitles(string primaryTitle, IEnumerable<string?> rawAltTitles)
    {
        var cleanLatin = new List<string>();
        var transliterated = new List<string>();

        foreach (var raw in rawAltTitles)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var norm = NormalizeFullWidthAndPunctuation(raw);
            if (string.Equals(norm, primaryTitle, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (IsCleanLatinText(norm))
            {
                if (!cleanLatin.Contains(norm, StringComparer.OrdinalIgnoreCase))
                {
                    cleanLatin.Add(norm);
                }
            }
            else
            {
                var sourceLetters = norm.Count(char.IsLetter);
                var rom = TransliterateToLatin(norm);
                var romLetters = rom.Count(IsLatinLetter);
                if (romLetters >= 2 && (sourceLetters == 0 || romLetters * 2 >= sourceLetters))
                {
                    var titled = CapitalizeRomanizedWords(rom);
                    if (!string.Equals(titled, primaryTitle, StringComparison.OrdinalIgnoreCase) &&
                        !cleanLatin.Contains(titled, StringComparer.OrdinalIgnoreCase) &&
                        !transliterated.Contains(titled, StringComparer.OrdinalIgnoreCase))
                    {
                        transliterated.Add(titled);
                    }
                }
            }
        }

        return cleanLatin.Concat(transliterated).ToList();
    }

    public static MetadataWorkCandidate NormalizeCandidate(MetadataWorkCandidate candidate)
    {
        var fallbackLabel = candidate.Vertical switch
        {
            MetadataVerticals.Movies => $"Movie ({candidate.ProviderKey}:{candidate.ProviderItemId})",
            MetadataVerticals.Music => $"Release ({candidate.ProviderKey}:{candidate.ProviderItemId})",
            MetadataVerticals.Anime => $"Anime ({candidate.ProviderKey}:{candidate.ProviderItemId})",
            MetadataVerticals.Tv => $"Series ({candidate.ProviderKey}:{candidate.ProviderItemId})",
            _ => $"Title ({candidate.ProviderKey}:{candidate.ProviderItemId})",
        };

        var primaryTitle = SelectBestEnglishOrLatinTitle(
            englishCandidates: IsCleanLatinText(candidate.Title) ? [candidate.Title] : [],
            latinOrRomajiCandidates: candidate.AltTitles,
            fallbackCandidates: [candidate.Title, ..candidate.AltTitles],
            fallbackLabel: fallbackLabel);

        var orderedAlts = OrderAndCleanAltTitles(primaryTitle, [candidate.Title, ..candidate.AltTitles]);

        var normalizedContainers = candidate.Containers
            .Select(c =>
            {
                var defaultContainerLabel = c.ContainerKind switch
                {
                    "season" => $"Season {c.ContainerNumber}",
                    "volume" => $"Volume {c.ContainerNumber}",
                    _ => primaryTitle,
                };
                var cTitle = SelectBestEnglishOrLatinTitle(
                    IsCleanLatinText(c.Title) ? [c.Title] : [],
                    [],
                    [c.Title],
                    defaultContainerLabel);

                var normalizedUnits = c.Units
                    .Select(u =>
                    {
                        var defaultUnitLabel = u.UnitKind switch
                        {
                            "episode" => $"Episode {u.UnitNumber}",
                            "track" => $"Track {u.UnitNumber}",
                            "chapter" => $"Chapter {u.UnitNumber}",
                            "ebook_slot" => $"{primaryTitle} (Ebook)",
                            "audiobook_slot" => $"{primaryTitle} (Audiobook)",
                            _ => primaryTitle,
                        };
                        var uTitle = SelectBestEnglishOrLatinTitle(
                            IsCleanLatinText(u.Title) ? [u.Title] : [],
                            [],
                            [u.Title],
                            defaultUnitLabel);
                        return u with { Title = uTitle };
                    })
                    .ToList();

                return c with { Title = cTitle, Units = normalizedUnits };
            })
            .ToList();

        return candidate with
        {
            Title = primaryTitle,
            AltTitles = orderedAlts,
            Containers = normalizedContainers,
        };
    }

    public static string TransliterateToLatin(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        var norm = NormalizeFullWidthAndPunctuation(raw);

        // Convert Katakana range U+30A1..U+30F6 -> Hiragana U+3041..U+3096 for unified Kana romanization
        var kanaNormalized = new StringBuilder(norm.Length);
        foreach (var ch in norm)
        {
            if (ch is >= '\u30A1' and <= '\u30F6')
            {
                kanaNormalized.Append((char)(ch - 0x60));
            }
            else
            {
                kanaNormalized.Append(ch);
            }
        }

        var src = kanaNormalized.ToString();
        var sb = new StringBuilder(src.Length * 2);
        var doubleNextConsonant = false;

        for (var i = 0; i < src.Length; i++)
        {
            var ch = src[i];

            // Sokuon (small tsu) doubles the next consonant
            if (ch == 'っ')
            {
                doubleNextConsonant = true;
                continue;
            }

            // Katakana/Hiragana prolonged sound mark
            if (ch == 'ー')
            {
                continue;
            }

            // Two-kana digraph check
            if (i + 1 < src.Length)
            {
                var pair = src.Substring(i, 2);
                if (HiraganaDigraphs.TryGetValue(pair, out var digraphRomaji))
                {
                    if (doubleNextConsonant && digraphRomaji.Length > 0)
                    {
                        sb.Append(digraphRomaji[0]);
                        doubleNextConsonant = false;
                    }

                    sb.Append(digraphRomaji);
                    i++;
                    continue;
                }
            }

            if (HiraganaMonographs.TryGetValue(ch, out var kanaRomaji))
            {
                if (doubleNextConsonant && kanaRomaji.Length > 0 && !"aeiou".Contains(kanaRomaji[0]))
                {
                    sb.Append(kanaRomaji[0]);
                }

                doubleNextConsonant = false;
                sb.Append(kanaRomaji);
                continue;
            }

            doubleNextConsonant = false;

            // Korean Hangul precomposed syllable block (U+AC00..U+D7A3)
            if (ch is >= '\uAC00' and <= '\uD7A3')
            {
                var syl = ch - 0xAC00;
                var lead = syl / 588;
                var vowel = (syl % 588) / 28;
                var tail = syl % 28;
                sb.Append(HangulLead[lead]);
                sb.Append(HangulVowel[vowel]);
                sb.Append(HangulTail[tail]);
                continue;
            }

            if (CyrillicAndGreekMap.TryGetValue(ch, out var cyrOrGreek))
            {
                sb.Append(cyrOrGreek);
                continue;
            }

            // Keep Latin letters, digits, whitespace, and standard punctuation; drop untransliterated CJK/other scripts
            if (IsLatinLetter(ch) || char.IsDigit(ch) || char.IsWhiteSpace(ch) || IsSafeAsciiPunctuation(ch))
            {
                sb.Append(ch);
            }
            else
            {
                sb.Append(' ');
            }
        }

        return CleanupSpacingAndPunctuation(sb.ToString());
    }

    public static string NormalizeFullWidthAndPunctuation(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            // Full-width ASCII variants U+FF01..U+FF5E -> U+0021..U+007E
            if (ch is >= '\uFF01' and <= '\uFF5E')
            {
                sb.Append((char)(ch - 0xFEE0));
                continue;
            }

            switch (ch)
            {
                case '\u3000':
                    sb.Append(' ');
                    break;
                case '。':
                    sb.Append(". ");
                    break;
                case '、':
                    sb.Append(", ");
                    break;
                case '「' or '『' or '【':
                    sb.Append('"');
                    break;
                case '」' or '』' or '】':
                    sb.Append('"');
                    break;
                case '〜' or '～':
                    sb.Append('~');
                    break;
                default:
                    sb.Append(ch);
                    break;
            }
        }

        return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim();
    }

    private static string CleanupSpacingAndPunctuation(string input)
    {
        var collapsed = string.Join(' ', input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Trim(' ', ',', '.', '-', '—', ':', ';', '/');
    }

    private static string CapitalizeRomanizedWords(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text;
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (w.Length > 0 && char.IsLower(w[0]))
            {
                words[i] = char.ToUpperInvariant(w[0]) + w[1..];
            }
        }

        return string.Join(' ', words);
    }

    private static bool IsLatinLetter(char ch) =>
        ch is (>= 'A' and <= 'Z')
            or (>= 'a' and <= 'z')
            or (>= '\u00C0' and <= '\u024F')
            or (>= '\u1E00' and <= '\u1EFF');

    private static bool IsSafeAsciiPunctuation(char ch) =>
        ch is '-' or '—' or '–' or '\'' or '"' or ':' or ';' or ',' or '.' or '!' or '?' or '(' or ')' or '[' or ']' or '&' or '/' or '+' or '#';

    private static bool IsNonLatinScriptChar(char ch) =>
        ch is (>= '\u0370' and <= '\u052F') // Greek & Cyrillic
            or (>= '\u0590' and <= '\u06FF') // Hebrew & Arabic
            or (>= '\u0750' and <= '\u077F') // Arabic Supplement
            or (>= '\u0900' and <= '\u097F') // Devanagari
            or (>= '\u0E00' and <= '\u0E7F') // Thai
            or (>= '\u1100' and <= '\u11FF') // Hangul Jamo
            or (>= '\u3040' and <= '\u30FF') // Hiragana & Katakana
            or (>= '\u3130' and <= '\u318F') // Hangul Compatibility Jamo
            or (>= '\u31F0' and <= '\u31FF') // Katakana Phonetic Extensions
            or (>= '\u3400' and <= '\u4DBF') // CJK Extension A
            or (>= '\u4E00' and <= '\u9FFF') // CJK Unified Ideographs
            or (>= '\uAC00' and <= '\uD7AF') // Hangul Syllables
            or (>= '\uF900' and <= '\uFAFF') // CJK Compatibility Ideographs
            or (>= '\uFF65' and <= '\uFF9F'); // Halfwidth Katakana
}
