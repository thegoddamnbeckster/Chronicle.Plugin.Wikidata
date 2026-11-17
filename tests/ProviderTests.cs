using System.Net;
using Chronicle.Plugins.Models;
using Xunit;

namespace Chronicle.Plugin.Wikidata.Tests;

public class ProviderTests
{
    private static MediaSearchContext Ctx(string name, string type, int level = 0, bool real = true,
        IReadOnlyDictionary<string, string>? known = null, int? year = null) =>
        new(Name: name, Year: year, HierarchyLevel: level, MediaTypeName: type, KnownExternalIds: known, IsRealHierarchyPosition: real);

    private static Dictionary<string, string> Known(params (string, string)[] p) => p.ToDictionary(x => x.Item1, x => x.Item2);

    // ── automatic: by id or absent ────────────────────────────────────────────

    [Fact]
    public async Task A_game_with_a_steam_id_is_matched_fetched_in_full_and_marked_an_id_match()
    {
        var h = TestClient.HelldiversFixtures();
        var r = await TestClient.Provider(h).SearchAsync(Ctx("helldivers 2", "game", known: Known(("steam", "steam:553850"))));

        var c = Assert.Single(r);
        Assert.Equal(100, c.Score);
        Assert.True(c.IdentifierMatch);
        Assert.Equal("steam id match", c.ScoreReason);
        Assert.Equal("wikidata:Q118645527", c.Metadata.ExternalId);
        Assert.Equal("Helldivers 2", c.Metadata.Title);
        Assert.Equal(["third-person shooter", "cooperative shooter"], c.Metadata.Genres);   // references resolved: the winner is in full (E5)
        Assert.Equal(553850L, c.Metadata.ExtendedData!.Value.GetProperty("ids").GetProperty("steam").GetInt64());
    }

    [Fact]
    public async Task With_no_id_the_automatic_path_returns_nothing_and_makes_no_request()
    {
        var h = TestClient.HelldiversFixtures();
        var r = await TestClient.Provider(h).SearchAsync(Ctx("helldivers 2", "game"));

        Assert.Empty(r);
        Assert.Equal(0, h.CallCount);   // I5: automatic matching is exact or absent; a name is never searched
    }

    [Fact]
    public async Task An_id_Wikidata_does_not_hold_is_no_match_not_an_error()
    {
        var h = TestClient.HelldiversFixtures();
        var r = await TestClient.Provider(h).SearchAsync(Ctx("x", "game", known: Known(("steam", "999999999"))));
        Assert.Empty(r);
    }

    [Fact]
    public async Task An_id_held_by_two_items_is_a_duplicate_and_matches_neither()
    {
        var h = new StubHandler(r => StubHandler.Param(r, "action") == "query"
            ? StubHandler.Json("{\"query\":{\"search\":[{\"title\":\"Q1\"},{\"title\":\"Q2\"}]}}")
            : StubHandler.Json("{\"entities\":{}}"));
        var r = await TestClient.Provider(h).SearchAsync(Ctx("x", "game", known: Known(("steam", "1"))));

        Assert.Empty(r);
        Assert.Equal(1, h.CallCount);   // no item was fetched
    }

    [Fact]
    public async Task Another_lookup_is_tried_when_the_first_is_a_duplicate_or_absent()
    {
        var calls = 0;
        var h = new StubHandler(r =>
        {
            if (StubHandler.Param(r, "action") == "query")
                return ++calls == 1 ? StubHandler.Json("{\"query\":{\"search\":[]}}") : StubHandler.Json(Fixture.Text("p1_steam"));
            return StubHandler.Json(Fixture.Text("p2_helldivers"));
        });
        var r = await TestClient.Provider(h).SearchAsync(Ctx("x", "game", known: Known(("steam", "1"), ("igdb", "helldivers-2"))));

        Assert.Equal("wikidata:Q118645527", Assert.Single(r).Metadata.ExternalId);
    }

