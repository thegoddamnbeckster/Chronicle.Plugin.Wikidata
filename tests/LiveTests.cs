using Chronicle.Plugins.Models;
using Xunit;
using Xunit.Abstractions;

namespace Chronicle.Plugin.Wikidata.Tests;

/// <summary>
/// Real requests to wikidata.org, a handful and paced by the plugin itself (1.2 s apart). They run only when WIKIDATA_LIVE=1 is set,
/// so the ordinary test run never touches the service. Used to look at real answers before release (checklist G3).
/// </summary>
public class LiveTests(ITestOutputHelper output)
{
    private static bool Live => Environment.GetEnvironmentVariable("WIKIDATA_LIVE") == "1";

    private static MediaSearchContext Ctx(string type, int level, params (string, string)[] known) =>
        new(Name: "x", HierarchyLevel: level, MediaTypeName: type, IsRealHierarchyPosition: true,
            KnownExternalIds: known.ToDictionary(k => k.Item1, k => k.Item2));

    [Fact]
    public async Task Real_items_are_found_by_the_ids_chronicle_holds_and_fetched_in_full()
    {
        if (!Live) return;
        using var p = new WikidataMetadataProvider();
        p.Configure(new Dictionary<string, string>());

        var cases = new (string Label, MediaSearchContext Ctx)[]
        {
            ("game by Steam id", Ctx("game", 0, ("steam", "steam:553850"))),
            ("movie by TMDB id", Ctx("movies", 0, ("tmdb", "movie:603"))),
            ("artist by MusicBrainz id", Ctx("music", 0, ("musicbrainz", "artist:a74b1b7f-71a5-4011-9441-d0b5e4122711"))),
            ("person by IMDb id", Ctx("people", 0, ("imdb", "imdb:nm0000150"))),
        };
        foreach (var (label, ctx) in cases)
        {
            var started = DateTime.UtcNow;
            var r = await p.SearchAsync(ctx);
            var m = Assert.Single(r).Metadata;
            var ids = m.ExtendedData!.Value.TryGetProperty("ids", out var i)
                ? string.Join(", ", i.EnumerateObject().Select(x => $"{x.Name}={x.Value.GetRawText()}")) : "none";
            output.WriteLine($"{label}: {m.ExternalId} \"{m.Title}\" year={m.Year} genres={m.Genres.Count} cast={m.Cast.Count} crew={m.Crew.Count} " +
                             $"names={m.AlternateNames.Count} poster={(m.PosterUrl is null ? "-" : "yes")} logo={(m.LogoUrl is null ? "-" : "yes")} " +
                             $"raw={m.ExtendedData.Value.GetProperty("raw").GetRawText().Length / 1024} KB in {(DateTime.UtcNow - started).TotalSeconds:F1} s");
            output.WriteLine($"   ids: {ids}");
            output.WriteLine($"   cast: {m.Cast.Count} ({m.Cast.Count(c => c.ProfileImageUrl is not null)} with a picture), crew: {m.Crew.Count} ({m.Crew.Count(c => c.ProfileImageUrl is not null)} with a picture)");
            output.WriteLine($"   cast: {string.Join("; ", m.Cast.Take(4).Select(c => $"{c.Name} as {c.Role}"))}");
            output.WriteLine($"   crew: {string.Join("; ", m.Crew.Take(6).Select(c => $"{c.Name} ({c.Job})"))}");
        }
    }
}
