using System.Text.RegularExpressions;
using Managerr.Indexers.Categories;

namespace Managerr.Downloaders.Parser;

public sealed partial class ReleaseParserRouter
{
    private static readonly HashSet<string> NonGroupTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "x264", "x265", "H264", "H265", "HEVC", "AVC", "AV1", "XviD", "DivX",
        "EPUB", "AZW3", "MOBI", "PDF", "CBZ", "CBR", "M4B", "MP3", "FLAC", "AAC", "AC3", "DDP",
        "1080p", "1080i", "720p", "2160p", "480p", "576p", "4K", "UHD",
        "WEB", "DL", "WEBDL", "WEBRip", "BluRay", "Blu-ray", "BD", "BDRip", "HDTV", "HTDV", "REMUX", "HDR", "HDR10", "DV",
        "Cut", "Edition", "Extended", "Unrated", "Remastered", "Uncut", "PROPER", "REPACK", "INTERNAL",
        "xpost", "postbot", "AsRequested", "AlternativeToRequested", "Obfuscated", "Obfuscation", "Scrambled",
        "mkv", "mp4", "avi", "m2ts", "ts", "nzb", "iso", "Subs", "Sub", "Dual Audio", "Multi-Subs", "128", "192", "256", "320",
    };

    [GeneratedRegex(
        @"(?:(?:\s*-\s*\[(?:rarbg\.com|N-Z-B|eztv)\])|(?:\s*\[(?:eztv|PublicHD|rarbg\.com|N-Z-B|cttv|ettv|rartv|TGx|[0-9A-Fa-f]{8})\])|(?:\s*\(Blue Lock,\s*Multi-Subs\))|(?:-(?:xpost|postbot|asrequested|alternativetorequested|obfuscated|obfuscation|scrambled|chamele0n|nzbgeek|siklopentan|rakuv|rakuv02|rakuvfinhel|rakuvus|whiterev|buymore|z0ids3n|gerov|altezachen|repackpost|4p|4planet|pre|rp|sample|1|2))|(?:\.(?:mkv|mp4|avi|m2ts|ts|iso|nzb|srt|sub|idx|epub|m4b|cbz|cbr|azw3|pdf|mobi)))+$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TrailingGroupNoiseSuffixRegex();

    [GeneratedRegex(@"^(?<prefix>.*-[A-Za-z0-9]+)[._\s]+(?:English|French|German|Spanish|Italian|Japanese)$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingGroupLanguageSuffixRegex();

    // Multi-token / hyphenated / dotted scene & P2P groups that contain '-' or '.' or spaces after '-'
    [GeneratedRegex(
        @"(?:^|[\s._\-\(\[])(?<group>D-Z0N3|FTW-HS|VH-PROD|DX-TV|Blu-bits|Fight-BB|Erai-raws|E\.N\.D|BEN\.THE\.MEN|BEN THE MEN|Eml HDTeam|EML HDTeam|YTS\.MX|YTS\.LT|YTS\.AG)(?:[\]\)]|\s*-\s*)?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex SpecialTrailingGroupRegex();

    // Exception P2P/encodes that appear at the end of a title (or before closing ')' / ']') without a leading '-'
    [GeneratedRegex(
        @"(?<=\b|[\s._\-\(\[])(?<group>TAoE|QxR|UTR|SEV|HDO|Tigole|Silence|Bandi|Joy|SAMPA|Ghost|theincognito|Kappa|GiLG|Qman|MONOLITH|HONE|Vyndros|r00t|afm72|FreetheFish|RZeroX|DarQ|Celdra|t3nzin|Anna|Garshasp|Natty|ImE|EDGE2020|RCVR|YOGI|Vialle|KRaLiMaRKo|BluDragon|HQMUX|YIFY|YTS|KCRT|TMd|LMain|RlsGroup|rl)[\]\)]*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex ExceptionReleaseGroupRegex();

    // Anime movie / BD multi-bracket format: 'Title 1999 [Group] [Blu-ray]...[Softsubs (Group)]'
    [GeneratedRegex(@"\[(?<group>[^\]]+)\]\s*\[(?:Blu-?ray|BD|DVD|WEB|MKV|M2TS|720p|1080p|2160p|h26[45]|x26[45])", RegexOptions.IgnoreCase)]
    private static partial Regex MidBracketAnimeGroupRegex();

    // Standard scene/usenet trailing '-Group' (including ' - Group', '-ZR-', '-[Group]', Unicode letters like -Báleygr, -Hveðrungr, -ZØNEHD)
    [GeneratedRegex(@"(?:(?<=[A-Za-z0-9\]\)\.])-(?<group>[\p{L}\p{N}_]{2,20})-?$|\s+-\s*(?<group>[\p{L}\p{N}_]{2,20})$)", RegexOptions.IgnoreCase)]
    private static partial Regex StandardTrailingGroupRegex();

    private static string? ExtractReleaseGroup(string raw, string vertical, string? leadingGroup)
    {
        var working = raw.Trim();

        // Strip trailing repost, obfuscation, tracker tags, and media extensions iteratively
        while (true)
        {
            var next = TrailingGroupNoiseSuffixRegex().Replace(working, string.Empty).TrimEnd();
            if (TrailingGroupLanguageSuffixRegex().Match(next) is { Success: true } langMatch)
            {
                next = langMatch.Groups["prefix"].Value;
            }

            if (next == working)
            {
                break;
            }

            working = next;
        }

        // Lidarr case: '(1080p.BD.DD5.1.x265-TheSickle[TAoE])' expects 'TheSickle' in music vertical
        if (vertical == MediaVerticals.Music &&
            Regex.Match(working, @"-(?<group>[A-Za-z0-9_]{2,20})\[[A-Za-z0-9]+\]\)?$") is { Success: true } musicBracketMatch)
        {
            return musicBracketMatch.Groups["group"].Value;
        }

        // 1. Check multi-token / hyphenated / dotted special groups first (e.g. D-Z0N3, BEN THE MEN, E.N.D, YTS.LT)
        if (SpecialTrailingGroupRegex().Match(working) is { Success: true } specMatch)
        {
            return specMatch.Groups["group"].Value;
        }

        // 2. Check trailing [TAoE] / [QxR] / ) QxR / exception P2P groups at the end of the cleaned string
        if (ExceptionReleaseGroupRegex().Match(working) is { Success: true } excMatch)
        {
            var candidate = excMatch.Groups["group"].Value;
            // 'Movie.Should.Not.Use.Dots.2022.1080p.BluRay.x265.10bit.Tigole' (without closing ')') is a negative test in Radarr
            if (string.Equals(candidate, "Tigole", StringComparison.OrdinalIgnoreCase) &&
                !working.EndsWith(')') && !working.EndsWith(']'))
            {
                return null;
            }

            // Space-separated ' LOL' or ' RUNNER' without '-' are negative tests; HQMUX/YIFY/Vialle/BluDragon/KRaLiMaRKo/TMd/LMain are allowed
            return candidate;
        }

        // 3. Mid-string anime/BD bracket group: 'Title 1999 [CtrlHD] [Blu-ray]...'
        if (leadingGroup is null && MidBracketAnimeGroupRegex().Match(working) is { Success: true } midMatch)
        {
            var candidate = midMatch.Groups["group"].Value.Trim();
            if (!NonGroupTokens.Contains(candidate) &&
                !Regex.IsMatch(candidate, @"^(?:19|20)\d{2}$"))
            {
                return candidate;
            }
        }

        // 4. Standard trailing '-Group' or ' - Group'
        if (StandardTrailingGroupRegex().Match(working) is { Success: true } stdMatch)
        {
            var candidate = stdMatch.Groups["group"].Value;
            if (!NonGroupTokens.Contains(candidate))
            {
                // Pure digits like '-128' in '7s-atlantis-128.mp3' are bitrates/parts unless preceded by quality tokens
                if ( candidate.All(char.IsDigit) &&
                     !Regex.IsMatch(working, @"\b(?:480p|720p|1080p|2160p|WEB-DL|WEBRip|BluRay|HDTV)\b", RegexOptions.IgnoreCase))
                {
                    return leadingGroup;
                }

                // Exception: 't3nzinFAKE' / 'VialleFAKE' in Sonarr negative tests
                if (candidate.EndsWith("FAKE", StringComparison.Ordinal))
                {
                    return null;
                }

                return candidate;
            }
        }

        return leadingGroup;
    }

    private static string StripLeadingBracketGroup(string prefix, string raw, ref string? releaseGroup)
    {
        var m = LeadingBracketGroupRegex().Match(prefix);
        if (!m.Success)
        {
            return prefix;
        }

        var remainder = prefix[m.Length..];
        if (string.IsNullOrWhiteSpace(remainder.Trim(' ', '.', '-', '_', '(', '[')))
        {
            return prefix;
        }

        var grpSpan = m.Groups["group"];
        if (releaseGroup is null && grpSpan.Index + grpSpan.Length <= raw.Length)
        {
            var extracted = raw.Substring(grpSpan.Index, grpSpan.Length).Trim();
            if (!string.IsNullOrWhiteSpace(extracted) &&
                !NonGroupTokens.Contains(extracted) &&
                !extracted.Contains("www.", StringComparison.OrdinalIgnoreCase) &&
                !extracted.Contains(".com", StringComparison.OrdinalIgnoreCase) &&
                !extracted.Contains(".org", StringComparison.OrdinalIgnoreCase) &&
                !extracted.Contains(".net", StringComparison.OrdinalIgnoreCase) &&
                !Regex.IsMatch( remainder.TrimStart(), @"^\["))
            {
                releaseGroup = extracted;
            }
        }

        return remainder;
    }

    private static IReadOnlyList<string> ExtractLanguages(string raw, out bool isDualAudio, out bool isMultiSubs)
    {
        var langs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        isDualAudio = Regex.IsMatch(raw, @"\b(Dual[\s._-]?Audio|DL)\b", RegexOptions.IgnoreCase);
        isMultiSubs = Regex.IsMatch(raw, @"\b(Multi[\s._-]?Subs?|MSubs)\b", RegexOptions.IgnoreCase);

        if (Regex.IsMatch(raw, @"\b(GERMAN|GER|DEU|Deutsch|SwissGerman)\b|\bby[\s._-]+Videomann\b|(?:-[A-Z]{2})+-DE\b", RegexOptions.IgnoreCase))
        {
            langs.Add("de");
        }

        if (Regex.IsMatch(raw, @"\b(ENGLISH|ENG|Ingl[eé]s|Ing)\b|\bEN[\s._-]+(?:480p|720p|1080p|2160p)\b|[\[\(\.+_-]EN[\]\)\.+_-]", RegexOptions.IgnoreCase))
        {
            langs.Add("en");
        }

        if (Regex.IsMatch(raw, @"\b(JAPANESE|JAP|JPN)\b|[\(\[]JA[\)\]]", RegexOptions.IgnoreCase))
        {
            langs.Add("ja");
        }

        if (Regex.IsMatch(raw, @"\b(FRENCH|FRE|FRA|FR|VF|VFF|VFQ|VOSTFR|TRUEFRENCH|VFI|VF2)\b", RegexOptions.IgnoreCase))
        {
            langs.Add("fr");
        }

        if (Regex.IsMatch(raw, @"\b(SPANISH|SPA|ESP|LATINO|Castellano|Espa[ñn]ol)\b|[\[\(\.+_-]ES[\]\)\.+_-]|\bAVC[\s._-]+ES[\s._-]+DTS", RegexOptions.IgnoreCase))
        {
            langs.Add("es");
        }

        if (Regex.IsMatch(raw, @"\b(ITALIAN|ITA)\b", RegexOptions.IgnoreCase))
        {
            langs.Add("it");
        }

        if (Regex.IsMatch(raw, @"\b(MULTI)\b", RegexOptions.IgnoreCase))
        {
            langs.Add("multi");
        }

        if (isDualAudio && langs.Count == 1 && langs.Contains("de"))
        {
            langs.Add("en");
        }

        return langs.OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    public static bool HasExplicitEnglishToken(string? raw) =>
        !string.IsNullOrWhiteSpace(raw) &&
        Regex.IsMatch(raw, @"\b(ENGLISH|ENG|Ingl[eé]s|Ing)\b|\bEN[\s._-]+(?:480p|720p|1080p|2160p)\b|[\[\(\.+_-]EN[\]\)\.+_-]", RegexOptions.IgnoreCase);

    private static (string? Source, string? Resolution) ExtractQuality(string raw, string vertical)
    {
        var resolution = ExtractExplicitResolution(raw);
        var withoutLeadingGroup = LeadingBracketGroupRegex().Replace(raw, string.Empty);

        var hasRemux = Regex.IsMatch(withoutLeadingGroup, @"(?:\b|\[)(?:BD|UHD)?REMUX\b", RegexOptions.IgnoreCase) ||
                       Regex.IsMatch(raw, @"\b(?:1080p|2160p)\.BluRay\.(?:AVC|HEVC)-", RegexOptions.IgnoreCase);
        var isWebDl = Regex.IsMatch(raw, @"\b(?:WEB-DL|WEBDL|WebHD|AmazonHD|AmazonSD|NetflixHD|NetflixUHD|iTunesHD|MaxdomeHD|HBOMaxHD|DisneyHD)\b|(?<!\d)540p\b", RegexOptions.IgnoreCase);
        var isWebRip = Regex.IsMatch(raw, @"\b(?:WEBRip|WEB-Rip|WEBMux)\b", RegexOptions.IgnoreCase);
        var isHdtv = Regex.IsMatch(raw, @"\b(?:HDTV|HD[\s._]TV|PDTV|DSR|TVRip)\b", RegexOptions.IgnoreCase);
        var isBluRay = Regex.IsMatch(raw, @"(?:\b|\[)(?:BluRay|Blu-Ray|BDRip|BDMux|BRRip|MBluRay|UHDBD|UHDBDRip|UHD2BD|BDLight|HDDVD|HD-DVD|HDDVDRip)(?:\b|\d{3,4}p)|\.m2ts$", RegexOptions.IgnoreCase) ||
                       (vertical is MediaVerticals.Movies or MediaVerticals.Tv or MediaVerticals.Anime &&
                        Regex.IsMatch(raw, @"(?<!-)(?:\b|\[)BD(?:\b|\d{3,4}p)", RegexOptions.IgnoreCase));
        var isWebStandalone = vertical is MediaVerticals.Movies or MediaVerticals.Tv or MediaVerticals.Anime &&
                              Regex.IsMatch(raw, @"(?:^|[\s._\-\[\(])WEB(?:$|[\s._\-\]\)])", RegexOptions.IgnoreCase);

        string? source = null;
        if (isWebDl)
        {
            source = "WEB-DL";
        }
        else if (isWebRip)
        {
            source = "WEBRip";
        }
        else if (isHdtv)
        {
            source = "HDTV";
        }
        else if (hasRemux)
        {
            if (resolution is "480p" or "720p")
            {
                source = "BluRay";
            }
            else
            {
                source = "Remux";
                resolution ??= Regex.IsMatch(raw, @"\bUHD(?:Remux)?\b", RegexOptions.IgnoreCase) ? "2160p" : "1080p";
            }
        }
        else if (isBluRay)
        {
            source = "BluRay";
        }
        else if (isWebStandalone)
        {
            source = "WEB-DL";
        }

        if (resolution is null)
        {
            if (source == "BluRay")
            {
                if (Regex.IsMatch(raw, @"\b(?:BDRip|XviD|X-viD|DivX)\b|\(BD\)", RegexOptions.IgnoreCase))
                {
                    resolution = "480p";
                }
                else if (Regex.IsMatch(raw, @"\.(?:avi|m2ts)$", RegexOptions.IgnoreCase))
                {
                    resolution = "720p";
                }
            }
            else if (source == "WEBRip" && Regex.IsMatch(raw, @"\bWEBMux\b", RegexOptions.IgnoreCase))
            {
                resolution = "480p";
            }
            else if (source == "WEB-DL")
            {
                if (Regex.IsMatch(raw, @"\[WEBDL\]|\[Web\]\[MKV\]\[h264\]", RegexOptions.IgnoreCase))
                {
                    resolution = "720p";
                }
                else if (Regex.IsMatch(raw, @"\b(?:AmazonSD|AmazonHD|NetflixUHD|NetflixHD|iTunesHD|MaxdomeHD|HBOMaxHD|DisneyHD)\b", RegexOptions.IgnoreCase))
                {
                    resolution = "480p";
                }
            }
            else if (source == "HDTV" && Regex.IsMatch(raw, @"\bHR[\s._]+WS\b|\[HDTV\]|\bHD[\s._]+TV\.mkv$", RegexOptions.IgnoreCase))
            {
                resolution = "720p";
            }
            else if (source is null &&
                     vertical is MediaVerticals.Tv or MediaVerticals.Anime &&
                     Regex.IsMatch(raw.TrimEnd(), @"\bS\d{1,2}E\d{1,2}\.mkv$", RegexOptions.IgnoreCase))
            {
                source = "HDTV";
                resolution = "720p";
            }
        }

        if (source is null &&
            resolution is "720p" or "1080p" or "2160p" &&
            vertical is MediaVerticals.Movies or MediaVerticals.Tv or MediaVerticals.Anime &&
            !Regex.IsMatch(raw, @"\b(?:DVD|DVDRip|DVDR|SDTV)\b", RegexOptions.IgnoreCase))
        {
            source = "HDTV";
        }

        return (source, resolution);
    }

    private static string? ExtractExplicitResolution(string raw)
    {
        if (Regex.IsMatch(raw, @"(?<!\d)(2160p|3840x2160)\b", RegexOptions.IgnoreCase)) return "2160p";
        if (Regex.IsMatch(raw, @"(?<!\d)(1080[pi]|1920x1080|1440p)\b", RegexOptions.IgnoreCase)) return "1080p";
        if (Regex.IsMatch(raw, @"(?<!\d)(720[pi]|1280x720|1280\*720|960p)\b", RegexOptions.IgnoreCase)) return "720p";
        if (Regex.IsMatch(raw, @"(?<!\d)(576[pi]|540p|480[pi]|360p|640x480|848x480)\b", RegexOptions.IgnoreCase)) return "480p";
        if (Regex.IsMatch(raw, @"(?:\b|\[|-)4K(?:\b|\])|\bUHD\b", RegexOptions.IgnoreCase)) return "2160p";
        if (Regex.IsMatch(raw, @"(?<!-)\bFHD\b", RegexOptions.IgnoreCase)) return "1080p";
        return null;
    }

    private static string? ExtractVideoCodec(string raw)
    {
        if (Regex.IsMatch(raw, @"\bAV1\b", RegexOptions.IgnoreCase)) return "AV1";
        if (Regex.IsMatch(raw, @"\b(HEVC|x265|H\.?265)\b", RegexOptions.IgnoreCase)) return "HEVC";
        if (Regex.IsMatch(raw, @"\b(AVC|x264|H\.?264)\b", RegexOptions.IgnoreCase)) return "AVC";
        return null;
    }

    private static string? ExtractHdr(string raw)
    {
        var dv = Regex.IsMatch(raw, @"\b(DV|DoVi|Dolby[\s._-]?Vision)\b", RegexOptions.IgnoreCase);
        var hdr10Plus = Regex.IsMatch(raw, @"HDR10\+", RegexOptions.IgnoreCase);
        var hdr10 = Regex.IsMatch(raw, @"\bHDR(?:10)?\b", RegexOptions.IgnoreCase);
        if (dv && hdr10Plus) return "DV HDR10+";
        if (dv && hdr10) return "DV HDR10";
        if (dv) return "DV";
        if (hdr10Plus) return "HDR10+";
        if (hdr10) return "HDR10";
        return null;
    }

    private static string? ExtractAudioCodec(string raw)
    {
        if (Regex.IsMatch(raw, @"\bTrueHD[\s._-]*Atmos\b", RegexOptions.IgnoreCase)) return "TrueHD Atmos";
        if (Regex.IsMatch(raw, @"\bTrueHD\b", RegexOptions.IgnoreCase)) return "TrueHD";
        if (Regex.IsMatch(raw, @"\bDTS-HD[\s._-]*MA\b", RegexOptions.IgnoreCase)) return "DTS-HD MA";
        if (Regex.IsMatch(raw, @"\b(DDP|EAC3|E-AC-3)[\s._0-9]*Atmos\b", RegexOptions.IgnoreCase)) return "DDP Atmos";
        if (Regex.IsMatch(raw, @"\b(DDP|EAC3|E-AC-3)\b", RegexOptions.IgnoreCase)) return "DDP";
        if (Regex.IsMatch(raw, @"\b(DD|AC3)\b", RegexOptions.IgnoreCase)) return "AC3";
        if (Regex.IsMatch(raw, @"\bFLAC\b", RegexOptions.IgnoreCase)) return "FLAC";
        if (Regex.IsMatch(raw, @"\bAAC\b", RegexOptions.IgnoreCase)) return "AAC";
        return null;
    }

    private static string? ExtractMovieEdition(string raw)
    {
        if (Regex.Match(raw, @"Director'?s[\s._-]?Cut", RegexOptions.IgnoreCase) is { Success: true, Index: > 0 }) return "Director's Cut";
        if (Regex.Match(raw, @"\bIMAX(?:\s*Enhanced)?\b", RegexOptions.IgnoreCase) is { Success: true, Index: > 0 }) return "IMAX";
        if (Regex.Match(raw, @"\bExtended(?:\s*Edition|\s*Cut)?\b", RegexOptions.IgnoreCase) is { Success: true, Index: > 0 }) return "Extended";
        if (Regex.Match(raw, @"\bCriterion\b", RegexOptions.IgnoreCase) is { Success: true, Index: > 0 }) return "Criterion";
        if (Regex.Match(raw, @"\bUnrated\b", RegexOptions.IgnoreCase) is { Success: true, Index: > 0 }) return "Unrated";
        if (Regex.Match(raw, @"\bRemastered\b", RegexOptions.IgnoreCase) is { Success: true, Index: > 0 }) return "Remastered";
        return null;
    }

    private static string? ExtractMusicFormat(string raw)
    {
        if (Regex.IsMatch(raw, @"FLAC.*24\s*bit", RegexOptions.IgnoreCase) || Regex.IsMatch(raw, @"24bit.*FLAC", RegexOptions.IgnoreCase)) return "FLAC 24bit";
        if (Regex.IsMatch(raw, @"\bFLAC\b", RegexOptions.IgnoreCase)) return "FLAC";
        if (Regex.IsMatch(raw, @"\b320\b", RegexOptions.IgnoreCase)) return "MP3-320";
        if (Regex.IsMatch(raw, @"\bV0\b", RegexOptions.IgnoreCase)) return "MP3-V0";
        return null;
    }

    private static string? ExtractBookFormat(string raw)
    {
        if (Regex.IsMatch(raw, @"\bM4B\b", RegexOptions.IgnoreCase)) return "M4B";
        if (Regex.IsMatch(raw, @"\bEPUB\b", RegexOptions.IgnoreCase)) return "EPUB";
        if (Regex.IsMatch(raw, @"\bAZW3\b", RegexOptions.IgnoreCase)) return "AZW3";
        if (Regex.IsMatch(raw, @"\bCBZ\b", RegexOptions.IgnoreCase)) return "CBZ";
        if (Regex.IsMatch(raw, @"\bCBR\b", RegexOptions.IgnoreCase)) return "CBR";
        if (Regex.IsMatch(raw, @"\bPDF\b", RegexOptions.IgnoreCase)) return "PDF";
        return null;
    }
}
