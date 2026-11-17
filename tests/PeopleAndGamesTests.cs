using System.Text.Json;
using Chronicle.Plugins.Models;
using Xunit;

namespace Chronicle.Plugin.Wikidata.Tests;

/// <summary>A person found by an id, and what a game gains from Wikidata beyond its ids (design section 12).</summary>
public class PeopleAndGamesTests
{
    private static Dictionary<string, WdRef> Occupations() => new()
    {
        ["Q10798782"] = new("Q10798782", "television actor", null),
        ["Q10800557"] = new("Q10800557", "film actor", null),
        ["Q33999"]    = new("Q33999", "actor", null),
    };

    private static MediaSearchContext Person(params (string, string)[] known) =>
        new(Name: "michael j fox", HierarchyLevel: 0, MediaTypeName: "people", IsRealHierarchyPosition: true,
            KnownExternalIds: known.ToDictionary(k => k.Item1, k => k.Item2));

    // ── the recorded person ───────────────────────────────────────────────────

    [Fact]
    public void The_recorded_person_is_a_human_with_a_portrait_and_a_birth_date()
    {
        var fox = Fixture.Fox();
        Assert.True(WikidataMapper.IsHuman(fox));

        var m = WikidataMapper.Map(fox, Occupations());
        Assert.Equal("Michael J. Fox", m.Title);
        Assert.Equal("wikidata:Q395274", m.ExternalId);

        // The portrait is the preferred-rank P18, not the first one listed.
        Assert.Equal("https://commons.wikimedia.org/wiki/Special:FilePath/Michael_j_fox_1988_emmy.jpg?width=600", m.PosterUrl);
        Assert.Contains("television actor", m.Tags);
        Assert.Contains("actor", m.Tags);

        var ext = m.ExtendedData!.Value;
        Assert.Equal("1961-06-09", ext.GetProperty("birthDate").GetString());      // the host's field name, and a full date
        Assert.False(ext.TryGetProperty("deathDate", out _));                       // he is alive: nothing is invented
    }

    [Fact]
    public void A_person_publishes_the_imdb_name_id_and_the_tmdb_person_id_in_the_types_the_host_accepts()
    {
        var ids = WikidataMapper.PublishedIds(Fixture.Fox());

        Assert.Equal("nm0000150", ids["imdb"].GetValue<string>());     // IMDb accepts a string only
        Assert.Equal(521L, ids["tmdb"].GetValue<long>());               // TMDB accepts a number only
        Assert.False(ids.ContainsKey("wikidata"));
        // He is also a musician (the item holds a MusicBrainz artist id); a person's page must not seed the music plugins.
        Assert.False(ids.ContainsKey("musicbrainz"));
        Assert.Equal(["imdb", "tmdb"], ids.Keys.OrderBy(k => k).ToArray());
    }

    [Fact]
    public void A_title_never_publishes_an_imdb_name_id_and_a_person_never_one_for_a_title()
    {
        // The item is not a human here: a stray nm id on it is not published as if it named the title.
        var title = JsonDocument.Parse("{\"id\":\"Q5\",\"claims\":{\"P345\":[{\"mainsnak\":{\"snaktype\":\"value\",\"datatype\":\"external-id\",\"datavalue\":{\"value\":\"nm0000150\",\"type\":\"string\"}},\"rank\":\"normal\"}]}}").RootElement;
        Assert.False(WikidataMapper.PublishedIds(title).ContainsKey("imdb"));

        var person = JsonDocument.Parse("{\"id\":\"Q6\",\"claims\":{\"P31\":[{\"mainsnak\":{\"snaktype\":\"value\",\"datavalue\":{\"value\":{\"entity-type\":\"item\",\"id\":\"Q5\"},\"type\":\"wikibase-entityid\"}},\"rank\":\"normal\"}]," +
                                        "\"P345\":[{\"mainsnak\":{\"snaktype\":\"value\",\"datatype\":\"external-id\",\"datavalue\":{\"value\":\"tt0133093\",\"type\":\"string\"}},\"rank\":\"normal\"}]}}").RootElement;
        Assert.False(WikidataMapper.PublishedIds(person).ContainsKey("imdb"));
    }

