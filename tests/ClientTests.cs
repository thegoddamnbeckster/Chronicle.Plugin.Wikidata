using System.Net;
using Xunit;

namespace Chronicle.Plugin.Wikidata.Tests;

public class ClientTests
{
    private const string NoSuchEntity =
        "{\"error\":{\"code\":\"no-such-entity\",\"info\":\"Could not find an entity with the ID \\\"Q9\\\".\"}}";

    [Fact]
    public async Task A_statement_lookup_returns_the_item_and_an_empty_list_when_wikidata_has_none()
    {
        var h = TestClient.HelldiversFixtures();
        var c = TestClient.Make(h);

        Assert.Equal(["Q118645527"], await c.FindByStatementAsync("P1733", "553850", default));
        Assert.Empty(await c.FindByStatementAsync("P1733", "999999999", default));
        // Unquoted: a quoted value matched nothing against the live service (the unit test once asserted the quotes, and was wrong).
        Assert.Contains("haswbstatement%3AP1733%3D553850", h.Requests[0].Query);
        Assert.DoesNotContain("%22", h.Requests[0].Query);
    }

    [Theory]
    [InlineData("P345", "tt1\" OR haswbstatement:P31=Q5")]
    [InlineData("P345", "tt1 P31=Q5")]
    [InlineData("P345", "a:b")]
    [InlineData("P345", "")]
    [InlineData("P345", "x*")]
    [InlineData("P31=Q5 OR P345", "tt1")]
    [InlineData("345", "tt1")]
    public async Task A_statement_lookup_that_is_not_a_plain_id_is_refused_before_any_request(string property, string value)
    {
        var h = new StubHandler(_ => StubHandler.Json("{\"query\":{\"search\":[]}}"));
        await Assert.ThrowsAsync<ArgumentException>(() => TestClient.Make(h).FindByStatementAsync(property, value, default));
        Assert.Equal(0, h.CallCount);
    }

    [Theory]
    [InlineData("P1733", "553850")]
    [InlineData("P5794", "helldivers-2")]
    [InlineData("P345", "tt0133093")]
    [InlineData("P434", "a74b1b7f-71a5-4011-9441-d0b5e4122711")]
    [InlineData("P648", "OL45883W")]
    public async Task The_ids_the_plugin_looks_up_pass_unchanged(string property, string value)
    {
        var h = new StubHandler(_ => StubHandler.Json("{\"query\":{\"search\":[]}}"));
        await TestClient.Make(h).FindByStatementAsync(property, value, default);
        Assert.Equal($"haswbstatement:{property}={value}", StubHandler.Param(new HttpRequestMessage(HttpMethod.Get, h.Requests[0]), "srsearch"));
    }

    [Fact]
    public async Task Every_request_names_chronicle_and_the_plugin_version()
    {
        var h = new StubHandler(_ => StubHandler.Json("{\"query\":{\"search\":[]}}"));
        using var c = new WikidataClient(new HttpClient(h) { DefaultRequestHeaders = { { "User-Agent", WdSettings.UserAgent(null) } } }, TimeSpan.Zero);
        await c.FindByStatementAsync("P345", "tt0133093", default);

        var ua = h.UserAgents.Single();
        Assert.StartsWith("Chronicle-Wikidata-plugin/", ua);
        Assert.Contains(WdSettings.ProjectUrl, ua);
        Assert.DoesNotContain("@", ua);   // K8: nothing personal unless the owner typed it
    }

    [Fact]
    public async Task A_missing_item_is_absent_not_an_error_and_a_bad_id_does_not_hide_the_good_ones()
    {
        // Wikidata refuses a whole batch when one id is malformed; asking one at a time finds out which.
        var h = new StubHandler(r =>
        {
            var ids = StubHandler.Param(r, "ids");
            return ids.Contains("notanid") && ids.Contains('|') ? StubHandler.Json(NoSuchEntity)
                 : ids == "notanid" ? StubHandler.Json(NoSuchEntity)
                 : StubHandler.Json(Fixture.Entities((ids, Fixture.LabelOnly(ids, "ok"))));
        });
        var got = await TestClient.Make(h).GetEntitiesAsync(["Q1", "notanid", "Q3"], "labels", null, default);

        Assert.Equal(["Q1", "Q3"], got.Keys.OrderBy(k => k).ToArray());
    }

    [Fact]
    public async Task An_item_marked_missing_is_skipped()
    {
        var h = new StubHandler(_ => StubHandler.Json("{\"entities\":{\"Q999\":{\"id\":\"Q999\",\"missing\":\"\"}}}"));
        Assert.Empty(await TestClient.Make(h).GetEntitiesAsync(["Q999"], "labels", null, default));
    }

