using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Chronicle.Plugins.Models;

namespace Chronicle.Plugin.Wikidata;

/// <summary>
/// A referenced item (a genre, a person, a company), resolved: its name in the best available language, its logo file if it has one, and,
/// for a person, whether it is a human and its portrait file. A cast or crew person is resolved to their own record, never left as a name (D10).
/// </summary>
internal sealed record WdRef(string Id, string? Label, string? LogoFile, string? PhotoFile = null, bool Human = false);

/// <summary>
/// Wikidata entity -> Chronicle metadata. Everything the service returns is kept: the whole entity under <c>raw</c>, every other-language
/// label and every alias as an alternate name, every external id grouped by property. What is mapped into first-class fields is a view of it.
///
/// Labels: most items now carry their label in the language-neutral <c>mul</c> code and not in <c>en</c> (measured), so a label is read
/// <c>en</c>, then <c>mul</c>, then any language at all.
/// </summary>
internal static class WikidataMapper
{
    public const string Source = "wikidata";
    public const string Site   = "https://www.wikidata.org/wiki/";

    public static readonly string[] Fields =
        ["title", "year", "poster_url", "logo_url", "genres", "tags", "cast", "crew"];

    /// <summary>What a person item is filled with (a person has no year, cast or crew of its own).</summary>
    public static readonly string[] PersonFields = ["title", "poster_url", "birth_date", "death_date", "tags"];

    // ── property groups ───────────────────────────────────────────────────────

    private static readonly string[] GenreProps  = ["P136"];
    // Classification: what it is (P31), its subject (P921), a person's occupations (P106), and for a game its platforms (P400),
    // game modes (P404) and engine (P408).
    private static readonly string[] TagProps    = ["P31", "P921", "P106", "P400", "P404", "P408"];
    private const string CastProp = "P161";
    private const string Developer = "P178";
    private static readonly (string Prop, string Job)[] CrewProps =
    [
        ("P57", "Director"), ("P58", "Screenwriter"), ("P162", "Producer"), ("P86", "Composer"), ("P344", "Director of Photography"),
        ("P1040", "Film Editor"), ("P50", "Author"), ("P175", "Performer"), ("P123", "Publisher"), ("P272", "Production Company"),
        ("P750", "Distributor"), ("P264", "Record Label"), ("P449", "Original Broadcaster"),
        // A game's own makers (Wikidata: designed by, programmer, creator, art director), and a work's production designer.
        ("P287", "Designer"), ("P943", "Programmer"), ("P170", "Creator"), ("P3174", "Art Director"), ("P2554", "Production Designer"),
        // The rest of a credit list: executive producers, costume designers, lyricists, conductors, choreographers.
        ("P1431", "Executive Producer"), ("P2515", "Costume Designer"), ("P676", "Lyricist"), ("P3300", "Conductor"), ("P1809", "Choreographer"),
    ];

    /// <summary>Voice actors (a game's or an animation's): credited as cast, with the character they voiced.</summary>
    private const string VoiceProp = "P725";

    /// <summary>
    /// "Has part(s)": for a band it lists its members, but for most things it lists parts that are not people (a series' episodes, a
    /// collection's films). It is read as a member list only on a musical act, which is what holds a MusicBrainz artist id, and only the
    /// values that turn out to be human.
    /// </summary>
    private const string PartsProp = "P527";

    /// <summary>The properties whose values are companies, so a logo is wanted for them.</summary>
    private static readonly HashSet<string> CompanyProps = ["P178", "P123", "P272", "P750", "P264", "P449"];