    [Theory]
    [InlineData("+1961-06-09T00:00:00Z", 11, "1961-06-09")]
    [InlineData("+1961-06-00T00:00:00Z", 10, null)]       // a month is not a day
    [InlineData("+1961-00-00T00:00:00Z", 9, null)]        // nor is a year
    [InlineData("+1960-00-00T00:00:00Z", 8, null)]        // nor a decade
    [InlineData("-0300-01-01T00:00:00Z", 11, null)]       // before the common era is not a date Chronicle can hold
    public void A_birth_date_is_written_only_when_wikidata_knows_it_to_the_day(string time, int precision, string? expected)
    {
        var e = JsonDocument.Parse(
            "{\"id\":\"Q6\",\"labels\":{\"mul\":{\"language\":\"mul\",\"value\":\"x\"}},\"claims\":{" +
            "\"P31\":[{\"mainsnak\":{\"snaktype\":\"value\",\"datavalue\":{\"value\":{\"entity-type\":\"item\",\"id\":\"Q5\"},\"type\":\"wikibase-entityid\"}},\"rank\":\"normal\"}]," +
            $"\"P569\":[{{\"mainsnak\":{{\"snaktype\":\"value\",\"datavalue\":{{\"value\":{{\"time\":\"{time}\",\"timezone\":0,\"precision\":{precision}}},\"type\":\"time\"}}}},\"rank\":\"normal\"}}]}}}}").RootElement;

        var ext = WikidataMapper.Map(e, new Dictionary<string, WdRef>()).ExtendedData!.Value;
        Assert.Equal(expected, ext.TryGetProperty("birthDate", out var b) ? b.GetString() : null);
    }

    [Fact]
    public void A_title_has_no_birth_date_and_its_general_image_is_still_not_a_poster()
    {
        var m = WikidataMapper.Map(Fixture.Helldivers(), new Dictionary<string, WdRef>());
        Assert.False(m.ExtendedData!.Value.TryGetProperty("birthDate", out _));
        Assert.Null(m.PosterUrl);
    }

    // ── finding a person by id ────────────────────────────────────────────────

    [Fact]
    public async Task A_person_is_found_by_the_imdb_name_id_and_fetched_with_the_dates_and_photo()
    {
        var h = TestClient.HelldiversFixtures();
        var r = await TestClient.Provider(h).SearchAsync(Person(("imdb", "imdb:nm0000150")));

        var c = Assert.Single(r);
        Assert.Equal("imdb id match", c.ScoreReason);
        Assert.True(c.IdentifierMatch);
        Assert.Equal("wikidata:Q395274", c.Metadata.ExternalId);
        Assert.Equal("1961-06-09", c.Metadata.ExtendedData!.Value.GetProperty("birthDate").GetString());
        Assert.Contains("Michael_j_fox_1988_emmy.jpg", c.Metadata.PosterUrl);
        Assert.Contains(h.Requests, u => StubHandler.Param(new HttpRequestMessage(HttpMethod.Get, u), "srsearch") == "haswbstatement:P345=nm0000150");
    }

    [Fact]
    public async Task A_person_is_found_by_the_tmdb_person_id_written_the_way_the_host_stores_it()
    {
        foreach (var raw in new[] { "tmdb:521", "521", "person:521" })
        {
            var h = TestClient.HelldiversFixtures();
            var r = await TestClient.Provider(h).SearchAsync(Person(("tmdb", raw)));
            Assert.Equal("tmdb id match", Assert.Single(r).ScoreReason);
            Assert.Contains(h.Requests, u => StubHandler.Param(new HttpRequestMessage(HttpMethod.Get, u), "srsearch") == "haswbstatement:P4985=521");
        }
    }

    [Fact]
    public async Task A_movie_or_tv_tmdb_id_on_a_person_is_not_read_as_the_persons()
    {
        var h = TestClient.HelldiversFixtures();
        Assert.Empty(await TestClient.Provider(h).SearchAsync(Person(("tmdb", "movie:521"))));
        Assert.Empty(await TestClient.Provider(h).SearchAsync(Person(("tmdb", "tv:521"))));
        Assert.Equal(0, h.CallCount);
    }