    [Fact]
    public async Task A_held_wikidata_id_is_fetched_directly_without_a_lookup()
    {
        var h = TestClient.HelldiversFixtures();
        var r = await TestClient.Provider(h).SearchAsync(Ctx("x", "game", known: Known(("wikidata", "wikidata:Q118645527"))));

        Assert.Equal("wikidata id match", Assert.Single(r).ScoreReason);
        Assert.DoesNotContain(h.Requests, u => u.Query.Contains("haswbstatement"));
    }

    [Fact]
    public async Task A_type_the_plugin_does_not_serve_gets_an_empty_list_with_no_request()
    {
        var h = TestClient.HelldiversFixtures();
        Assert.Empty(await TestClient.Provider(h).SearchAsync(Ctx("x", "podcast", known: Known(("steam", "553850")))));
        Assert.Equal(0, h.CallCount);
    }

    [Fact]
    public async Task A_musicbrainz_id_of_the_wrong_kind_for_the_level_makes_no_request()
    {
        var h = TestClient.HelldiversFixtures();
        var r = await TestClient.Provider(h).SearchAsync(Ctx("x", "music", level: 0, known: Known(("musicbrainz", "recording:a74b1b7f-71a5-4011-9441-d0b5e4122711"))));
        Assert.Empty(r);
        Assert.Equal(0, h.CallCount);
    }

    // ── typed ids ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Q118645527")]
    [InlineData("wikidata:Q118645527")]
    [InlineData("https://www.wikidata.org/wiki/Q118645527")]
    public async Task A_typed_id_or_address_is_the_answer_at_100(string typed)
    {
        var r = await TestClient.Provider(TestClient.HelldiversFixtures()).SearchAsync(Ctx(typed, "game", real: false));
        var c = Assert.Single(r);
        Assert.Equal((100, "id given"), (c.Score, c.ScoreReason));
        Assert.True(c.IdentifierMatch);
    }

    [Fact]
    public async Task On_the_automatic_path_an_items_own_name_that_looks_like_an_id_is_not_taken_for_one()
    {
        var h = TestClient.HelldiversFixtures();
        var p = TestClient.Provider(h);

        Assert.Empty(await p.SearchAsync(Ctx("Q118645527", "game")));                       // a game really called "Q118645527"
        Assert.Equal(0, h.CallCount);
        Assert.Single(await p.SearchAsync(Ctx("wikidata:Q118645527", "game")));              // an explicit statement is honoured
    }

    [Fact]
    public async Task A_season_or_episode_is_not_searched_for_by_name()
    {
        var h = TestClient.HelldiversFixtures();
        Assert.Empty(await TestClient.Provider(h).SearchAsync(Ctx("Season 2", "tv", level: 1, real: false)));
        Assert.Equal(0, h.CallCount);
    }

    // ── interactive: by name, filtered by kind ────────────────────────────────

    [Fact]
    public async Task The_interactive_search_lists_the_game_and_drops_the_other_hits()
    {
        var h = TestClient.HelldiversFixtures();
        var r = await TestClient.Provider(h).SearchAsync(Ctx("Helldivers 2", "game", real: false));

        var c = Assert.Single(r);                                  // "Helldivers 2 review bombing" is not a video game
        Assert.Equal("Q118645527", c.Metadata.ExtendedData!.Value.GetProperty("id").GetString());
        Assert.Equal(90, c.Score);
        Assert.Equal(2024, c.Metadata.Year);
        Assert.Single(h.Requests);                                 // one request: lean, inside the host's 2.5 s
    }

    [Fact]
    public async Task The_interactive_search_for_dune_keeps_the_game_for_a_game_and_drops_the_novel_the_band_and_the_landform()
    {
        var game = await TestClient.Provider(TestClient.HelldiversFixtures()).SearchAsync(Ctx("Dune", "game", real: false));
        Assert.Equal("Q1265597", Assert.Single(game).Metadata.ExtendedData!.Value.GetProperty("id").GetString());

        var movies = await TestClient.Provider(TestClient.HelldiversFixtures()).SearchAsync(Ctx("Dune", "movies", real: false));
        Assert.Empty(movies);
    }