    private static readonly Regex YearOf = new(@"^[+-]?(\d{1,6})-", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ImdbTitle = new(@"^tt[0-9]{7,}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // ── what to resolve ───────────────────────────────────────────────────────

    /// <summary>A musical act: the item holds a MusicBrainz artist id, so its "has parts" are read as members.</summary>
    private static bool IsMusicalAct(JsonElement entity) => Claims(entity, WikidataLookups.MbArtist).Count > 0;

    /// <summary>
    /// The item ids this entity refers to for fields that show a name, with whether each needs its own record fetched (true) or only its
    /// name (false). A person or company in the cast and crew needs the record, for the photo or logo and the ids: a credit is never reduced
    /// to a bare name to save a request (D10). Genres, classes and characters need only the name.
    /// </summary>
    public static IReadOnlyDictionary<string, bool> ReferencedIds(JsonElement entity)
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        void Add(string prop, bool wholeRecord)
        {
            foreach (var c in Claims(entity, prop))
                if (ItemId(c) is { } id) map[id] = map.TryGetValue(id, out var was) && was || wholeRecord;
        }
        foreach (var p in GenreProps) Add(p, false);
        foreach (var p in TagProps) Add(p, false);
        Add(CastProp, true);
        Add(VoiceProp, true);
        if (IsMusicalAct(entity)) Add(PartsProp, true);
        foreach (var (p, _) in CrewProps) Add(p, true);
        Add(Developer, true);
        // The character a cast member played can be an item.
        foreach (var c in Claims(entity, CastProp))
            foreach (var q in WdJson.List(WdJson.Obj(c, "qualifiers"), "P453"))
                if (ItemIdOfSnak(q) is { } id) map.TryAdd(id, false);
        return map;
    }

    // ── mapping ───────────────────────────────────────────────────────────────

    public static MediaMetadata Map(JsonElement entity, IReadOnlyDictionary<string, WdRef> refs)
    {
        var id = WdJson.Str(entity, "id") ?? string.Empty;
        var title = Label(entity) ?? string.Empty;
        var m = new MediaMetadata
        {
            ExternalId = WikidataIds.Prefix + id,
            Source     = Source,
            Title      = title,
            Year       = YearFrom(entity),
            SourceUrl  = Site + id,
        };

        // Names: every other label and every alias in every language (nothing dropped).
        var names = new List<string>();
        foreach (var l in WdJson.Members(entity, "labels")) if (WdJson.Str(l.Value, "value") is { } v) names.Add(v);
        foreach (var lang in WdJson.Members(entity, "aliases"))
            if (lang.Value.ValueKind == JsonValueKind.Array)
                foreach (var a in lang.Value.EnumerateArray())
                    if (WdJson.Str(a, "value") is { } v) names.Add(v);
        m.AlternateNames = names.Where(n => !n.Equals(title, StringComparison.OrdinalIgnoreCase))
                                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        string? NameOf(string qid) => refs.TryGetValue(qid, out var r) ? r.Label : null;

        foreach (var prop in GenreProps)
            foreach (var c in Claims(entity, prop))
                if (ItemId(c) is { } q && NameOf(q) is { } n) m.Genres.Add(n);
        m.Genres = m.Genres.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var prop in TagProps)
            foreach (var c in Claims(entity, prop))
                if (ItemId(c) is { } q && NameOf(q) is { } n) m.Tags.Add(n);
        m.Tags = m.Tags.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        string? RoleOf(JsonElement claim, string? fallback = null) =>
            WdJson.List(WdJson.Obj(claim, "qualifiers"), "P4633").Select(Snak).FirstOrDefault(x => x is not null)
            ?? WdJson.List(WdJson.Obj(claim, "qualifiers"), "P453").Select(x => ItemIdOfSnak(x)).Where(x => x is not null)
                     .Select(x => NameOf(x!)).FirstOrDefault(x => x is not null)
            ?? fallback;

        // Cast: the people credited as cast on a film or show, with their portrait when Wikidata has one; a game's voice actors with the
        // character they voiced; a band's members; for anything with a developer, the developers (as IGDB does).
        foreach (var c in Claims(entity, CastProp))
            if (ItemId(c) is { } q && NameOf(q) is { } name)
                m.Cast.Add(new CastMember(name, RoleOf(c), WikidataIds.Prefix + q, ImageOf(refs, q)));
        foreach (var c in Claims(entity, VoiceProp))
            if (ItemId(c) is { } q && NameOf(q) is { } name)
                m.Cast.Add(new CastMember(name, RoleOf(c, "Voice actor"), WikidataIds.Prefix + q, ImageOf(refs, q)));
        if (IsMusicalAct(entity))
            foreach (var c in Claims(entity, PartsProp))
                if (ItemId(c) is { } q && refs.TryGetValue(q, out var member) && member.Human && member.Label is { } name)
                    m.Cast.Add(new CastMember(name, RoleOf(c, "Member"), WikidataIds.Prefix + q, ImageOf(refs, q)));
        foreach (var c in Claims(entity, Developer))
            if (ItemId(c) is { } q && NameOf(q) is { } name)
                m.Cast.Add(new CastMember(name, "Developer", WikidataIds.Prefix + q, ImageOf(refs, q)));

        foreach (var (prop, job) in CrewProps)
            foreach (var c in Claims(entity, prop))
                if (ItemId(c) is { } q && NameOf(q) is { } name)
                    m.Crew.Add(new CrewMember(name, job, WikidataIds.Prefix + q, ImageOf(refs, q)));

        // Images: built only from file names Wikidata returned, never guessed (M1).
        // Only a film poster (P3383) is a poster for a work. P18 ("image") is whatever a volunteer chose: for The Matrix it is a screenshot
        // of the green-code screensaver, for a game often a scene. It is kept as an additional image the owner can pin, never promoted.
        // For a person it IS the portrait, so there it is the photo.
        var posters = (IsHuman(entity) ? Files(entity, "P18") : Files(entity, "P3383")).ToList();
        if (posters.Count > 0) m.PosterUrl = ImageUrl(posters[0], 600);
        var logos = Files(entity, "P154").ToList();
        if (logos.Count > 0) m.LogoUrl = ImageUrl(logos[0], 600);

        foreach (var f in Files(entity, "P3383").Distinct())
            m.AdditionalImages.Add(new AdditionalImage { Url = ImageUrl(f, 1200), Type = "Poster", ThumbnailUrl = ImageUrl(f, 300) });
        foreach (var f in Files(entity, "P18").Distinct())
            m.AdditionalImages.Add(new AdditionalImage { Url = ImageUrl(f, 1200), Type = "Image", ThumbnailUrl = ImageUrl(f, 300) });
        foreach (var f in logos.Distinct())
            m.AdditionalImages.Add(new AdditionalImage { Url = ImageUrl(f, 1200), Type = "Logo", ThumbnailUrl = ImageUrl(f, 300) });
        foreach (var f in Files(entity, "P2910").Distinct())
            m.AdditionalImages.Add(new AdditionalImage { Url = ImageUrl(f, 1200), Type = "Icon", ThumbnailUrl = ImageUrl(f, 300) });

        m.ExtendedData = JsonSerializer.SerializeToElement(Extended(entity, id, refs));
        return m;
    }