    [Fact]
    public async Task A_person_is_never_found_by_name_not_automatically_and_not_interactively()
    {
        var h = TestClient.HelldiversFixtures();
        var p = TestClient.Provider(h);

        Assert.Empty(await p.SearchAsync(Person()));                                  // automatic, no id
        Assert.Empty(await p.SearchAsync(Person() with { IsRealHierarchyPosition = false }));   // Add Media or Fix Match, typed name
        Assert.Equal(0, h.CallCount);                                                 // not one request: two people share a name
    }

    [Fact]
    public void The_person_type_is_declared_as_tmdb_declares_it_and_only_with_fields_the_plugin_fills()
    {
        var people = new WikidataMetadataProvider().GetSupportedMediaTypes().Single(t => t.MediaTypeName == "people");

        Assert.Equal(1, people.HierarchyLevels);
        Assert.Equal("viewed", people.InteractionVerb);
        Assert.Equal("percent", people.ProgressUnit);
        Assert.True(string.IsNullOrEmpty(people.DisplayName));
        Assert.Equal(["title", "poster_url", "birth_date", "death_date", "tags"], people.SupportedFields.ToArray());
    }

    // ── what a game gains ─────────────────────────────────────────────────────

    private static JsonElement Game(string claims) =>
        JsonDocument.Parse("{\"id\":\"Q9\",\"labels\":{\"mul\":{\"language\":\"mul\",\"value\":\"A Game\"}},\"claims\":{" + claims + "}}").RootElement;

    private static string Claim(string prop, string id) =>
        $"\"{prop}\":[{{\"mainsnak\":{{\"snaktype\":\"value\",\"datavalue\":{{\"value\":{{\"entity-type\":\"item\",\"id\":\"{id}\"}},\"type\":\"wikibase-entityid\"}}}},\"rank\":\"normal\"}}]";

    [Fact]
    public void A_games_platforms_modes_and_engine_become_tags_and_its_makers_become_crew()
    {
        var e = Game(string.Join(",",
            Claim("P400", "Q1"), Claim("P404", "Q2"), Claim("P408", "Q3"),
            Claim("P287", "Q4"), Claim("P943", "Q5"), Claim("P170", "Q6"), Claim("P3174", "Q7")));
        var refs = Enumerable.Range(1, 7).ToDictionary(i => "Q" + i, i => new WdRef("Q" + i, new[] { "PlayStation 5", "single-player video game", "Unreal Engine", "Ann Designer", "Bo Programmer", "Cy Creator", "Di Artist" }[i - 1], null));

        var m = WikidataMapper.Map(e, refs);

        Assert.Equal(["PlayStation 5", "single-player video game", "Unreal Engine"], m.Tags);
        Assert.Equal([("Ann Designer", "Designer"), ("Bo Programmer", "Programmer"), ("Cy Creator", "Creator"), ("Di Artist", "Art Director")],
            m.Crew.Select(c => (c.Name, c.Job!)).ToArray());
        Assert.All(m.Crew, c => Assert.StartsWith("wikidata:Q", c.ExternalPersonId));
    }

    [Fact]
    public void Those_references_are_asked_for_by_name()
    {
        var ids = WikidataMapper.ReferencedIds(Game(string.Join(",", Claim("P400", "Q1"), Claim("P287", "Q4"), Claim("P408", "Q3"))));
        Assert.Equal(["Q1", "Q3", "Q4"], ids.Keys.OrderBy(k => k).ToArray());
    }

    // ── D10: every person a record names is resolved to their own record ───────

    private static string ItemClaim(string prop, string id, string? qualifiers = null) =>
        $"\"{prop}\":[{{\"mainsnak\":{{\"snaktype\":\"value\",\"datavalue\":{{\"value\":{{\"entity-type\":\"item\",\"id\":\"{id}\"}},\"type\":\"wikibase-entityid\"}}}},\"rank\":\"normal\"" +
        (qualifiers is null ? "" : $",\"qualifiers\":{qualifiers}") + "}]";

