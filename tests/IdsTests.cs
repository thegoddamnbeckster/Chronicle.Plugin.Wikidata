using Xunit;

namespace Chronicle.Plugin.Wikidata.Tests;

public class IdsTests
{
    [Theory]
    [InlineData("wikidata:Q42", 42)]
    [InlineData("WIKIDATA:q42", 42)]
    [InlineData("Q42", 42)]
    [InlineData("q118645527", 118645527)]
    [InlineData("  Q42  ", 42)]
    [InlineData("https://www.wikidata.org/wiki/Q42", 42)]
    [InlineData("https://wikidata.org/wiki/Q42", 42)]
    [InlineData("https://m.wikidata.org/wiki/Q42", 42)]
    [InlineData("https://www.wikidata.org/entity/Q42", 42)]
    [InlineData("https://www.wikidata.org/wiki/Special:EntityData/Q42.json", 42)]
    [InlineData("https://www.wikidata.org/wiki/Special:EntityData/Q42", 42)]
    [InlineData("https://www.wikidata.org/wiki/Q42?uselang=de", 42)]
    [InlineData("https://www.wikidata.org/wiki/Q42#P31", 42)]
    public void An_id_in_any_accepted_form_is_read(string raw, long number)
    {
        var id = WikidataIds.Parse(raw);
        Assert.Equal(number, id?.Number);
        Assert.Equal($"wikidata:Q{number}", id!.Value.Canonical);   // I1: the plugin always issues the prefixed form
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Q")]
    [InlineData("Q0")]
    [InlineData("Q042")]               // a leading zero is not an id
    [InlineData("Q42x")]               // anything after the number is refused, not trimmed
    [InlineData("Q42/extra")]
    [InlineData("Q42 Q43")]
    [InlineData("P31")]                // a property is not an item
    [InlineData("L99")]
    [InlineData("42")]
    [InlineData("wikidata:wikidata:Q42")]
    [InlineData("Q1234567890123456")]  // too long
    [InlineData("http://www.wikidata.org/wiki/Q42")]            // https only
    [InlineData("https://www.wikidata.org.evil.com/wiki/Q42")]  // the host is compared whole
    [InlineData("https://evilwikidata.org/wiki/Q42")]
    [InlineData("https://user@www.wikidata.org/wiki/Q42")]      // no userinfo
    [InlineData("https://www.wikidata.org:8443/wiki/Q42")]      // default port only
    [InlineData("https://www.wikidata.org/wiki/Q42/extra")]
    [InlineData("https://www.wikidata.org/w/index.php?title=Q42")]
    [InlineData("https://en.wikipedia.org/wiki/Q42")]
    public void Anything_else_is_refused(string? raw) => Assert.Null(WikidataIds.Parse(raw));

    private static IReadOnlyDictionary<string, string> Known(params (string, string)[] pairs) =>
        pairs.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public void A_held_wikidata_id_is_read_in_any_form()
    {
        Assert.Equal(42, WikidataLookups.OwnId(Known(("wikidata", "wikidata:Q42")))?.Number);
        Assert.Null(WikidataLookups.OwnId(Known(("wikidata", "nonsense"))));
        Assert.Null(WikidataLookups.OwnId(null));
    }

    [Fact]
    public void A_movie_is_found_by_its_tmdb_movie_id_whichever_way_the_id_is_written()
    {
        foreach (var raw in new[] { "movie:603", "603" })
        {
            var l = Assert.Single(WikidataLookups.For(Known(("tmdb", raw)), "movies", 0));
            Assert.Equal(("P4947", "603"), (l.Property, l.Value));
        }
    }

    [Fact]
    public void A_tv_show_is_found_by_its_tmdb_tv_id_and_a_movie_id_on_a_show_is_ignored()
    {
        Assert.Equal("P4983", Assert.Single(WikidataLookups.For(Known(("tmdb", "tv:1399")), "tv", 0)).Property);
        Assert.Equal("P4983", Assert.Single(WikidataLookups.For(Known(("tmdb", "1399")), "tv", 0)).Property);
        Assert.Empty(WikidataLookups.For(Known(("tmdb", "movie:603")), "tv", 0));   // the wrong kind is never converted
        Assert.Empty(WikidataLookups.For(Known(("tmdb", "tv:1399")), "movies", 0));
    }

    [Fact]
    public void A_season_or_episode_is_not_found_by_its_shows_id()
    {
        Assert.Empty(WikidataLookups.For(Known(("tmdb", "tv:1399"), ("tvdb", "121361")), "tv", 1));
        Assert.Empty(WikidataLookups.For(Known(("tvdb", "121361"), ("tvmaze", "82")), "tv", 2));
    }

    [Fact]
    public void Only_an_imdb_title_id_is_used_never_a_person_or_company()
    {
        Assert.Equal("P345", Assert.Single(WikidataLookups.For(Known(("imdb", "tt0133093")), "movies", 0)).Property);
        Assert.Equal("P345", Assert.Single(WikidataLookups.For(Known(("imdb", "imdb:tt0133093")), "tv", 2)).Property);   // an episode has its own tt id
        Assert.Empty(WikidataLookups.For(Known(("imdb", "nm0000206")), "movies", 0));
        Assert.Empty(WikidataLookups.For(Known(("imdb", "co0001234")), "movies", 0));
        Assert.Empty(WikidataLookups.For(Known(("imdb", "tt12")), "movies", 0));   // too short to be a title id
    }

    [Fact]
    public void A_musicbrainz_id_must_be_of_the_kind_the_hierarchy_level_needs()
    {
        const string mbid = "a74b1b7f-71a5-4011-9441-d0b5e4122711";
        Assert.Equal("P434", Assert.Single(WikidataLookups.For(Known(("musicbrainz", "artist:" + mbid)), "music", 0)).Property);
        Assert.Equal("P436", Assert.Single(WikidataLookups.For(Known(("musicbrainz", "release-group:" + mbid)), "music", 1)).Property);
        Assert.Equal("P4404", Assert.Single(WikidataLookups.For(Known(("musicbrainz", "recording:" + mbid)), "music", 2)).Property);
        Assert.Empty(WikidataLookups.For(Known(("musicbrainz", "recording:" + mbid)), "music", 0));      // a recording id on an artist
        Assert.Empty(WikidataLookups.For(Known(("musicbrainz", "artist:" + mbid)), "music", 2));
        Assert.Empty(WikidataLookups.For(Known(("musicbrainz", mbid)), "music", 0));                     // bare: kind unknown, not guessed
        Assert.Empty(WikidataLookups.For(Known(("musicbrainz", "artist:not-a-uuid")), "music", 0));
    }

    [Fact]
    public void A_game_is_found_by_steam_id_or_igdb_slug_and_never_by_igdbs_numeric_id()
    {
        Assert.Equal("P1733", Assert.Single(WikidataLookups.For(Known(("steam", "steam:553850")), "game", 0)).Property);
        Assert.Equal("P5794", Assert.Single(WikidataLookups.For(Known(("igdb", "helldivers-2")), "game", 0)).Property);
        Assert.Equal("P5794", Assert.Single(WikidataLookups.For(Known(("igdb", "igdb:Helldivers-2")), "game", 0)).Value == "helldivers-2" ? "P5794" : "wrong");
        Assert.Empty(WikidataLookups.For(Known(("igdb", "135400")), "game", 0));      // Wikidata holds IGDB's slug, not its number
        Assert.Empty(WikidataLookups.For(Known(("steam", "x")), "game", 0));
    }

    [Fact]
    public void A_book_is_found_by_an_open_library_id()
    {
        Assert.Equal("P648", Assert.Single(WikidataLookups.For(Known(("openlibrary", "OL45883W")), "book", 0)).Property);
        Assert.Empty(WikidataLookups.For(Known(("openlibrary", "12345")), "book", 0));
    }

    [Fact]
    public void An_unsupported_type_or_no_known_ids_yields_no_lookups()
    {
        Assert.Empty(WikidataLookups.For(Known(("steam", "553850")), "movies", 0));
        Assert.Empty(WikidataLookups.For(null, "movies", 0));
        Assert.Empty(WikidataLookups.For(Known(), "movies", 0));
        Assert.Empty(WikidataLookups.For(Known(("steam", "553850")), null, 0));
    }
}