    [Fact]
    public async Task A_year_that_differs_lowers_the_score_and_a_match_raises_it()
    {
        var r1 = await TestClient.Provider(TestClient.HelldiversFixtures()).SearchAsync(Ctx("Dune", "game", real: false, year: 1992));
        var r2 = await TestClient.Provider(TestClient.HelldiversFixtures()).SearchAsync(Ctx("Dune", "game", real: false, year: 2021));
        Assert.Equal(95, Assert.Single(r1).Score);
        Assert.Equal(60, Assert.Single(r2).Score);
    }

    [Fact]
    public async Task The_interactive_search_never_throws_it_returns_an_empty_list_when_wikidata_fails()
    {
        var h = new StubHandler(_ => StubHandler.Json("{}", HttpStatusCode.ServiceUnavailable));
        var r = await TestClient.Provider(h).SearchAsync(Ctx("Helldivers 2", "game", real: false));
        Assert.Empty(r);
    }

    // ── get by id ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Get_by_id_returns_the_full_item_and_a_second_call_for_it_is_served_from_the_search()
    {
        var h = TestClient.HelldiversFixtures();
        var p = TestClient.Provider(h);
        await p.SearchAsync(Ctx("x", "game", known: Known(("steam", "553850"))));
        var before = h.CallCount;

        var m = await p.GetByIdAsync("wikidata:Q118645527");

        Assert.Equal("Helldivers 2", m.Title);
        Assert.Equal(before, h.CallCount);   // the host asks right after a search; the answer is already held
    }

    [Fact]
    public async Task Garbage_is_an_argument_exception_the_host_clears_and_a_missing_item_is_key_not_found()
    {
        var p = TestClient.Provider(new StubHandler(r => StubHandler.Json("{\"entities\":{\"Q999\":{\"id\":\"Q999\",\"missing\":\"\"}}}")));

        await Assert.ThrowsAsync<ArgumentException>(() => p.GetByIdAsync("tt0133093"));
        await Assert.ThrowsAsync<ArgumentException>(() => p.GetByIdAsync("Q42x"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => p.GetByIdAsync("wikidata:Q999"));
    }

    [Fact]
    public async Task A_busy_service_is_a_pause_for_the_host_never_an_empty_item()
    {
        var p = TestClient.Provider(new StubHandler(_ => StubHandler.Json("{}", HttpStatusCode.TooManyRequests)));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => p.GetByIdAsync("Q42"));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
    }

    [Fact]
    public async Task One_item_that_always_fails_is_exhausted_after_three_tries_so_it_cannot_block_the_queue()
    {
        var p = TestClient.Provider(new StubHandler(_ => StubHandler.Json("{\"error\":{\"code\":\"internal_api_error\",\"info\":\"boom\"}}")));

        await Assert.ThrowsAsync<WdRefusedException>(() => p.GetByIdAsync("Q42"));
        await Assert.ThrowsAsync<WdRefusedException>(() => p.GetByIdAsync("Q42"));
        await Assert.ThrowsAsync<InvalidDataException>(() => p.GetByIdAsync("Q42"));
        // another item is unaffected
        await Assert.ThrowsAsync<WdRefusedException>(() => p.GetByIdAsync("Q43"));
    }

    [Fact]
    public async Task A_busy_service_never_counts_against_an_item()
    {
        var p = TestClient.Provider(new StubHandler(_ => StubHandler.Json("{}", HttpStatusCode.TooManyRequests)));
        for (var i = 0; i < 6; i++)
            await Assert.ThrowsAsync<HttpRequestException>(() => p.GetByIdAsync("Q42"));   // never InvalidDataException
    }