    /// <summary>Several statements of one property in ONE array. (Two separate "P527" members of one object keep only the last.)</summary>
    private static string ItemClaims(string prop, params (string Id, string? Qualifiers)[] items) =>
        $"\"{prop}\":[" + string.Join(",", items.Select(i =>
            $"{{\"mainsnak\":{{\"snaktype\":\"value\",\"datavalue\":{{\"value\":{{\"entity-type\":\"item\",\"id\":\"{i.Id}\"}},\"type\":\"wikibase-entityid\"}}}},\"rank\":\"normal\"" +
            (i.Qualifiers is null ? "" : $",\"qualifiers\":{i.Qualifiers}") + "}")) + "]";

    [Fact]
    public void Every_cast_and_crew_person_is_marked_for_their_own_record_not_just_a_name()
    {
        var film = JsonDocument.Parse("{\"id\":\"Q1\",\"claims\":{" + string.Join(",",
            ItemClaim("P161", "Q10"), ItemClaim("P57", "Q11"), ItemClaim("P725", "Q12"), ItemClaim("P1431", "Q13"), ItemClaim("P136", "Q14")) + "}}").RootElement;

        var ids = WikidataMapper.ReferencedIds(film);

        Assert.True(ids["Q10"]);    // cast: the portrait and ids are wanted
        Assert.True(ids["Q11"]);    // a director
        Assert.True(ids["Q12"]);    // a voice actor
        Assert.True(ids["Q13"]);    // an executive producer
        Assert.False(ids["Q14"]);   // a genre needs only its name
    }

    [Fact]
    public void A_cast_member_and_a_crew_member_carry_their_portrait()
    {
        var film = JsonDocument.Parse("{\"id\":\"Q1\",\"labels\":{\"mul\":{\"language\":\"mul\",\"value\":\"F\"}},\"claims\":{" + string.Join(",",
            ItemClaim("P161", "Q10"), ItemClaim("P57", "Q11")) + "}}").RootElement;
        var refs = new Dictionary<string, WdRef>
        {
            ["Q10"] = new("Q10", "An Actor", null, "Actor portrait.jpg", true),
            ["Q11"] = new("Q11", "A Director", null, null, true),     // Wikidata has no portrait for this one: none is invented
        };

        var m = WikidataMapper.Map(film, refs);

        Assert.Equal("https://commons.wikimedia.org/wiki/Special:FilePath/Actor_portrait.jpg?width=300", m.Cast.Single().ProfileImageUrl);
        Assert.Null(m.Crew.Single().ProfileImageUrl);
        Assert.Equal("wikidata:Q11", m.Crew.Single().ExternalPersonId);
    }

    [Fact]
    public void A_games_voice_actors_are_cast_with_the_character_they_voiced()
    {
        var game = JsonDocument.Parse("{\"id\":\"Q1\",\"claims\":{" +
            ItemClaims("P725",
                ("Q10", "{\"P453\":[{\"snaktype\":\"value\",\"property\":\"P453\",\"datavalue\":{\"value\":{\"entity-type\":\"item\",\"id\":\"Q20\"},\"type\":\"wikibase-entityid\"}}]}"),
                ("Q11", null)) + "}}").RootElement;
        var refs = new Dictionary<string, WdRef>
        {
            ["Q10"] = new("Q10", "Voice One", null, "One.jpg", true), ["Q11"] = new("Q11", "Voice Two", null, null, true), ["Q20"] = new("Q20", "The Hero", null),
        };

        var cast = WikidataMapper.Map(game, refs).Cast;

        Assert.Equal([("Voice One", "The Hero"), ("Voice Two", "Voice actor")], cast.Select(c => (c.Name, c.Role!)).ToArray());
        Assert.Contains("One.jpg", cast[0].ProfileImageUrl);
    }