    private static JsonObject Extended(JsonElement entity, string id, IReadOnlyDictionary<string, WdRef> refs)
    {
        var o = new JsonObject { ["id"] = id };
        if (Description(entity) is { } d) o["description"] = d;

        var instance = new JsonArray();
        foreach (var c in Claims(entity, "P31"))
            if (ItemId(c) is { } q)
                instance.Add(new JsonObject { ["id"] = q, ["label"] = refs.TryGetValue(q, out var r) ? r.Label : null });
        if (instance.Count > 0) o["instance_of"] = instance;

        var ids = PublishedIds(entity);
        if (ids.Count > 0)
        {
            var node = new JsonObject();
            foreach (var (k, v) in ids) node[k] = v.DeepClone();
            o["ids"] = node;
        }

        // A person's dates, under the names the host reads (FieldMap: birth_date -> birthDate). Only a date precise to the day: a year
        // or a decade written as "1961-01-01" would be a date nobody recorded. The statements stay in raw whatever their precision.
        if (IsHuman(entity))
        {
            if (FullDate(entity, "P569") is { } born) o["birthDate"] = born;
            if (FullDate(entity, "P570") is { } died) o["deathDate"] = died;
        }

        // Every external id the item holds, by property (the names of the properties are in the raw entity's claims).
        var external = new JsonObject();
        foreach (var prop in WdJson.Members(entity, "claims"))
        {
            if (prop.Value.ValueKind != JsonValueKind.Array) continue;
            var values = new JsonArray();
            foreach (var c in prop.Value.EnumerateArray())
            {
                var snak = WdJson.Obj(c, "mainsnak");
                if (WdJson.Str(snak, "datatype") != "external-id" || WdJson.Str(snak, "snaktype") != "value") continue;
                if (Snak(c) is { } v) values.Add(v);
            }
            if (values.Count > 0) external[prop.Name] = values;
        }
        if (external.Count > 0) o["external_ids"] = external;

        var sites = new JsonObject();
        foreach (var s in WdJson.Members(entity, "sitelinks"))
            if (WdJson.Str(s.Value, "title") is { } t)
                sites[s.Name] = new JsonObject { ["title"] = t, ["url"] = SitelinkUrl(s.Name, t) };
        if (sites.Count > 0) o["sitelinks"] = sites;

        var sitesOfficial = new JsonArray();
        foreach (var c in Claims(entity, "P856")) if (Snak(c) is { } url) sitesOfficial.Add(url);
        if (sitesOfficial.Count > 0) o["official_websites"] = sitesOfficial;

        // The whole entity, kept as it came.
        o["raw"] = JsonNode.Parse(entity.GetRawText());
        return o;
    }