    [Fact]
    public async Task A_success_clears_an_items_failure_count()
    {
        var fail = true;
        var p = TestClient.Provider(new StubHandler(r => fail
            ? StubHandler.Json("{\"error\":{\"code\":\"x\"}}")
            : StubHandler.Json(Fixture.Entities((StubHandler.Param(r, "ids"), Fixture.LabelOnly(StubHandler.Param(r, "ids"), "Douglas Adams"))))));

        await Assert.ThrowsAsync<WdRefusedException>(() => p.GetByIdAsync("Q42"));
        await Assert.ThrowsAsync<WdRefusedException>(() => p.GetByIdAsync("Q42"));
        fail = false;
        Assert.Equal("Douglas Adams", (await p.GetByIdAsync("Q42")).Title);
        await p.GetByIdAsync("Q43");          // replaces the one-item cache, so Q42 is asked of the service again
        fail = true;

        // Without the reset Q42 would already be at three and exhausted on this first call.
        await Assert.ThrowsAsync<WdRefusedException>(() => p.GetByIdAsync("Q42"));
        await Assert.ThrowsAsync<WdRefusedException>(() => p.GetByIdAsync("Q42"));
        await Assert.ThrowsAsync<InvalidDataException>(() => p.GetByIdAsync("Q42"));
    }

    [Fact]
    public async Task A_html_block_pauses_the_pass_it_is_not_an_empty_result()
    {
        var p = TestClient.Provider(new StubHandler(_ => StubHandler.Html()));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => p.GetByIdAsync("Q42"));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
    }

    [Fact]
    public async Task Get_image_is_a_refusal_with_a_reason()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => TestClient.Provider(TestClient.HelldiversFixtures()).GetImageAsync("https://x/y.png"));
    }

    [Fact]
    public async Task The_health_check_is_one_cheap_read_and_false_not_an_exception_when_it_fails()
    {
        var ok = new StubHandler(_ => StubHandler.Json(Fixture.Entities(("Q42", "{\"id\":\"Q42\"}"))));
        Assert.True(await TestClient.Provider(ok).HealthCheckAsync());
        Assert.Equal(1, ok.CallCount);

        var down = new StubHandler(_ => StubHandler.Json("{}", HttpStatusCode.BadGateway));
        Assert.False(await TestClient.Provider(down).HealthCheckAsync());
    }

    // ── interactive class filter ──────────────────────────────────────────────

    [Theory]
    [InlineData("game", 0, "2024 video game developed by Arrowhead Game Studios", true)]
    [InlineData("game", 0, "video game series", false)]
    [InlineData("game", 0, "Swedish video game developer", false)]
    [InlineData("game", 0, "mass review bombing of Helldivers 2", false)]
    [InlineData("movies", 0, "1999 science fiction film by the Wachowskis", true)]
    [InlineData("movies", 0, "film series", false)]
    [InlineData("movies", 0, "soundtrack album to the film", false)]
    [InlineData("tv", 0, "American television series", true)]
    [InlineData("tv", 0, "2008 animated television series", true)]
    [InlineData("tv", 0, "episode of television series", false)]
    [InlineData("book", 0, "1965 science fiction novel by Frank Herbert", true)]
    [InlineData("book", 0, "2021 film", false)]
    [InlineData("music", 0, "English rock band", true)]
    [InlineData("music", 0, "1997 studio album by Radiohead", false)]
    [InlineData("music", 1, "1997 studio album by Radiohead", true)]
    [InlineData("music", 2, "1987 single by Rick Astley", true)]
    [InlineData("movies", 0, "American film director", false)]
    [InlineData("movies", 0, "American film studio", false)]
    [InlineData("tv", 0, "American television producer", false)]
    [InlineData("music", 0, "ethnic group in Nigeria", false)]
    [InlineData("music", 0, "South Korean girl group", true)]
    [InlineData("", 0, "English rock band", true)]
    [InlineData("", 0, "2024 video game developed by Arrowhead Game Studios", true)]
    [InlineData("", 0, "landform, hill of sand", false)]
    [InlineData("music", 0, null, false)]
    [InlineData("game", 0, "", false)]
    public void The_description_must_name_the_right_kind_of_thing(string type, int level, string? description, bool fits) =>
        Assert.Equal(fits, WikidataMetadataProvider.ClassFits(type, level, description));
}
