using System.Text.Json;
using Xunit;

namespace Chronicle.Plugin.Wikidata.Tests;

public class MapperTests
{
    private static Dictionary<string, WdRef> Refs() => new()
    {
        ["Q380266"]    = new("Q380266", "third-person shooter", null),
        ["Q129695809"] = new("Q129695809", "cooperative shooter", null),
        ["Q7889"]      = new("Q7889", "video game", null),
        ["Q18594"]     = new("Q18594", "Sony Interactive Entertainment", "Sony logo.svg"),
        ["Q16833903"]  = new("Q16833903", "Arrowhead Game Studios", null),
    };

    private static JsonElement Entity(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Claim(string prop, string valueJson, string datatype = "wikibase-item", string rank = "normal", string? qualifiers = null) =>
        $"\"{prop}\":[{{\"mainsnak\":{{\"snaktype\":\"value\",\"property\":\"{prop}\",\"datatype\":\"{datatype}\",\"datavalue\":{valueJson}}},\"type\":\"statement\",\"rank\":\"{rank}\"" +
        (qualifiers is null ? "" : $",\"qualifiers\":{qualifiers}") + "}]";

    private static string Item(string id) => $"{{\"value\":{{\"entity-type\":\"item\",\"numeric-id\":{id[1..]},\"id\":\"{id}\"}},\"type\":\"wikibase-entityid\"}}";
    private static string Str(string v) => $"{{\"value\":\"{v}\",\"type\":\"string\"}}";
    private static string Time(string t, int precision = 11) => $"{{\"value\":{{\"time\":\"{t}\",\"timezone\":0,\"precision\":{precision}}},\"type\":\"time\"}}";

    private static JsonElement Make(string labels, string claims, string extra = "") =>
        Entity($"{{\"id\":\"Q5\",\"labels\":{{{labels}}},\"claims\":{{{claims}}}{extra}}}");

    // ── the recorded entity ───────────────────────────────────────────────────

    [Fact]
    public void The_recorded_helldivers_entity_maps_its_title_year_genres_and_credits()
    {
        var m = WikidataMapper.Map(Fixture.Helldivers(), Refs());

        Assert.Equal("wikidata:Q118645527", m.ExternalId);
        Assert.Equal("wikidata", m.Source);
        Assert.Equal("Helldivers 2", m.Title);               // the label is in "mul", not "en"
        Assert.Equal(2024, m.Year);
        Assert.Equal("https://www.wikidata.org/wiki/Q118645527", m.SourceUrl);
        Assert.Equal(["third-person shooter", "cooperative shooter"], m.Genres);
        Assert.Equal(["video game"], m.Tags);

        var dev = Assert.Single(m.Cast);
        Assert.Equal(("Arrowhead Game Studios", "Developer", "wikidata:Q16833903"), (dev.Name, dev.Role, dev.ExternalPersonId));
        Assert.Null(dev.ProfileImageUrl);                     // Wikidata has no logo for Arrowhead (measured): none is invented

        var pub = Assert.Single(m.Crew);
        Assert.Equal(("Sony Interactive Entertainment", "Publisher"), (pub.Name, pub.Job));
        Assert.Equal("https://commons.wikimedia.org/wiki/Special:FilePath/Sony_logo.svg?width=300", pub.ProfileImageUrl);
    }

    [Fact]
    public void Every_label_and_alias_in_every_language_is_kept_as_an_alternate_name()
    {
        var m = WikidataMapper.Map(Fixture.Helldivers(), Refs());

        Assert.Contains("絕地戰兵2", m.AlternateNames);
        Assert.Contains("헬다이버즈 2", m.AlternateNames);
        Assert.Contains("Helldivers II", m.AlternateNames);
        Assert.Contains("HII", m.AlternateNames);
        Assert.DoesNotContain("Helldivers 2", m.AlternateNames);   // the title is not its own alternate
        Assert.Equal(m.AlternateNames.Count, m.AlternateNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void The_whole_entity_and_every_external_id_are_kept()
    {
        var raw = Fixture.Helldivers();
        var m = WikidataMapper.Map(raw, Refs());
        var ext = m.ExtendedData!.Value;

        Assert.Equal("Q118645527", ext.GetProperty("id").GetString());
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(                                 // kept as it came, every member
            System.Text.Json.Nodes.JsonNode.Parse(raw.GetRawText()), System.Text.Json.Nodes.JsonNode.Parse(ext.GetProperty("raw").GetRawText())));
        Assert.Equal(159, ext.GetProperty("raw").GetProperty("claims").EnumerateObject().Count());
        Assert.Equal(119, ext.GetProperty("external_ids").EnumerateObject().Count());          // every external-id property, none dropped
        Assert.Equal("553850", ext.GetProperty("external_ids").GetProperty("P1733")[0].GetString());
        Assert.Equal(24, ext.GetProperty("sitelinks").EnumerateObject().Count());
        Assert.Equal("https://en.wikipedia.org/wiki/Helldivers_2", ext.GetProperty("sitelinks").GetProperty("enwiki").GetProperty("url").GetString());
        Assert.Contains("playstation.com", ext.GetProperty("official_websites")[0].GetString());
        Assert.Equal("video game", ext.GetProperty("instance_of")[0].GetProperty("label").GetString());
    }

    [Fact]
    public void Ids_for_the_other_plugins_are_published_from_the_recorded_entity()
    {
        var ids = WikidataMapper.PublishedIds(Fixture.Helldivers());

        Assert.Equal(553850L, ids["steam"].GetValue<long>());          // a number: see PublishedIds
        Assert.Equal("helldivers-2", ids["igdb"].GetValue<string>());
        Assert.Equal("tt27916646", ids["imdb"].GetValue<string>());
        Assert.False(ids.ContainsKey("tmdb"));
        Assert.False(ids.ContainsKey("wikidata"));            // the host skips the producing source; it would be our own id
    }

    [Fact]
    public void The_published_ids_are_in_the_extended_data_block_the_host_reads()
    {
        var ids = WikidataMapper.Map(Fixture.Helldivers(), Refs()).ExtendedData!.Value.GetProperty("ids");
        Assert.Equal(JsonValueKind.Number, ids.GetProperty("steam").ValueKind);
        Assert.Equal(553850L, ids.GetProperty("steam").GetInt64());
        Assert.Equal("helldivers-2", ids.GetProperty("igdb").GetString());
    }

    // ── labels ────────────────────────────────────────────────────────────────

    [Fact]
    public void A_label_is_read_en_then_mul_then_any_language()
    {
        Assert.Equal("English", WikidataMapper.Label(Make("\"en\":{\"language\":\"en\",\"value\":\"English\"},\"mul\":{\"language\":\"mul\",\"value\":\"Neutral\"}", "")));
        Assert.Equal("Neutral", WikidataMapper.Label(Make("\"mul\":{\"language\":\"mul\",\"value\":\"Neutral\"},\"de\":{\"language\":\"de\",\"value\":\"Deutsch\"}", "")));
        Assert.Equal("Deutsch", WikidataMapper.Label(Make("\"de\":{\"language\":\"de\",\"value\":\"Deutsch\"}", "")));
        Assert.Null(WikidataMapper.Label(Make("", "")));
    }

    // ── years ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("+1999-03-31T00:00:00Z", 11, 1999)]
    [InlineData("+2009-00-00T00:00:00Z", 9, 2009)]          // a year with no month, as Wikidata writes it
    [InlineData("+1990-00-00T00:00:00Z", 8, null)]           // a decade is not a year
    [InlineData("+1900-00-00T00:00:00Z", 7, null)]           // nor a century
    [InlineData("-0300-00-00T00:00:00Z", 9, null)]           // before the common era
    public void A_year_is_taken_only_from_a_date_precise_to_a_year(string time, int precision, int? expected) =>
        Assert.Equal(expected, WikidataMapper.Map(Make("\"mul\":{\"language\":\"mul\",\"value\":\"x\"}", Claim("P577", Time(time, precision), "time")), new Dictionary<string, WdRef>()).Year);

    [Fact]
    public void The_earliest_publication_date_wins_and_inception_is_the_fallback()
    {
        var two = Entity("{\"id\":\"Q5\",\"claims\":{\"P577\":[" +
            "{\"mainsnak\":{\"snaktype\":\"value\",\"datavalue\":" + Time("+2001-01-01T00:00:00Z") + "},\"rank\":\"normal\"}," +
            "{\"mainsnak\":{\"snaktype\":\"value\",\"datavalue\":" + Time("+1999-05-05T00:00:00Z") + "},\"rank\":\"normal\"}]}}");
        Assert.Equal(1999, WikidataMapper.Map(two, new Dictionary<string, WdRef>()).Year);

        var founded = Make("", Claim("P571", Time("+1985-01-01T00:00:00Z"), "time"));
        Assert.Equal(1985, WikidataMapper.Map(founded, new Dictionary<string, WdRef>()).Year);
    }

    // ── ranks and odd values ──────────────────────────────────────────────────

    [Fact]
    public void A_deprecated_statement_is_not_used_and_a_preferred_one_wins()
    {
        var e = Entity("{\"id\":\"Q5\",\"claims\":{\"P136\":[" +
            "{\"mainsnak\":{\"snaktype\":\"value\",\"datavalue\":" + Item("Q1") + "},\"rank\":\"deprecated\"}," +
            "{\"mainsnak\":{\"snaktype\":\"value\",\"datavalue\":" + Item("Q2") + "},\"rank\":\"normal\"}," +
            "{\"mainsnak\":{\"snaktype\":\"value\",\"datavalue\":" + Item("Q3") + "},\"rank\":\"preferred\"}]}}");
        var refs = new Dictionary<string, WdRef> { ["Q1"] = new("Q1", "dep", null), ["Q2"] = new("Q2", "norm", null), ["Q3"] = new("Q3", "pref", null) };

        Assert.Equal(["pref"], WikidataMapper.Map(e, refs).Genres);
    }

    [Fact]
    public void A_statement_with_no_value_is_skipped()
    {
        var e = Entity("{\"id\":\"Q5\",\"claims\":{\"P136\":[{\"mainsnak\":{\"snaktype\":\"somevalue\"},\"rank\":\"normal\"}]}}");
        Assert.Empty(WikidataMapper.Map(e, new Dictionary<string, WdRef>()).Genres);
    }

    [Fact]
    public void A_referenced_item_that_could_not_be_named_is_not_listed_but_stays_in_the_raw_entity()
    {
        var e = Make("\"mul\":{\"language\":\"mul\",\"value\":\"x\"}", Claim("P136", Item("Q77")));
        var m = WikidataMapper.Map(e, new Dictionary<string, WdRef>());
        Assert.Empty(m.Genres);
        Assert.Contains("Q77", m.ExtendedData!.Value.GetProperty("raw").GetRawText());
    }

    [Fact]
    public void An_entity_with_nothing_but_an_id_maps_without_throwing()
    {
        var m = WikidataMapper.Map(Entity("{\"id\":\"Q5\"}"), new Dictionary<string, WdRef>());
        Assert.Equal("wikidata:Q5", m.ExternalId);
        Assert.Equal(string.Empty, m.Title);
        Assert.Null(m.Year);
    }

    [Fact]
    public void A_cast_member_carries_the_character_played()
    {
        var e = Make("\"mul\":{\"language\":\"mul\",\"value\":\"x\"}", Claim("P161", Item("Q10"), qualifiers:
            "{\"P4633\":[{\"snaktype\":\"value\",\"property\":\"P4633\",\"datavalue\":" + Str("Neo") + "}]}"));
        var cast = Assert.Single(WikidataMapper.Map(e, new Dictionary<string, WdRef> { ["Q10"] = new("Q10", "Keanu Reeves", null) }).Cast);

        Assert.Equal(("Keanu Reeves", "Neo", "wikidata:Q10"), (cast.Name, cast.Role, cast.ExternalPersonId));
    }

    [Fact]
    public void Every_referenced_item_is_listed_for_resolution_and_companies_are_marked()
    {
        var ids = WikidataMapper.ReferencedIds(Fixture.Helldivers());
        Assert.True(ids["Q16833903"]);          // developer: a company, a logo is wanted
        Assert.True(ids["Q18594"]);             // publisher
        Assert.False(ids["Q380266"]);           // a genre
        Assert.False(ids["Q7889"]);             // the class
    }

    // ── publishing ids ────────────────────────────────────────────────────────

    [Fact]
    public void An_id_held_twice_for_one_kind_is_withheld_not_guessed()
    {
        var e = Entity("{\"id\":\"Q5\",\"claims\":{\"P345\":[" +
            "{\"mainsnak\":{\"snaktype\":\"value\",\"datatype\":\"external-id\",\"datavalue\":" + Str("tt0000001") + "},\"rank\":\"normal\"}," +
            "{\"mainsnak\":{\"snaktype\":\"value\",\"datatype\":\"external-id\",\"datavalue\":" + Str("tt0000002") + "},\"rank\":\"normal\"}]}}");
        Assert.False(WikidataMapper.PublishedIds(e).ContainsKey("imdb"));
    }

    [Fact]
    public void A_movie_id_and_a_tv_id_on_one_item_leave_tmdb_unpublished()
    {
        var both = Make("", Claim("P4947", Str("603"), "external-id") + "," + Claim("P4983", Str("1399"), "external-id"));
        Assert.False(WikidataMapper.PublishedIds(both).ContainsKey("tmdb"));

        Assert.Equal(603L, WikidataMapper.PublishedIds(Make("", Claim("P4947", Str("603"), "external-id")))["tmdb"].GetValue<long>());
        Assert.Equal(1399L, WikidataMapper.PublishedIds(Make("", Claim("P4983", Str("1399"), "external-id")))["tmdb"].GetValue<long>());
    }

    [Fact]
    public void A_person_or_company_imdb_id_is_not_published_as_a_title()
    {
        Assert.False(WikidataMapper.PublishedIds(Make("", Claim("P345", Str("nm0000206"), "external-id"))).ContainsKey("imdb"));
    }

    [Fact]
    public void Musicbrainz_ids_are_published_typed_the_way_musicbrainz_writes_them()
    {
        const string u = "a74b1b7f-71a5-4011-9441-d0b5e4122711";
        Assert.Equal("artist:" + u, WikidataMapper.PublishedIds(Make("", Claim("P434", Str(u), "external-id")))["musicbrainz"].GetValue<string>());
        Assert.Equal("release-group:" + u, WikidataMapper.PublishedIds(Make("", Claim("P436", Str(u), "external-id")))["musicbrainz"].GetValue<string>());
        Assert.Equal("recording:" + u, WikidataMapper.PublishedIds(Make("", Claim("P4404", Str(u), "external-id")))["musicbrainz"].GetValue<string>());
    }

    // ── images ────────────────────────────────────────────────────────────────

    [Fact]
    public void An_image_address_is_built_only_from_a_file_name_wikidata_returned_and_is_escaped()
    {
        var e = Make("\"mul\":{\"language\":\"mul\",\"value\":\"x\"}",
            Claim("P18", Str("A file (1) & more.jpg"), "commonsMedia") + "," + Claim("P154", Str("Logo x.svg"), "commonsMedia"));
        var m = WikidataMapper.Map(e, new Dictionary<string, WdRef>());

        Assert.Contains(m.AdditionalImages, i => i.Type == "Image"
            && i.Url == "https://commons.wikimedia.org/wiki/Special:FilePath/A_file_%281%29_%26_more.jpg?width=1200");
        Assert.Equal("https://commons.wikimedia.org/wiki/Special:FilePath/Logo_x.svg?width=600", m.LogoUrl);
        Assert.Contains(m.AdditionalImages, i => i.Type == "Logo" && i.ThumbnailUrl!.EndsWith("width=300"));
    }

    [Fact]
    public void Only_a_film_poster_is_a_poster_a_general_image_is_offered_but_never_promoted()
    {
        var both = Make("", Claim("P18", Str("Photo.jpg"), "commonsMedia") + "," + Claim("P3383", Str("Poster.jpg"), "commonsMedia"));
        var m = WikidataMapper.Map(both, new Dictionary<string, WdRef>());
        Assert.Contains("Poster.jpg", m.PosterUrl);
        Assert.Contains(m.AdditionalImages, i => i.Type == "Image" && i.Url.Contains("Photo.jpg"));

        // The Matrix's P18 is a screenshot of its screensaver (seen in the isolated host): with no P3383 there is no poster.
        var imageOnly = WikidataMapper.Map(Make("", Claim("P18", Str("The.Matrix.glmatrix.1.png"), "commonsMedia")), new Dictionary<string, WdRef>());
        Assert.Null(imageOnly.PosterUrl);
        Assert.Contains(imageOnly.AdditionalImages, i => i.Type == "Image");

        Assert.Null(WikidataMapper.Map(Fixture.Helldivers(), Refs()).PosterUrl);   // Helldivers 2 has no image on Wikidata (measured)
        Assert.Null(WikidataMapper.Map(Fixture.Helldivers(), Refs()).LogoUrl);
    }
}