    [Fact]
    public async Task A_merged_item_is_found_under_both_ids()
    {
        var h = new StubHandler(_ => StubHandler.Json(Fixture.Entities(("Q2", Fixture.LabelOnly("Q2", "target")))));
        var got = await TestClient.Make(h).GetEntitiesAsync(["Q2"], "labels", null, default);
        Assert.True(got.ContainsKey("Q2"));
    }

    [Fact]
    public async Task More_than_fifty_ids_are_asked_for_fifty_at_a_time_and_none_is_dropped()
    {
        var h = new StubHandler(r =>
        {
            var ids = StubHandler.Param(r, "ids").Split('|');
            return StubHandler.Json(Fixture.Entities(ids.Select(i => (i, Fixture.LabelOnly(i, "n"))).ToArray()));
        });
        var all = Enumerable.Range(1, 120).Select(i => "Q" + i).ToList();
        var got = await TestClient.Make(h).GetEntitiesAsync(all, "labels", null, default);

        Assert.Equal(120, got.Count);
        Assert.Equal(3, h.CallCount);                                  // 50 + 50 + 20
        Assert.All(h.Requests, u => Assert.True(StubHandler.Param(new HttpRequestMessage(HttpMethod.Get, u), "ids").Split('|').Length <= 50));
    }

    [Fact]
    public async Task A_duplicate_id_is_asked_for_once()
    {
        var h = new StubHandler(r => StubHandler.Json(Fixture.Entities((StubHandler.Param(r, "ids"), Fixture.LabelOnly("Q1", "n")))));
        await TestClient.Make(h).GetEntitiesAsync(["Q1", "q1", "Q1"], "labels", null, default);
        Assert.Equal("Q1", StubHandler.Param(new HttpRequestMessage(HttpMethod.Get, h.Requests.Single()), "ids"));
    }

