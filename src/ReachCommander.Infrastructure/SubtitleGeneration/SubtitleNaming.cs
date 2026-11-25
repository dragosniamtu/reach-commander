using ReachCommander.Application.SubtitleGeneration;

namespace ReachCommander.Infrastructure.SubtitleGeneration;

internal static class SubtitleNaming
{
    private static readonly HashSet<string> VideoExtensions = new(
        [".mkv", ".mp4", ".avi", ".mov", ".m4v", ".webm", ".ts", ".mts", ".m2ts", ".wmv", ".asf",
            ".mpeg", ".mpg", ".vob", ".flv", ".ogv", ".3gp", ".3g2", ".rm", ".rmvb", ".divx", ".mxf"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SubtitleExtensions = new(
        [".srt", ".ass", ".ssa", ".vtt"], StringComparer.OrdinalIgnoreCase);
    // Keep language recognition independent of ICU: the production Alpine runtime uses invariant globalization.
    private const string LanguageCodes = """
        aa aar ab abk ae afr af agq ak aka am amh an ar ara arg arn as asa asm ast av ava ave ay aym az aze
        ba bak bam bas be bel bem ben bez bg bgc bh bho bi bin bis bm bn bo bod bos br bre brx bs bul byn
        ca cat ccp ce ceb ces cgg ch cha che chr chu chv ckb co cor cos cr cre cs cu cv cy cym
        da dan dav de deu div dje doi dsb dua dv dyo dz dzo ebu ee el ell en eng eo epo es est et eu eus ewe ewo
        fa fao fas ff fi fij fil fin fj fo fr fra fry ful fur fy ga gd gl gla gle glg glv gn grn gsw gu guj guz gv
        ha hat hau haw he heb her hi hin hmo ho hr hrv hsb ht hu hun hy hye hz ia ibb ibo id ido ie ig ii iii ik iku ile ina ind ipk io is isl it ita iu
        ja jav jgo jmc jpn jv ka kab kal kam kan kas kat kau kaz kde kea kg kgp khm khq ki kik kin kir kj kk kkj kl kln km kn ko kok kom kon kor kr ks ksb ksf ksh ku kua kur kv kw ky
        la lag lao lat lav lb lg li lim lin lit lkt ln lo lrc lt ltz lu lub lug luo lus luy lv mai mal mar mas mer mfe mg mgh mgo mh mi mk mkd ml mlg mlt mn mni moh mon mr mri ms msa mt mua my mya mzn
        na nau naq nb nbl nd nde ndo nds ne nep ng nl nld nmg nn nnh nno no nob nor nqo nr nso nus nv nya nyn ny
        oc oci oj oji om or ori orm os oss pa pan pap pcm pi pli pl pol por prg ps pt pus qu quc que
        raj rm rn ro rof roh ron ru run rus rw rwk sa sag sah san saq sat sbp sc sd se seh ses sg shi si sin sk sl slk slv sm sma sme smj smn smo sms sn sna snd so som sot spa sq sqi sr srd srp ss ssw ssy st su sun sv sw swa swe syr
        ta tah tam tat te tel teo tet tg tgk th tha ti tig tir tk tl tgl tn to ton tr ts tsn tso tt tuk tur tw twi twq ty tzm
        ug uig uk ukr ur urd uz uzb vai ve ven vi vie vo vol vun wa wae wal wln wo wol xh xho xog yav yi yid yo yor yrl za zgh zh zha zho zu zul
        alb arm baq bur chi cze dut fre geo ger gre ice mac mao may per rum slo tib wel
        """;
    private static readonly HashSet<string> Languages = new(
        LanguageCodes.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> SuffixTokens = new(
        ["forced", "sdh", "hi", "cc", "default", "foreign"], StringComparer.OrdinalIgnoreCase);

    public static string[] OutputNames(string videoName, bool english, bool romanian)
    {
        if (!english && !romanian)
            throw new SubtitleGenerationException("languages_required", "Select at least one subtitle language.");
        var stem = Path.GetFileNameWithoutExtension(videoName);
        return english && romanian ? [stem + ".srt", stem + ".ro.srt"] : [stem + ".srt"];
    }

    public static string[] AssociatedSidecars(string videoName, IReadOnlyList<string> directoryNames)
    {
        var stem = Path.GetFileNameWithoutExtension(videoName);
        var videoNames = directoryNames.Where(n => VideoExtensions.Contains(Path.GetExtension(n))).ToArray();
        if (videoNames.Count(n => string.Equals(Path.GetFileNameWithoutExtension(n), stem, StringComparison.OrdinalIgnoreCase)) != 1)
            throw new SubtitleGenerationException("subtitle_ownership_ambiguous", "Multiple videos share this subtitle basename.");
        var otherStems = videoNames.Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.Equals(n, stem, StringComparison.OrdinalIgnoreCase)).Cast<string>().ToArray();
        var associated = directoryNames.Where(name =>
        {
            if (!SubtitleExtensions.Contains(Path.GetExtension(name))) return false;
            var sidecarStem = Path.GetFileNameWithoutExtension(name);
            if (!MatchesStem(sidecarStem, stem)) return false;
            // A longer existing video stem owns its sidecars even when its name looks like a language suffix.
            return !otherStems.Any(other => other.Length > stem.Length && MatchesStem(sidecarStem, other));
        }).Order(StringComparer.Ordinal).ToArray();
        if (associated.Distinct(StringComparer.OrdinalIgnoreCase).Count() != associated.Length)
            throw new SubtitleGenerationException("subtitle_ownership_ambiguous", "Subtitle filenames differ only by letter case.");
        return associated;
    }

    public static string BackupName(string originalName, HashSet<string> occupied, ref int sequence)
    {
        var stem = Path.GetFileNameWithoutExtension(originalName);
        var extension = Path.GetExtension(originalName);
        while (true)
        {
            var candidate = FormattableString.Invariant(
                $"{stem}.existing-{DateTime.UtcNow:yyyyMMdd}-{++sequence:000}{extension}.bak");
            if (occupied.Add(candidate)) return candidate;
        }
    }

    private static bool MatchesStem(string sidecarStem, string videoStem)
    {
        if (sidecarStem.Equals(videoStem, StringComparison.OrdinalIgnoreCase)) return true;
        if (!sidecarStem.StartsWith(videoStem + ".", StringComparison.OrdinalIgnoreCase)) return false;
        return sidecarStem[(videoStem.Length + 1)..].Split('.').All(IsSuffixToken);
    }

    private static bool IsSuffixToken(string token)
    {
        if (Languages.Contains(token) || SuffixTokens.Contains(token)) return true;
        var parts = token.Split('-');
        if (parts.Length is < 2 or > 3 || !Languages.Contains(parts[0])) return false;
        static bool Script(string value) => value.Length == 4 && value.All(char.IsAsciiLetter);
        static bool Region(string value) => (value.Length == 2 && value.All(char.IsAsciiLetter)) ||
            (value.Length == 3 && value.All(char.IsAsciiDigit));
        return parts.Length == 2 ? Script(parts[1]) || Region(parts[1]) : Script(parts[1]) && Region(parts[2]);
    }
}
