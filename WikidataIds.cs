using System.Text.RegularExpressions;

namespace Chronicle.Plugin.Wikidata;

/// <summary>A Wikidata item id: <c>Q</c> and a number.</summary>
internal readonly record struct WdId(long Number)
{
    public string Q => "Q" + Number;
    /// <summary>The id as this plugin issues it (I1): always prefixed.</summary>
    public string Canonical => WikidataIds.Prefix + Q;
    public override string ToString() => Canonical;
}

/// <summary>
/// What an id Chronicle, a person or a URL gives this plugin means. Every id the plugin issues is <c>wikidata:Q123</c>. It accepts
/// that, a bare <c>Q123</c> (any case) and wikidata.org addresses, and refuses anything else, including anything after the number:
/// <c>Q42x</c> and <c>Q42/extra</c> are not trimmed into <c>Q42</c>, because a wrong item filed under a right-looking id is worse than a refusal.
/// </summary>
internal static class WikidataIds
{
    public const string Prefix = "wikidata:";

    private static readonly Regex QPattern = new(@"^[Qq]([1-9][0-9]{0,14})$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly string[] Hosts = ["wikidata.org", "www.wikidata.org", "m.wikidata.org"];

    public static WdId? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();

        if (s.Contains("://", StringComparison.Ordinal)) return FromUrl(s);
        if (s.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) s = s[Prefix.Length..].Trim();
        return FromQ(s);
    }

    private static WdId? FromQ(string s)
    {
        var m = QPattern.Match(s);
        return m.Success && long.TryParse(m.Groups[1].Value, out var n) ? new WdId(n) : null;
    }

    private static WdId? FromUrl(string s)
    {
        // https only, the host compared as a parsed Host against an exact list, no userinfo, the default port.
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.UserInfo) || u.Port != 443) return null;
        if (!Hosts.Contains(u.Host, StringComparer.OrdinalIgnoreCase)) return null;

        var segments = u.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        // /wiki/Q42, /entity/Q42, /wiki/Special:EntityData/Q42 and /wiki/Special:EntityData/Q42.json
        string? candidate = segments switch
        {
            ["wiki", var q] => q,
            ["entity", var q] => q,
            ["wiki", var special, var q] when special.Equals("Special:EntityData", StringComparison.OrdinalIgnoreCase) => q,
            _ => null,
        };
        if (candidate is null) return null;
        if (candidate.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) candidate = candidate[..^5];
        return FromQ(Uri.UnescapeDataString(candidate));
    }
}

/// <summary>An item found by another service's id: the Wikidata property that holds it, and the value.</summary>
internal readonly record struct WdLookup(string Property, string Value, string Source);

/// <summary>
/// The ids Chronicle already holds for an item (<c>KnownExternalIds</c>) read as Wikidata statements. Each source's id is only
/// used where its kind is certain: a TMDB id says movie or TV in its own text or from the item's type, a MusicBrainz id says artist,
/// release group or recording and the item's hierarchy level must agree. An id of the wrong kind for the item is ignored, never converted.
/// </summary>
internal static class WikidataLookups
{
    // Wikidata properties, verified by name on 2026-11-16 (docs/plans/2026-11-16-wikidata-plugin-design.md section 2).
    public const string Imdb = "P345", TmdbMovie = "P4947", TmdbTv = "P4983", Tvdb = "P4835", TvMaze = "P8600";
    public const string MbArtist = "P434", MbReleaseGroup = "P436", MbRecording = "P4404";
    public const string Steam = "P1733", Igdb = "P5794", OpenLibrary = "P648", AniListAnime = "P8729", AniListManga = "P8731";
    public const string TmdbPerson = "P4985";

