using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chronicle.Plugins;
using Chronicle.Plugins.Models;
using Serilog;

namespace Chronicle.Plugin.Wikidata;

/// <summary>
/// Chronicle metadata provider for Wikidata.
///
/// Two jobs: publish the ids an item has at other services (so the host can seed the other plugins without a text search), and keep
/// the structured facts. It stands alone: no key, no account, no other plugin. Automatic matching is by id or absent; the
/// interactive search (Add Media, Fix Match) looks by name and keeps only results whose one-line description names the right kind of thing.
/// Design: docs/plans/2026-11-16-wikidata-plugin-design.md in the Chronicle repository.
/// </summary>
public sealed class WikidataMetadataProvider : IMetadataProvider, IDisposable
{
    public string PluginId => WikidataClient.PluginId;
    public string Name     => "Wikidata";
    public string Version  => WdSettings.Version;
    public string Author   => "Chronicle Contributors";

    /// <summary>The types this plugin adds facts to. It never owns one: no display name, so the type's owner (TMDB, MusicBrainz, IGDB ...) keeps its declaration (T1).</summary>
    private static readonly string[] Types = ["movies", "tv", "music", "book", "game", "people"];

    // Deadlines (E4): below the host's 25 s guard for a search; a full fetch of a large item makes several paced requests, so the
    // manifest declares call_timeout_seconds and this stays under it. The interactive path keeps inside the host's 2.5 s.
    internal static readonly TimeSpan InteractiveDeadline = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan SearchDeadline      = TimeSpan.FromSeconds(3300);
    internal static readonly TimeSpan FetchDeadline       = TimeSpan.FromSeconds(3300);

    private WikidataClient? _client;
    private WdSettings _settings = WdSettings.From(null);

    // The follow-up GetByIdAsync after a search asks for the same item; the search already holds it.
    private sealed record CacheEntry(string Id, MediaMetadata Result, long ExpiresAtTicks);
    private volatile CacheEntry? _cache;
    private static readonly long CacheTtlTicks = 30L * TimeSpan.TicksPerSecond;

    // One item that always fails must not block the queue behind it (E7). Per id, bounded.
    private readonly Dictionary<string, int> _failures = new(StringComparer.OrdinalIgnoreCase);
    private const int PoisonLimit = 3;
    private const int FailureTableLimit = 500;

    /// <summary>Chronicle instantiates plugins via <c>Activator.CreateInstance</c>: a public parameterless constructor is required.</summary>
    public WikidataMetadataProvider() => _client = new WikidataClient(WdSettings.UserAgent(null));

    /// <summary>Test-only: inject a pre-built client.</summary>
    internal WikidataMetadataProvider(WikidataClient client) => _client = client;

    // ── Declarations ──────────────────────────────────────────────────────────

    public MediaTypeSupport[] GetSupportedMediaTypes() =>
        Types.Select(t => t == "people" ? PeopleType() : new MediaTypeSupport
        {
            MediaTypeName   = t,
            // After Wikipedia (90): Wikidata fills gaps and never wins a field unless the owner ranks it higher.
            DefaultPriority = 95,
            SupportedFields = [.. WikidataMapper.Fields],
        }).ToArray();

    /// <summary>
    /// The person type, declared exactly as TMDB declares it (one level, verb "viewed", unit "percent", no display name): Chronicle merges
    /// every plugin's declaration of one type by name, so a different one here would decide the type's settings by load order (T1).
    /// A person is found only by an id (IMDb nm, TMDB person), never by name: two people share a name, an id does not.
    /// </summary>
    private static MediaTypeSupport PeopleType() => new()
    {
        MediaTypeName   = "people",
        HierarchyLevels = 1,
        InteractionVerb = "viewed",
        ProgressUnit    = "percent",
        DefaultPriority = 95,
        SupportedFields = [.. WikidataMapper.PersonFields],
    };

    /// <summary>Ids another plugin may hand this provider: this plugin's own. Other services' ids are read through <see cref="SearchAsync"/>.</summary>
    public IReadOnlyList<string> GetAcceptedCrossRefPrefixes() => [WikidataIds.Prefix];

