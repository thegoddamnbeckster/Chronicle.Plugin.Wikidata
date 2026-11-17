using System.Net;
using System.Text.Json;
using Chronicle.Plugin.Wikidata;

namespace Chronicle.Plugin.Wikidata.Tests;

/// <summary>Scripted HTTP handler that records every request and answers via a callback.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];
    public List<DateTime> At { get; } = [];
    public List<string> UserAgents { get; } = [];
    public int CallCount => Requests.Count;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request.RequestUri!);
        At.Add(DateTime.UtcNow);
        UserAgents.Add(request.Headers.UserAgent.ToString());
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Html(string body = "<html><body>Checking your browser</body></html>", HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html") };

    public static string Param(HttpRequestMessage r, string name) =>
        System.Web.HttpUtility.ParseQueryString(r.RequestUri!.Query)[name] ?? "";
}

internal static class Fixture
{
    public static string Text(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name + ".json"));
    public static JsonElement Json(string name) => JsonDocument.Parse(Text(name)).RootElement.Clone();

    /// <summary>Michael J. Fox's entity exactly as Wikidata returned it.</summary>
    public static JsonElement Fox() => Json("p9_fox").GetProperty("entities").GetProperty("Q395274");

    /// <summary>The Helldivers 2 entity exactly as Wikidata returned it.</summary>
    public static JsonElement Helldivers() => Json("p2_helldivers").GetProperty("entities").GetProperty("Q118645527");

    /// <summary>A response holding the given entities, as <c>wbgetentities</c> returns them.</summary>
    public static string Entities(params (string Id, string EntityJson)[] items) =>
        "{\"entities\":{" + string.Join(",", items.Select(i => $"\"{i.Id}\":{i.EntityJson}")) + "},\"success\":1}";

    public static string LabelOnly(string id, string label, string lang = "mul") =>
        $"{{\"type\":\"item\",\"id\":\"{id}\",\"labels\":{{\"{lang}\":{{\"language\":\"{lang}\",\"value\":\"{label}\"}}}}}}";
}

internal static class TestClient
{
    public static WikidataClient Make(StubHandler handler, TimeSpan? blocked = null) =>
        new(new HttpClient(handler), TimeSpan.Zero, blocked);

    public static WikidataMetadataProvider Provider(StubHandler handler) => new(Make(handler));

    /// <summary>A handler that answers the endpoints a Helldivers 2 fetch uses, from the recorded responses.</summary>
    public static StubHandler HelldiversFixtures() => new(req =>
    {
        var action = StubHandler.Param(req, "action");
        if (action == "query")
        {
            var q = StubHandler.Param(req, "srsearch");
            return q.Contains("P4985") || q.Contains("nm0000150") ? StubHandler.Json(Fixture.Text("p9_fox_search"))
                 : q.Contains("P1733") && q.Contains("553850") ? StubHandler.Json(Fixture.Text("p1_steam"))
                 : q.Contains("P4947") ? StubHandler.Json(Fixture.Text("p1_tmdb"))
                 : q.Contains("P345") ? StubHandler.Json(Fixture.Text("p1_imdb"))
                 : q.Contains("P434") ? StubHandler.Json(Fixture.Text("p1_mbid"))
                 : StubHandler.Json(Fixture.Text("p1_none"));
        }
        if (action == "wbsearchentities")
            return StubHandler.Json(Fixture.Text(StubHandler.Param(req, "search").Contains("Dune") ? "p6_search_dune" : "p6_search"));
        if (action == "wbgetentities")
        {
            var ids = StubHandler.Param(req, "ids").Split('|');
            var props = StubHandler.Param(req, "props");
            if (ids.Length == 1 && ids[0] == "Q118645527") return StubHandler.Json(Fixture.Text("p2_helldivers"));
            if (ids.Length == 1 && ids[0] == "Q395274") return StubHandler.Json(Fixture.Text("p9_fox"));
            var items = ids.Select(id => id switch
            {
                "Q380266" => (id, Fixture.LabelOnly(id, "third-person shooter")),
                "Q129695809" => (id, Fixture.LabelOnly(id, "cooperative shooter")),
                "Q7889" => (id, Fixture.LabelOnly(id, "video game")),
                "Q18594" => (id, Fixture.LabelOnly(id, "Sony Interactive Entertainment")),
                "Q16833903" => (id, Fixture.LabelOnly(id, "Arrowhead Game Studios")),
                _ => (id, Fixture.LabelOnly(id, "x " + id)),
            }).ToArray();
            return StubHandler.Json(Fixture.Entities(items));
        }
        return StubHandler.Json("{}", HttpStatusCode.NotFound);
    });
}
