using System.Text.Json;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Xunit;

namespace Chronicle.Plugin.Wikidata.Tests;

/// <summary>Declared-equals-filled, one source of version, the manifest, and the settings-schema checks (checklist P2, K1, K4, X10).</summary>
public class ContractTests
{
    /// <summary>The canonical field names the host resolves (<c>MetadataResolutionService.FieldMap</c>); a misspelt name makes a field invisible.</summary>
    private static readonly HashSet<string> HostFields =
    [
        "title", "overview", "year", "poster_url", "backdrop_url", "logo_url", "banner_url", "thumb_url", "clearart_url",
        "disc_url", "character_art_url", "runtime_minutes", "rating", "genres", "cast", "crew", "tags", "collection", "composer",
        "label", "bpm", "mood", "language", "isrc", "birth_date", "death_date",
    ];

    private static readonly WikidataMetadataProvider P = new();

    [Fact]
    public void Every_declared_field_is_a_host_field_name() =>
        Assert.All(P.GetSupportedMediaTypes().SelectMany(t => t.SupportedFields), f => Assert.Contains(f, HostFields));

    [Fact]
    public void It_never_owns_a_type_and_ranks_after_wikipedia()
    {
        var types = P.GetSupportedMediaTypes();
        Assert.Equal(["movies", "tv", "music", "book", "game", "people"], types.Select(t => t.MediaTypeName).ToArray());
        // T1: no display name, hierarchy or verb, so the type's owner (TMDB, MusicBrainz, IGDB ...) keeps its own declaration.
        Assert.All(types, t =>
        {
            Assert.True(string.IsNullOrEmpty(t.DisplayName));
            Assert.Equal(95, t.DefaultPriority);
            Assert.True(t.DefaultPriority > 90, "Wikipedia is 90; Wikidata fills gaps after it");
        });
    }

    [Fact]
    public async Task Declared_equals_filled_for_a_well_described_item()
    {
        // A field the plugin declares must be one it fills for a real record (a field declared and never filled is a queue that can
        // only fail). Helldivers 2 has no image on Wikidata, so the image fields are covered by the mapper tests instead.
        var m = WikidataMapper.Map(Fixture.Helldivers(), new Dictionary<string, WdRef>
        {
            ["Q380266"] = new("Q380266", "g", null), ["Q7889"] = new("Q7889", "c", null),
            ["Q18594"] = new("Q18594", "p", null), ["Q16833903"] = new("Q16833903", "d", null),
        });
        var filled = new HashSet<string>();
        if (m.Title.Length > 0) filled.Add("title");
        if (m.Year is not null) filled.Add("year");
        if (m.Genres.Count > 0) filled.Add("genres");
        if (m.Tags.Count > 0) filled.Add("tags");
        if (m.Cast.Count > 0) filled.Add("cast");
        if (m.Crew.Count > 0) filled.Add("crew");

        var declared = P.GetSupportedMediaTypes()[0].SupportedFields.ToHashSet();
        Assert.All(filled, f => Assert.Contains(f, declared));                       // everything filled is declared
        var imageFields = new[] { "poster_url", "logo_url" };
        Assert.Empty(declared.Except(filled).Except(imageFields));                   // everything declared but images is filled
        await Task.CompletedTask;
    }

    [Fact]
    public void The_cross_reference_prefix_is_this_plugins_own_and_only_that()
        => Assert.Equal(["wikidata:"], P.GetAcceptedCrossRefPrefixes().ToArray());