    // ── ids published for other plugins ───────────────────────────────────────

    /// <summary>
    /// The ids this item holds at services Chronicle has plugins for, keyed by the host's short source name. Only an id that is
    /// unambiguous is published: when an item holds two of a kind (a TMDB movie id and a TMDB TV id, two IMDb titles) the
    /// host would seed the wrong one for half its items, so that kind is withheld here and stays in <c>raw</c>.
    /// </summary>
    /// <remarks>
    /// The JSON type of each value matters: the host files an id through the TARGET plugin's own declared policy, and TMDB, Simkl and
    /// Trakt accept only a JSON number there while IMDb accepts only a string (<c>CrossRefValueKinds</c>); an id of the wrong type is
    /// dropped without a word. So numeric ids are written as numbers and the rest as strings. (The first build wrote every id as a
    /// string, and the TMDB id would never have seeded anything.)
    /// </remarks>
    public static IReadOnlyDictionary<string, JsonNode> PublishedIds(JsonElement entity)
    {
        var result = new Dictionary<string, JsonNode>();
        var human = IsHuman(entity);

        List<string> V(string prop) => Claims(entity, prop).Select(Snak).Where(s => s is not null).Select(s => s!).ToList();
        void One(string key, bool number, params (string Prop, Func<string, string?> Format)[] sources)
        {
            var found = sources.SelectMany(s => V(s.Prop).Select(v => s.Format(v)).Where(x => x is not null)).Select(x => x!).Distinct().ToList();
            if (found.Count != 1) return;
            result[key] = number && long.TryParse(found[0], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                ? JsonValue.Create(n)
                : JsonValue.Create(found[0])!;
        }

        // IMDb: a title is "tt…", a person "nm…". On the item of a person the id is the person's.
        One("imdb", false, (WikidataLookups.Imdb, v => (human ? ImdbName : ImdbTitle).IsMatch(v) ? v : null));
        One("tmdb", true, (WikidataLookups.TmdbMovie, Digits), (WikidataLookups.TmdbTv, Digits), (WikidataLookups.TmdbPerson, Digits));
        // A person is a person item in Chronicle (people type): only the ids that name a person are passed on. Michael J. Fox also holds
        // a MusicBrainz artist id (he is a musician too), and handing that to the music plugins would seed an artist from a person's page.
        if (human) return result;

        One("tvdb", true, (WikidataLookups.Tvdb, Digits));
        One("tvmaze", true, (WikidataLookups.TvMaze, Digits));
        One("musicbrainz", false,
            (WikidataLookups.MbArtist, v => "artist:" + v), (WikidataLookups.MbReleaseGroup, v => "release-group:" + v),
            (WikidataLookups.MbRecording, v => "recording:" + v));
        One("steam", true, (WikidataLookups.Steam, Digits));
        One("igdb", false, (WikidataLookups.Igdb, v => v));
        One("openlibrary", false, (WikidataLookups.OpenLibrary, v => Regex.IsMatch(v, @"^OL[0-9]+[WMA]$") ? v : null));
        One("anilist", true, (WikidataLookups.AniListAnime, Digits), (WikidataLookups.AniListManga, Digits));
        return result;

        static string? Digits(string v) => v.Length is > 0 and <= 12 && v.All(char.IsAsciiDigit) ? v : null;
    }

    private static readonly Regex ImdbName = new(@"^nm[0-9]{7,}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Is the item a person: an instance of "human" (Q5)?</summary>
    public static bool IsHuman(JsonElement entity) => Claims(entity, "P31").Any(c => ItemId(c) == "Q5");

    /// <summary>A date as <c>yyyy-MM-dd</c>, only when Wikidata knows it to the day (precision 11).</summary>
    private static string? FullDate(JsonElement entity, string prop)
    {
        foreach (var c in Claims(entity, prop))
        {
            var value = WdJson.Obj(WdJson.Obj(WdJson.Obj(c, "mainsnak"), "datavalue"), "value");
            if (value is null || !value.Value.TryGetProperty("precision", out var p) || p.ValueKind != JsonValueKind.Number || p.GetInt32() < 11) continue;
            if (WdJson.Str(value, "time") is not { } t || t.StartsWith('-')) continue;
            // "+1961-06-09T00:00:00Z"; the sign and the time of day are not part of the date.
            var date = t.TrimStart('+');
            var cut = date.IndexOf('T');
            if (cut > 0) date = date[..cut];
            if (DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return date;
        }
        return null;
    }

    // ── reading ───────────────────────────────────────────────────────────────

    /// <summary>The label of an item or entity in the best language: <c>en</c>, then <c>mul</c>, then any.</summary>
    public static string? Label(JsonElement entity) => Pick(WdJson.Obj(entity, "labels"));

    public static string? Description(JsonElement entity) => Pick(WdJson.Obj(entity, "descriptions"));

    private static string? Pick(JsonElement? languages)
    {
        if (languages is null) return null;
        foreach (var lang in new[] { "en", "mul" })
            if (WdJson.Str(WdJson.Obj(languages, lang), "value") is { } v) return v;
        foreach (var l in languages.Value.EnumerateObject())
            if (WdJson.Str(l.Value, "value") is { } v) return v;
        return null;
    }

    /// <summary>
    /// The usable statements of a property: not deprecated, with a real value. When any is marked preferred, only those: that is
    /// what Wikidata means by a preferred rank.
    /// </summary>
    public static IReadOnlyList<JsonElement> Claims(JsonElement entity, string prop)
    {
        var all = WdJson.List(WdJson.Obj(entity, "claims"), prop)
            .Where(c => WdJson.Str(c, "rank") != "deprecated" && WdJson.Str(WdJson.Obj(c, "mainsnak"), "snaktype") == "value")
            .ToList();
        var preferred = all.Where(c => WdJson.Str(c, "rank") == "preferred").ToList();
        return preferred.Count > 0 ? preferred : all;
    }

    /// <summary>The item id a statement's value points to, or null when the value is not an item.</summary>
    private static string? ItemId(JsonElement claim) => ItemIdOfSnak(WdJson.Obj(claim, "mainsnak"));

    private static string? ItemIdOfSnak(JsonElement? snak)
    {
        var value = WdJson.Obj(WdJson.Obj(snak, "datavalue"), "value");
        if (value is null || WdJson.Str(value, "entity-type") != "item") return null;
        return WdJson.Str(value, "id") is { } id && WikidataIds.Parse(id) is { } q ? q.Q : null;
    }

    /// <summary>A statement's value as text, for string, external-id, url and monolingual-text values.</summary>
    private static string? Snak(JsonElement claimOrSnak)
    {
        var snak = claimOrSnak.TryGetProperty("mainsnak", out var main) ? main : claimOrSnak;
        if (snak.ValueKind != JsonValueKind.Object) return null;
        if (!snak.TryGetProperty("datavalue", out var dv) || dv.ValueKind != JsonValueKind.Object) return null;
        if (!dv.TryGetProperty("value", out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => string.IsNullOrWhiteSpace(v.GetString()) ? null : v.GetString()!.Trim(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.Object => WdJson.Str(v, "text") ?? WdJson.Str(v, "amount"),
            _ => null,
        };
    }

    private static IEnumerable<string> Files(JsonElement entity, string prop) =>
        Claims(entity, prop).Select(Snak).Where(s => s is not null).Select(s => s!);

    private static int? YearFrom(JsonElement entity)
    {
        foreach (var prop in new[] { "P577", "P571" })
        {
            var years = new List<int>();
            foreach (var c in Claims(entity, prop))
            {
                var value = WdJson.Obj(WdJson.Obj(WdJson.Obj(c, "mainsnak"), "datavalue"), "value");
                // precision 9 is a year, 10 a month, 11 a day; a decade or century (8, 7) is not a year.
                if (WdJson.Str(value, "time") is { } t && YearOf.Match(t) is { Success: true } m
                    && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var y)
                    && y > 0
                    && (!value!.Value.TryGetProperty("precision", out var p) || p.ValueKind != JsonValueKind.Number || p.GetInt32() >= 9)
                    && !t.StartsWith('-'))
                    years.Add(y);
            }
            if (years.Count > 0) return years.Min();
        }
        return null;
    }

    // ── images ────────────────────────────────────────────────────────────────

    /// <summary>
    /// The address of a Commons file at a given width: <c>Special:FilePath</c> redirects to the upload server, which sends
    /// <c>Access-Control-Allow-Origin: *</c>, so the browser loads it directly. A thumbnail of a vector logo is a transparent PNG (M4).
    /// </summary>
    public static string ImageUrl(string fileName, int width) =>
        "https://commons.wikimedia.org/wiki/Special:FilePath/" + Uri.EscapeDataString(fileName.Trim().Replace(' ', '_'))
        + "?width=" + width.ToString(CultureInfo.InvariantCulture);

    /// <summary>The picture for a credited person (their portrait) or company (its logo), when Wikidata has one.</summary>
    private static string? ImageOf(IReadOnlyDictionary<string, WdRef> refs, string qid) =>
        refs.TryGetValue(qid, out var r) && (r.PhotoFile ?? r.LogoFile) is { } f ? ImageUrl(f, 300) : null;

    /// <summary>A resolved reference from an entity fetched for its label (and, for a company, its logo).</summary>
    public static WdRef ToRef(JsonElement entity)
    {
        var id = WdJson.Str(entity, "id") ?? string.Empty;
        var logo = Claims(entity, "P154").Select(Snak).FirstOrDefault(s => s is not null);
        var human = IsHuman(entity);
        var photo = human ? Claims(entity, "P18").Select(Snak).FirstOrDefault(s => s is not null) : null;
        return new WdRef(id, Label(entity), logo, photo, human);
    }

    /// <summary>The Wikipedia address of a sitelink; null for any other Wikimedia project (the title is still kept).</summary>
    private static string? SitelinkUrl(string site, string title)
    {
        var m = Regex.Match(site, @"^([a-z\-_]+)wiki$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        return m.Success && site is not ("commonswiki" or "specieswiki" or "metawiki" or "wikidatawiki" or "mediawikiwiki")
            ? $"https://{m.Groups[1].Value.Replace('_', '-')}.wikipedia.org/wiki/{Uri.EscapeDataString(title.Replace(' ', '_'))}"
            : null;
    }
}