    public PluginSettingsSchema GetSettingsSchema() => new()
    {
        Settings =
        [
            new SettingDefinition
            {
                Key         = "notice",
                Label       = "No account or key is needed",
                Description = "This plugin works on its own and has nothing you must fill in. It finds an item by an id Chronicle already has " +
                              "(IMDb, TMDB, TheTVDB, TVmaze, MusicBrainz, Steam, IGDB, Open Library) and adds the other ids and facts Wikidata holds for it.",
                Type        = SettingType.Notice,
            },
            new SettingDefinition
            {
                Key         = WdSettings.KeyContact,
                Label       = "Contact detail (optional)",
                Description = "Wikimedia asks every program that reads its data to say how to reach whoever runs it. Chronicle already identifies itself " +
                              "with its project address, so you can leave this empty. If you want Wikimedia to be able to reach YOU about heavy use, " +
                              "enter an email address or a web address here (for example name@example.com). It is sent only to Wikimedia, only in the " +
                              "request's User-Agent. Do not enter a password or key.",
                Type        = SettingType.Text,
                Required    = false,
            },
            new SettingDefinition
            {
                Key          = WdSettings.KeyInterval,
                Label        = "Milliseconds between requests (advanced)",
                Description  = "How long to wait between requests to Wikidata. Wikimedia asks for unhurried, one-at-a-time requests, so the plugin never " +
                               "goes below 1000 and a smaller number is raised to 1000. Larger is gentler and slower.",
                Type         = SettingType.Number,
                Required     = false,
                DefaultValue = "1200",
            },
        ],
    };

    public void Configure(IReadOnlyDictionary<string, string> settings)
    {
        _settings = WdSettings.From(settings);
        if (_settings.Problem is not null) Log.Warning("Wikidata: {Problem}", _settings.Problem);

        _client?.Dispose();
        _client = new WikidataClient(WdSettings.UserAgent(_settings.Contact), _settings.Interval);
        _cache  = null;
        lock (_failures) _failures.Clear();
    }

    // ── Search: by id, or (interactive only) by name ──────────────────────────