    [Fact]
    public void Version_comes_from_the_assembly_and_equals_the_manifest()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.json")));
        Assert.Equal(doc.RootElement.GetProperty("version").GetString(), P.Version);
        Assert.Equal(P.Version, WdSettings.Version);
    }

    [Fact]
    public void The_manifest_is_complete_and_its_entry_type_is_this_provider()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.json")));
        var m = doc.RootElement;
        Assert.Equal("chronicle.plugin.wikidata", m.GetProperty("plugin_id").GetString());
        Assert.Equal(P.PluginId, m.GetProperty("plugin_id").GetString());
        Assert.Equal(typeof(WikidataMetadataProvider).FullName, m.GetProperty("entry_type").GetString());
        foreach (var key in new[] { "name", "version", "author", "description", "min_chronicle_version", "iconUrl", "brandColorLight", "brandColorDark", "fixMatchHint" })
            Assert.False(string.IsNullOrWhiteSpace(m.GetProperty(key).GetString()), key);
        Assert.True(m.GetProperty("call_timeout_seconds").GetInt32() > 25);              // K13: long fetches finish
        Assert.True(WikidataMetadataProvider.FetchDeadline < TimeSpan.FromSeconds(m.GetProperty("call_timeout_seconds").GetInt32()));
        Assert.True(WikidataMetadataProvider.InteractiveDeadline < TimeSpan.FromSeconds(2.5), "inside the host's interactive search deadline");
        Assert.StartsWith("https://", m.GetProperty("iconUrl").GetString());

        var tasks = m.GetProperty("background_tasks").EnumerateArray().Select(t => t.GetProperty("task_id").GetString()).ToArray();
        Assert.Equal(["fetch-missing-metadata", "resync-all-metadata"], tasks);
    }

    [Fact]
    public void The_source_url_pattern_links_an_item_to_its_page()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "manifest.json")));
        var s = doc.RootElement.GetProperty("sourceUrls")[0];
        var m = System.Text.RegularExpressions.Regex.Match("wikidata:Q118645527", s.GetProperty("match").GetString()!);
        Assert.True(m.Success);
        Assert.Equal("https://www.wikidata.org/wiki/Q118645527", s.GetProperty("url").GetString()!.Replace("$1", m.Groups[1].Value));
    }

    // ── settings schema (X1 to X10) ───────────────────────────────────────────

    [Fact]
    public void Every_setting_has_a_key_a_label_help_and_a_default_where_optional()
    {
        var s = P.GetSettingsSchema().Settings;
        Assert.All(s, d =>
        {
            Assert.False(string.IsNullOrWhiteSpace(d.Key));
            Assert.False(string.IsNullOrWhiteSpace(d.Label));
            Assert.False(string.IsNullOrWhiteSpace(d.Description));
            if (!d.Required && d.Type == SettingType.Number) Assert.False(string.IsNullOrEmpty(d.DefaultValue));
        });
        Assert.Contains(s, d => d.Type == SettingType.Notice);
        Assert.DoesNotContain(s, d => d.Type == SettingType.Password);     // there is no secret to hold
        Assert.Equal(s.Count, s.Select(d => d.Key).Distinct().Count());
    }

    [Fact]
    public void The_default_interval_in_the_schema_parses_and_is_the_plugins_default()
    {
        var d = P.GetSettingsSchema().Settings.Single(x => x.Key == WdSettings.KeyInterval);
        Assert.Equal(WikidataClient.DefaultInterval, WdSettings.From(new Dictionary<string, string> { [WdSettings.KeyInterval] = d.DefaultValue! }).Interval);
    }

    [Fact]
    public void Configure_with_nothing_or_garbage_works_and_is_idempotent()
    {
        var p = new WikidataMetadataProvider();
        p.Configure(new Dictionary<string, string>());
        p.Configure(new Dictionary<string, string> { [WdSettings.KeyInterval] = "soon", [WdSettings.KeyContact] = "(bad)" });
        p.Configure(new Dictionary<string, string> { [WdSettings.KeyContact] = "me@example.com" });
        Assert.Contains("me@example.com", WdSettings.UserAgent("me@example.com"));
        Assert.Equal("me@example.com", WdSettings.From(new Dictionary<string, string> { [WdSettings.KeyContact] = " me@example.com " }).Contact);
        Assert.Null(WdSettings.From(new Dictionary<string, string> { [WdSettings.KeyContact] = "a\nb" }).Contact);
        Assert.Null(WdSettings.From(new Dictionary<string, string> { [WdSettings.KeyContact] = new string('x', 201) }).Contact);
        Assert.NotNull(WdSettings.From(new Dictionary<string, string> { [WdSettings.KeyInterval] = "soon" }).Problem);
    }

    [Fact]
    public void The_user_agent_never_carries_a_contact_the_owner_did_not_type()
    {
        var ua = WdSettings.UserAgent(null);
        Assert.Equal($"Chronicle-Wikidata-plugin/{WdSettings.Version} ({WdSettings.ProjectUrl})", ua);
    }
}