    private static readonly Regex ImdbNm = new(@"^nm[0-9]{7,}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static readonly Regex Imdbtt = new(@"^tt[0-9]{7,}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Digits = new(@"^[0-9]{1,12}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex Slug = new(@"^[a-z0-9][a-z0-9\-]{0,120}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex OlId = new(@"^OL[0-9]{1,12}[WMA]$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
    private static readonly Regex Uuid = new(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    /// <summary>The item's own Wikidata id, if Chronicle already holds one.</summary>
    public static WdId? OwnId(IReadOnlyDictionary<string, string>? known) =>
        known is not null && known.TryGetValue("wikidata", out var raw) ? WikidataIds.Parse(raw) : null;

    /// <summary>The lookups worth trying for an item of this type and level, best first.</summary>
    public static IReadOnlyList<WdLookup> For(IReadOnlyDictionary<string, string>? known, string? type, int level)
    {
        var result = new List<WdLookup>();
        if (known is null) return result;
        var t = (type ?? string.Empty).ToLowerInvariant();

        string? Get(string key) => known.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        // A person: found by the ids the person's own credits carry, never by name (two people share a name; an id does not).
        if (t == "people" && level == 0)
        {
            if (Get("imdb") is { } nm && ImdbNm.IsMatch(Strip(nm, "imdb")))
                result.Add(new(Imdb, Strip(nm, "imdb"), "imdb"));
            // A person's TMDB id is written "tmdb:521" (or "person:521" before the host rewrote it); never "movie:" or "tv:".
            if (Get("tmdb") is { } tp)
            {
                var (kind, num) = SplitKind(tp);
                if (kind is null or "tmdb" or "person" && Digits.IsMatch(num)) result.Add(new(TmdbPerson, num, "tmdb"));
            }
        }

        // A movie or TV id first: they are the most exact for video.
        if (t is "movies" or "movie" or "tv" && Get("tmdb") is { } tmdb)
        {
            var (kind, num) = SplitKind(tmdb);
            if (Digits.IsMatch(num))
            {
                var isTv = kind == "tv" || (kind is null && t == "tv");
                var isMovie = kind == "movie" || (kind is null && t is "movies" or "movie");
                // Only a series (level 0) has a TMDB TV item of its own; a movie is always level 0.
                if (isTv && level == 0 && t == "tv") result.Add(new(TmdbTv, num, "tmdb"));
                else if (isMovie && level == 0 && t is "movies" or "movie") result.Add(new(TmdbMovie, num, "tmdb"));
            }
        }

        if (t is "movies" or "movie" or "tv")
        {
            if (level == 0 && t == "tv" && Get("tvdb") is { } tvdb && Digits.IsMatch(Strip(tvdb, "tvdb")))
                result.Add(new(Tvdb, Strip(tvdb, "tvdb"), "tvdb"));
            if (level == 0 && t == "tv" && Get("tvmaze") is { } tvm && Digits.IsMatch(Strip(tvm, "tvmaze")))
                result.Add(new(TvMaze, Strip(tvm, "tvmaze"), "tvmaze"));
            // An IMDb id of a title; "nm…" names a person and "co…" a company, neither is this item.
            if (Get("imdb") is { } imdb && Imdbtt.IsMatch(Strip(imdb, "imdb")))
                result.Add(new(Imdb, Strip(imdb, "imdb"), "imdb"));
        }

        if (t == "music" && Get("musicbrainz") is { } mb)
        {
            var (kind, value) = SplitKind(mb);
            var want = level switch { 0 => "artist", 1 => "release-group", _ => "recording" };
            var prop = level switch { 0 => MbArtist, 1 => MbReleaseGroup, _ => MbRecording };
            if (NormaliseKind(kind) == want && Uuid.IsMatch(value))
                result.Add(new(prop, value.ToLowerInvariant(), "musicbrainz"));
        }

        if (t == "game" && level == 0)
        {
            if (Get("steam") is { } steam && Digits.IsMatch(Strip(steam, "steam")))
                result.Add(new(Steam, Strip(steam, "steam"), "steam"));
            // Wikidata holds IGDB's game page name (a slug), never its numeric id.
            if (Get("igdb") is { } igdb && Slug.IsMatch(Strip(igdb, "igdb").ToLowerInvariant()) && !Digits.IsMatch(Strip(igdb, "igdb")))
                result.Add(new(Igdb, Strip(igdb, "igdb").ToLowerInvariant(), "igdb"));
        }

        if (t is "book" or "audiobook" && level == 0 && Get("openlibrary") is { } ol && OlId.IsMatch(Strip(ol, "openlibrary")))
            result.Add(new(OpenLibrary, Strip(ol, "openlibrary").ToUpperInvariant(), "openlibrary"));

        return result;
    }

    private static string Strip(string value, string source) =>
        value.StartsWith(source + ":", StringComparison.OrdinalIgnoreCase) ? value[(source.Length + 1)..].Trim() : value.Trim();

    /// <summary>"movie:603" is ("movie", "603"); "603" is (null, "603").</summary>
    private static (string? Kind, string Value) SplitKind(string value)
    {
        var i = value.IndexOf(':');
        return i > 0 ? (value[..i].Trim().ToLowerInvariant(), value[(i + 1)..].Trim()) : (null, value.Trim());
    }

    private static string? NormaliseKind(string? kind) => kind?.Replace("_", "-") switch
    {
        "artist" => "artist",
        "release-group" or "releasegroup" => "release-group",
        "recording" => "recording",
        _ => null,
    };
}