    [Fact]
    public void A_bands_members_are_cast_but_only_the_humans_and_only_on_a_musical_act()
    {
        // The act holds a MusicBrainz artist id; one of its "has parts" is a person, one is a record (not a person).
        var act = JsonDocument.Parse("{\"id\":\"Q1\",\"claims\":{" + ItemClaims("P527", ("Q10", null), ("Q11", null)) + "," +
            "\"P434\":[{\"mainsnak\":{\"snaktype\":\"value\",\"datatype\":\"external-id\",\"datavalue\":{\"value\":\"a74b1b7f-71a5-4011-9441-d0b5e4122711\",\"type\":\"string\"}},\"rank\":\"normal\"}]}}").RootElement;
        var refs = new Dictionary<string, WdRef>
        {
            ["Q10"] = new("Q10", "Thom Yorke", null, "Thom.jpg", true), ["Q11"] = new("Q11", "A Record", null, null, false),
        };

        var cast = WikidataMapper.Map(act, refs).Cast;
        Assert.Equal([("Thom Yorke", "Member")], cast.Select(c => (c.Name, c.Role!)).ToArray());
        Assert.Contains("Thom.jpg", cast.Single().ProfileImageUrl);
        Assert.True(WikidataMapper.ReferencedIds(act)["Q10"]);

        // The same "has parts" on something that is not a musical act is not a member list (a series lists its episodes).
        var series = JsonDocument.Parse("{\"id\":\"Q2\",\"claims\":{" + ItemClaim("P527", "Q10") + "}}").RootElement;
        Assert.Empty(WikidataMapper.Map(series, refs).Cast);
        Assert.False(WikidataMapper.ReferencedIds(series).ContainsKey("Q10"));
    }

    [Fact]
    public void The_rest_of_a_credit_list_is_read_executive_producers_costume_designers_lyricists_conductors_choreographers()
    {
        var work = JsonDocument.Parse("{\"id\":\"Q1\",\"claims\":{" + string.Join(",",
            ItemClaim("P1431", "Q1"), ItemClaim("P2515", "Q2"), ItemClaim("P676", "Q3"), ItemClaim("P3300", "Q4"), ItemClaim("P1809", "Q5")) + "}}").RootElement;
        var refs = Enumerable.Range(1, 5).ToDictionary(i => "Q" + i, i => new WdRef("Q" + i, "Person " + i, null, null, true));

        var crew = WikidataMapper.Map(work, refs).Crew;

        Assert.Equal(["Executive Producer", "Costume Designer", "Lyricist", "Conductor", "Choreographer"], crew.Select(c => c.Job!).ToArray());
    }

    [Fact]
    public async Task A_provider_fetch_asks_for_the_whole_record_of_every_credited_person_and_puts_their_portrait_on_the_credit()
    {
        var propsAsked = new List<string>();
        var h = new StubHandler(r =>
        {
            var action = StubHandler.Param(r, "action");
            if (action != "wbgetentities") return StubHandler.Json("{}", System.Net.HttpStatusCode.NotFound);
            var ids = StubHandler.Param(r, "ids");
            propsAsked.Add(ids + "=" + StubHandler.Param(r, "props"));
            if (ids == "Q1")
                return StubHandler.Json(Fixture.Entities(("Q1", "{\"id\":\"Q1\",\"labels\":{\"mul\":{\"language\":\"mul\",\"value\":\"A Film\"}},\"claims\":{" + ItemClaim("P161", "Q10") + "," + ItemClaim("P136", "Q14") + "}}")));
            if (ids == "Q10")
                return StubHandler.Json(Fixture.Entities(("Q10", "{\"id\":\"Q10\",\"labels\":{\"mul\":{\"language\":\"mul\",\"value\":\"An Actor\"}},\"claims\":{" +
                    ItemClaim("P31", "Q5") + ",\"P18\":[{\"mainsnak\":{\"snaktype\":\"value\",\"datavalue\":{\"value\":\"Actor face.jpg\",\"type\":\"string\"}},\"rank\":\"normal\"}]}}")));
            return StubHandler.Json(Fixture.Entities((ids, Fixture.LabelOnly(ids, "comedy"))));
        });

        var m = await TestClient.Provider(h).GetByIdAsync("wikidata:Q1");

        var actor = Assert.Single(m.Cast);
        Assert.Equal("An Actor", actor.Name);
        Assert.Equal("https://commons.wikimedia.org/wiki/Special:FilePath/Actor_face.jpg?width=300", actor.ProfileImageUrl);
        Assert.Contains("Q10=labels|claims", propsAsked);     // the person's whole record, not only the name
        Assert.Contains("Q14=labels", propsAsked);            // a genre needs only its name
    }
}