    [Fact]
    public async Task Maxlag_is_waited_out_a_bounded_number_of_times_then_thrown_as_a_pause()
    {
        var h = new StubHandler(_ => StubHandler.Json("{\"error\":{\"code\":\"maxlag\",\"info\":\"lagged\"}}"));
        // The wait goes through the shared pacing, which a test client has to itself and with a zero interval; the retries use short waits.
        var c = TestClient.Make(h);
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => c.FindByStatementAsync("P1", "1", default));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(4, h.CallCount);                                   // the first try and three retries
    }

    [Fact]
    public async Task An_unrecognised_error_is_a_refusal_never_an_empty_result()
    {
        var h = new StubHandler(_ => StubHandler.Json("{\"error\":{\"code\":\"badvalue\",\"info\":\"nope\"}}"));
        var ex = await Assert.ThrowsAsync<WdRefusedException>(() => TestClient.Make(h).FindByStatementAsync("P1", "1", default));
        Assert.Null(ex.StatusCode);
        Assert.Contains("badvalue", ex.Message);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("\"text\"")]
    public async Task An_answer_that_is_not_the_apis_shape_is_a_refusal(string body)
    {
        var h = new StubHandler(_ => StubHandler.Json(body));
        await Assert.ThrowsAsync<WdRefusedException>(() => TestClient.Make(h).FindByStatementAsync("P1", "1", default));
    }

    [Fact]
    public async Task A_rate_limit_is_retried_a_bounded_number_of_times_then_thrown_with_its_status()
    {
        var h = new StubHandler(_ => StubHandler.Json("{}", HttpStatusCode.TooManyRequests));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => TestClient.Make(h).FindByStatementAsync("P1", "1", default));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(4, h.CallCount);
    }

    [Fact]
    public async Task A_server_error_is_retried_then_thrown_never_recorded_as_nothing()
    {
        var calls = 0;
        var h = new StubHandler(_ => ++calls < 3
            ? StubHandler.Json("{}", HttpStatusCode.ServiceUnavailable)
            : StubHandler.Json("{\"query\":{\"search\":[]}}"));
        Assert.Empty(await TestClient.Make(h).FindByStatementAsync("P1", "1", default));
        Assert.Equal(3, calls);                                          // two failures, then the answer

        var down = new StubHandler(_ => StubHandler.Json("{}", HttpStatusCode.BadGateway));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => TestClient.Make(down).FindByStatementAsync("P1", "1", default));
        Assert.Equal(HttpStatusCode.BadGateway, ex.StatusCode);
    }

    [Fact]
    public async Task An_html_answer_is_a_block_it_stops_everyone_and_is_never_worked_around()
    {
        // E9. A test client has its own pacing state, so a block holds that client's callers; the production client shares one.
        var calls = 0;
        var h = new StubHandler(_ => { calls++; return StubHandler.Html(); });
        var c = TestClient.Make(h, blocked: TimeSpan.FromMinutes(30));

        var first = await Assert.ThrowsAsync<HttpRequestException>(() => c.FindByStatementAsync("P1", "1", default));
        Assert.Equal(HttpStatusCode.TooManyRequests, first.StatusCode);
        Assert.Contains("web page instead of data", first.Message);
        Assert.Equal(1, calls);                                         // no retry of a block

        var second = await Assert.ThrowsAsync<HttpRequestException>(() => c.GetEntitiesAsync(["Q1"], "labels", null, default));
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Contains("paused until", second.Message);
        Assert.Equal(1, calls);                                         // held: the second call made no request at all
    }

    [Fact]
    public async Task A_block_page_says_how_long_the_plugin_will_hold_so_the_host_can_send_the_item_to_the_back_of_the_queue()
    {
        var h = new StubHandler(_ => StubHandler.Html());
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => TestClient.Make(h, blocked: TimeSpan.FromMinutes(30)).FindByStatementAsync("P1", "1", default));
        Assert.Equal(TimeSpan.FromMinutes(30), ex.Data["RetryAfter"]);
    }

    [Fact]
    public async Task While_the_hold_lasts_each_call_reports_what_is_left_of_it()
    {
        var h = new StubHandler(_ => StubHandler.Html());
        var c = TestClient.Make(h, blocked: TimeSpan.FromMinutes(30));
        await Assert.ThrowsAsync<HttpRequestException>(() => c.FindByStatementAsync("P1", "1", default));
        var held = await Assert.ThrowsAsync<HttpRequestException>(() => c.FindByStatementAsync("P1", "2", default));

        var left = Assert.IsType<TimeSpan>(held.Data["RetryAfter"]);
        Assert.InRange(left, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(30));
        Assert.Equal(1, h.CallCount);                                   // and no request was made while held
    }

    [Fact]
    public void A_retry_after_from_the_service_is_reported_in_full_not_cut_to_what_this_client_sleeps()
    {
        var r = StubHandler.Json("{}", HttpStatusCode.TooManyRequests);
        r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(45));

        Assert.Equal(TimeSpan.FromMinutes(45), WikidataClient.AskedFor(r, TimeSpan.FromSeconds(2)));
        Assert.Equal(TimeSpan.FromSeconds(2), WikidataClient.AskedFor(StubHandler.Json("{}", HttpStatusCode.TooManyRequests), TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task An_html_body_with_a_json_content_type_is_still_a_block()
    {
        var h = new StubHandler(_ => StubHandler.Json("<!DOCTYPE html><html></html>"));
        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => TestClient.Make(h).FindByStatementAsync("P1", "1", default));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
    }

    [Fact]
    public async Task Requests_are_made_one_at_a_time_and_apart()
    {
        var interval = TimeSpan.FromMilliseconds(250);
        var h = new StubHandler(_ => StubHandler.Json("{\"query\":{\"search\":[]}}"));
        using var c = new WikidataClient(new HttpClient(h), interval);

        await Task.WhenAll(c.FindByStatementAsync("P1", "1", default), c.FindByStatementAsync("P1", "2", default), c.FindByStatementAsync("P1", "3", default));

        Assert.Equal(3, h.CallCount);
        for (var i = 1; i < h.At.Count; i++)
            Assert.True(h.At[i] - h.At[i - 1] >= interval - TimeSpan.FromMilliseconds(40), $"gap {i}: {(h.At[i] - h.At[i - 1]).TotalMilliseconds} ms");
    }

    [Fact]
    public async Task No_maxlag_is_ever_sent()
    {
        var h = TestClient.HelldiversFixtures();
        var c = TestClient.Make(h);
        await c.FindByStatementAsync("P1733", "553850", default);
        await c.GetEntitiesAsync(["Q118645527"], "labels", null, default);
        await c.SearchAsync("Helldivers 2", 7, default);
        Assert.All(h.Requests, u => Assert.DoesNotContain("maxlag", u.Query));
    }

    [Fact]
    public async Task A_search_returns_ranked_hits_with_their_descriptions()
    {
        var hits = await TestClient.Make(TestClient.HelldiversFixtures()).SearchAsync("Helldivers 2", 7, default);
        Assert.Equal("Q118645527", hits[0].Id);
        Assert.Equal("Helldivers 2", hits[0].Label);
        Assert.Contains("video game", hits[0].Description);
    }

    [Fact]
    public void The_interval_never_goes_below_one_second()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), WdSettings.From(new Dictionary<string, string> { [WdSettings.KeyInterval] = "0" }).Interval);
        Assert.Equal(TimeSpan.FromSeconds(1), WdSettings.From(new Dictionary<string, string> { [WdSettings.KeyInterval] = "300" }).Interval);
        Assert.Equal(TimeSpan.FromSeconds(5), WdSettings.From(new Dictionary<string, string> { [WdSettings.KeyInterval] = "5000" }).Interval);
        Assert.Equal(TimeSpan.FromSeconds(1.2), WdSettings.From(null).Interval);
    }
}