    public async Task<IReadOnlyList<ScoredCandidate>> SearchAsync(MediaSearchContext context, CancellationToken ct = default)
    {
        var type = (context.MediaTypeName ?? string.Empty).ToLowerInvariant();
        if (type.Length > 0 && !Types.Contains(type)) return [];

        var interactive = !context.IsRealHierarchyPosition;
        return await GuardedAsync(interactive ? InteractiveDeadline : SearchDeadline, interactive, [], "search",
            c => SearchCoreAsync(context, type, interactive, c), ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ScoredCandidate>> SearchCoreAsync(MediaSearchContext ctx, string type, bool interactive, CancellationToken ct)
    {
        // 1. Fix Match / Add Media: the owner typed or pasted an id or an address. It is the answer. On the automatic path the name is
        //    the item's own name, and "Q42" can be a real title, so only an explicit statement (the prefix or an address) counts there.
        if ((interactive || ctx.Name.Contains("wikidata", StringComparison.OrdinalIgnoreCase)) && WikidataIds.Parse(ctx.Name) is { } typed
            && await FetchAsync(typed, full: !interactive, ct).ConfigureAwait(false) is { Title.Length: > 0 } direct)
            return [new ScoredCandidate(direct, 100, "id given") { IdentifierMatch = true }];

        // 2. The Wikidata id Chronicle already holds.
        if (WikidataLookups.OwnId(ctx.KnownExternalIds) is { } own
            && await FetchAsync(own, full: !interactive, ct).ConfigureAwait(false) is { Title.Length: > 0 } held)
            return [new ScoredCandidate(held, 100, "wikidata id match") { IdentifierMatch = true }];

        // 3. Another service's id Chronicle already holds, read as a Wikidata statement. More than one item holding it is a Wikidata
        //    duplicate: nothing is better than the wrong one of two (I5, I6).
        foreach (var lookup in WikidataLookups.For(ctx.KnownExternalIds, type, ctx.HierarchyLevel))
        {
            var hits = await Client.FindByStatementAsync(lookup.Property, lookup.Value, ct).ConfigureAwait(false);
            if (hits.Count > 1)
            {
                Log.Warning("Wikidata: {Prop}={Value} is held by {Count} items ({Items}); no automatic match", lookup.Property, lookup.Value, hits.Count, string.Join(", ", hits));
                continue;
            }
            if (hits.Count == 1 && WikidataIds.Parse(hits[0]) is { } id
                && await FetchAsync(id, full: !interactive, ct).ConfigureAwait(false) is { Title.Length: > 0 } meta)
                return [new ScoredCandidate(meta, 100, $"{lookup.Source} id match") { IdentifierMatch = true }];
        }

        // 4. No id. Automatic matching stops here, with no request made for a name. A person is never found by name at all.
        if (!interactive || type == "people") return [];

        return await NameSearchAsync(ctx, type, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ScoredCandidate>> NameSearchAsync(MediaSearchContext ctx, string type, CancellationToken ct)
    {
        var text = ctx.PreciseName ?? ctx.Name;
        if (string.IsNullOrWhiteSpace(text)) return [];
        // A season, an episode, a chapter: not something a name finds on Wikidata. Music has real album and track levels.
        if (type != "music" && ctx.HierarchyLevel > 0) return [];

        var hits = await Client.SearchAsync(text.Trim(), 7, ct).ConfigureAwait(false);
        var wanted = Norm(text);
        var candidates = new List<ScoredCandidate>();
        foreach (var h in hits)
        {
            if (h.Label is null || !ClassFits(type, ctx.HierarchyLevel, h.Description)) continue;

            int score;
            string reason;
            var label = Norm(h.Label);
            if (label == wanted) { score = 90; reason = "label match"; }
            else if (h.Aliases.Any(a => Norm(a) == wanted)) { score = 80; reason = "alias match"; }
            else if (label.StartsWith(wanted, StringComparison.Ordinal) || wanted.StartsWith(label, StringComparison.Ordinal)) { score = 60; reason = "label starts with the query"; }
            else continue;

            var year = YearInText(h.Description);
            if (ctx.Year is { } y && year is { } dy)
            {
                if (y == dy) { score += 5; reason += ", year match"; }
                else if (Math.Abs(y - dy) > 1) { score -= 30; reason += ", year differs"; }
            }

            if (WikidataIds.Parse(h.Id) is not { } id) continue;
            // The candidate list is lean (one request, no references resolved); choosing one fetches it in full.
            var meta = new MediaMetadata
            {
                ExternalId = id.Canonical, Source = WikidataMapper.Source, Title = h.Label, Year = year, SourceUrl = WikidataMapper.Site + id.Q,
                ExtendedData = JsonSerializer.SerializeToElement(new { id = id.Q, description = h.Description }),
            };
            candidates.Add(new ScoredCandidate(meta, Math.Clamp(score, 0, 100), reason));
        }
        return candidates.OrderByDescending(c => c.Score).ToList();
    }

    // ── Get by id ─────────────────────────────────────────────────────────────

    public async Task<MediaMetadata> GetByIdAsync(string externalId, CancellationToken ct = default)
    {
        var id = WikidataIds.Parse(externalId)
                 ?? throw new ArgumentException(
                        $"Invalid Wikidata ID format: '{externalId}'. Expected wikidata:Q42, Q42 or a wikidata.org address.");

        var meta = await GuardedAsync(FetchDeadline, interactive: false, null, "fetch",
            c => FetchAsync(id, full: true, c), ct).ConfigureAwait(false);
        // An id Wikidata does not hold: the host clears a stored id on KeyNotFoundException and searches again.
        return meta ?? throw new KeyNotFoundException($"Wikidata has no item {id.Q}.");
    }

    /// <summary>The item in full (references resolved) or, for an interactive list, as the one request that holds it.</summary>
    private async Task<MediaMetadata?> FetchAsync(WdId id, bool full, CancellationToken ct)
    {
        if (full && Cached(id) is { } cached) return cached;

        try
        {
            var entities = await Client.GetEntitiesAsync([id.Q], "labels|descriptions|aliases|claims|sitelinks|info", null, ct).ConfigureAwait(false);
            if (!entities.TryGetValue(id.Q, out var entity)) { Forget(id); return null; }

            var refs = new Dictionary<string, WdRef>(StringComparer.OrdinalIgnoreCase);
            if (full)
            {
                var wanted = WikidataMapper.ReferencedIds(entity);
                // Names for all; a company's claims too, for its logo. Read page by page: no list is cut short (D6).
                foreach (var e in await Client.GetEntitiesAsync(wanted.Where(w => !w.Value).Select(w => w.Key), "labels", null, ct).ConfigureAwait(false))
                    refs[e.Key] = WikidataMapper.ToRef(e.Value);
                foreach (var e in await Client.GetEntitiesAsync(wanted.Where(w => w.Value).Select(w => w.Key), "labels|claims", null, ct).ConfigureAwait(false))
                    refs[e.Key] = WikidataMapper.ToRef(e.Value);
            }

            var meta = WikidataMapper.Map(entity, refs);
            Forget(id);
            if (full) _cache = new CacheEntry(id.Q, meta, DateTime.UtcNow.Ticks + CacheTtlTicks);
            return meta;
        }
        catch (HttpRequestException ex) when (CountsAgainstItem(ex))
        {
            if (Fail(id) >= PoisonLimit)
                throw new InvalidDataException($"Wikidata item {id.Q} could not be read after {PoisonLimit} tries: {ex.Message}", ex);
            throw;
        }
    }

    private MediaMetadata? Cached(WdId id) =>
        _cache is { } c && c.Id == id.Q && DateTime.UtcNow.Ticks < c.ExpiresAtTicks ? c.Result : null;

    /// <summary>A failure says something about THIS item only when the service answered (5xx, a refusal), not when it was busy or unreachable.</summary>
    private static bool CountsAgainstItem(HttpRequestException ex) =>
        ex is WdRefusedException || ex.StatusCode is { } s && (int)s >= 500;

    private int Fail(WdId id)
    {
        lock (_failures)
        {
            if (_failures.Count >= FailureTableLimit) _failures.Clear();   // bounded
            return _failures[id.Q] = _failures.GetValueOrDefault(id.Q) + 1;
        }
    }

    private void Forget(WdId id) { lock (_failures) _failures.Remove(id.Q); }

    // ── Images, health ────────────────────────────────────────────────────────

    /// <summary>The browser loads images itself, straight from the address in the metadata; the host never calls this (M2).</summary>
    public Task<byte[]> GetImageAsync(string url, CancellationToken ct = default) =>
        throw new NotSupportedException("The Wikidata plugin does not proxy images: they load directly from commons.wikimedia.org.");

    public async Task<bool> HealthCheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            var found = await Client.GetEntitiesAsync(["Q42"], "info", null, cts.Token).ConfigureAwait(false);
            return found.ContainsKey("Q42");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            Log.Warning("Wikidata health check failed: {Message}", ex.Message);
            return false;
        }
    }

    // ── Plumbing ──────────────────────────────────────────────────────────────

    private WikidataClient Client => _client ?? throw new InvalidOperationException("The Wikidata plugin was disposed.");

    /// <summary>
    /// Runs one call under the plugin's own deadline, which is below the host's: the host reads its own timeout as "no result", and for
    /// enrichment that is NotFound for good (E4). On the plugin's deadline the pause form is thrown instead. The interactive path never
    /// throws: the host would show nothing either way, so it returns <paramref name="whenInteractiveFails"/> and logs (E5).
    /// </summary>
    private static async Task<T> GuardedAsync<T>(TimeSpan deadline, bool interactive, T whenInteractiveFails, string what,
                                                 Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(deadline);
        try
        {
            return await work(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Log.Warning("Wikidata {What} did not finish within {Seconds:F0} s", what, deadline.TotalSeconds);
            if (interactive) return whenInteractiveFails;
            throw new HttpRequestException($"Wikidata {what} did not finish in time.", null, null);
        }
        catch (Exception ex) when (interactive && ex is not OperationCanceledException)
        {
            Log.Warning("Wikidata interactive {What} failed: {Message}", what, ex.Message);
            return whenInteractiveFails;
        }
    }

    private static string Norm(string s) =>
        new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static readonly Regex YearPattern = new(@"\b(1[5-9][0-9]{2}|20[0-9]{2})\b", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static int? YearInText(string? text) =>
        text is not null && YearPattern.Match(text) is { Success: true } m ? int.Parse(m.Value) : null;

    // The one-line description names the kind of thing ("2024 video game developed by ...", "1999 science fiction film", "American rock band").
    // A heuristic, used only for the interactive list; automatic matching never reads a description.
    private static readonly Dictionary<string, (Regex Fit, Regex? Reject)> Classes = new()
    {
        ["movies"] = (R(@"\b(film|movie)\b"), R(@"\b(film|movie) (series|franchise)\b|\bfranchise\b|\bsoundtrack\b|\bsong\b|\balbum\b|\b(director|actor|actress|producer|screenwriter|critic|studio|company|festival|award|distributor)\b")),
        ["tv"]     = (R(@"\b(television|tv|web|animated|anime|streaming)\b.*\b(series|show|program|programme|miniseries)\b|\bminiseries\b|\bsitcom\b|\bmini-series\b"),
                      R(@"\bepisode\b|\bseason\b|\bsoundtrack\b|\bcharacter\b|\b(presenter|host|actor|actress|producer|network|channel)\b")),
        ["game"]   = (R(@"\bvideo game\b"), R(@"\b(series|franchise|character|developer|company|publisher|console|genre|platform|engine|review bombing)\b")),
        ["book"]   = (R(@"\b(novel|novella|book|memoir|biography|short story|graphic novel|comic|poem|play)\b"), R(@"\b(film|television|video game|album|song)\b")),
    };

    internal static bool ClassFits(string type, int level, string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return false;
        // No type given (the plugin's own search box): the description may name any kind the plugin serves.
        if (string.IsNullOrEmpty(type)) return Types.Any(t => ClassFits(t, level, description));
        if (type == "music")
        {
            return level switch
            {
                0 => R(@"\b(band|singer|musician|rapper|duo|musical group|vocal group|girl group|idol group|composer|dj|songwriter|vocalist|orchestra|ensemble|singer-songwriter|record producer)\b").IsMatch(description)
                     && !R(@"\b(album|song|single|film|ep|mixtape)\b").IsMatch(description),
                1 => R(@"\b(album|ep|mixtape|compilation|soundtrack|studio album|live album)\b").IsMatch(description),
                _ => R(@"\b(song|single|track|composition|recording)\b").IsMatch(description),
            };
        }
        return Classes.TryGetValue(type, out var c) && c.Fit.IsMatch(description) && (c.Reject is null || !c.Reject.IsMatch(description));
    }

    private static Regex R(string pattern) =>
        new(pattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));

    public void Dispose() => _client?.Dispose();
}
