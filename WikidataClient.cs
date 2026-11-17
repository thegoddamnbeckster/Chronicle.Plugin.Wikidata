using System.Net;
using System.Text;
using System.Text.Json;

namespace Chronicle.Plugin.Wikidata;

/// <summary>
/// Thread-safe client for the Wikidata Action API (read only). Wikimedia asks for a descriptive User-Agent with a way to reach the
/// operator, serial requests, and respect for <c>Retry-After</c>; there is no key and no account.
///
/// Measured 2026-11-16 (docs/plans/2026-11-16-wikidata-plugin-design.md section 2) and handled here:
///   * errors arrive as HTTP 200 with an <c>error</c> object (<c>no-such-entity</c>, <c>maxlag</c>): the status line says nothing, so every
///     answer is read for it. A missing item is data; <c>maxlag</c> and anything unknown is a pause (never "nothing there")
///   * an HTML answer where JSON was asked for is a block or maintenance page: stop, hold every caller for 30 minutes, never work around it
///   * 429 and 5xx are retried a bounded number of times honouring <c>Retry-After</c>, then thrown with their status
///   * no <c>maxlag</c> is sent, because reads do not need it and a lagging query-service host would answer every call with an error
/// </summary>
internal sealed class WikidataClient : IDisposable
{
    internal const string PluginId = "chronicle.plugin.wikidata";
    internal const string Endpoint = "https://www.wikidata.org/w/api.php";

    /// <summary>Wikidata's own limit on ids per <c>wbgetentities</c> call for an ordinary client.</summary>
    internal const int BatchSize = 50;

    private static readonly System.Text.RegularExpressions.Regex PropertyPattern = new(@"^P[0-9]{1,6}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly System.Text.RegularExpressions.Regex ValuePattern = new(@"^[A-Za-z0-9._\-]{1,128}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private const int MaxRetries = 3;
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(2);

    /// <summary>One pacing state for every client in the process (a plugin may be two registry instances).</summary>
    private sealed class Pacing(TimeSpan minInterval)
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public TimeSpan MinInterval = minInterval;
        public DateTime NextAllowed = DateTime.MinValue;
    }

    /// <summary>The floor under any configured interval: Wikimedia asks for serial, unhurried requests.</summary>
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(1.2);

    private static readonly Pacing SharedPacing = new(DefaultInterval);

    private readonly HttpClient _http;
    private readonly Pacing _pacing;
    private readonly TimeSpan _blockedPenalty;
    private readonly TimeSpan _retryBase;

    public WikidataClient(string userAgent, TimeSpan? interval = null)
    {
        _pacing = SharedPacing;
        SetInterval(interval);
        _blockedPenalty = TimeSpan.FromMinutes(30);
        _retryBase = TimeSpan.FromSeconds(2);
        // The call deadline of the provider bounds a whole fetch; one request never needs more than this.
        _http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _http.DefaultRequestHeaders.Add("User-Agent", userAgent);
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    /// <summary>Test-only constructor: a pre-built HttpClient and its own pacing state.</summary>
    internal WikidataClient(HttpClient http, TimeSpan minInterval, TimeSpan? blockedPenalty = null, TimeSpan? retryBase = null)
    {
        _retryBase = retryBase ?? TimeSpan.Zero;
        _http = http;
        _pacing = new Pacing(minInterval);
        _blockedPenalty = blockedPenalty ?? TimeSpan.FromMinutes(30);
    }

    private static void SetInterval(TimeSpan? interval)
    {
        var value = interval is { } i && i > MinimumInterval ? i : (interval is null ? DefaultInterval : MinimumInterval);
        SharedPacing.MinInterval = value;
    }

    // ── Calls ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The items that hold <paramref name="value"/> for <paramref name="property"/>. An empty list means Wikidata has none (a
    /// genuine <c>totalhits: 0</c>); a failure throws.
    /// </summary>
    public async Task<IReadOnlyList<string>> FindByStatementAsync(string property, string value, CancellationToken ct)
    {
        // CirrusSearch syntax: haswbstatement:P1733=553850. The value is NOT quoted: measured live, a quoted value matches nothing
        // (0 hits for a Steam id Wikidata holds). So the value is held to plain id characters instead, which also means it can never
        // carry search syntax of its own (a space, a quote, a colon, an operator).
        if (!PropertyPattern.IsMatch(property) || !ValuePattern.IsMatch(value))
            throw new ArgumentException($"Not a plain id to look up on Wikidata: {property}={value}");

        var root = await GetAsync(new()
        {
            ["action"] = "query", ["list"] = "search", ["format"] = "json", ["srlimit"] = "5",
            ["srsearch"] = $"haswbstatement:{property}={value}",
        }, "an id lookup", ct).ConfigureAwait(false);

        var hits = new List<string>();
        foreach (var h in WdJson.List(WdJson.Obj(root, "query"), "search"))
            if (WdJson.Str(h, "title") is { } t && WikidataIds.Parse(t) is { } id)
                hits.Add(id.Q);
        return hits;
    }

    /// <summary>Items by id, each as the complete entity object. An id Wikidata does not hold is absent from the result.</summary>
    public async Task<IReadOnlyDictionary<string, JsonElement>> GetEntitiesAsync(
        IEnumerable<string> ids, string props, string? languages, CancellationToken ct)
    {
        var all = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in ids.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(BatchSize))
        {
            try
            {
                Collect(await FetchBatchAsync(batch, props, languages, ct).ConfigureAwait(false), all);
            }
            catch (WdMissingException) when (batch.Length > 1)
            {
                // The service refuses the WHOLE batch when one id is malformed; asking one at a time says which.
                foreach (var one in batch)
                {
                    try { Collect(await FetchBatchAsync([one], props, languages, ct).ConfigureAwait(false), all); }
                    catch (WdMissingException) { /* this one is not an item */ }
                }
            }
            catch (WdMissingException)
            {
                // a batch of one that is not an item: absent
            }
        }
        return all;
    }

    private Task<JsonElement?> FetchBatchAsync(string[] batch, string props, string? languages, CancellationToken ct)
    {
        var query = new Dictionary<string, string>
        {
            ["action"] = "wbgetentities", ["format"] = "json", ["props"] = props, ["ids"] = string.Join('|', batch),
        };
        if (languages is not null) query["languages"] = languages;
        return GetAsync(query, "an item", ct, allowMissing: true);
    }

    private static void Collect(JsonElement? root, Dictionary<string, JsonElement> all)
    {
        foreach (var e in WdJson.Members(root, "entities"))
        {
            if (e.Value.ValueKind != JsonValueKind.Object) continue;
            if (e.Value.TryGetProperty("missing", out _)) continue;
            // A merged item answers under its target id; the entity says which it is.
            var id = WdJson.Str(e.Value, "id") ?? e.Name;
            all[id] = e.Value;
            if (!string.Equals(id, e.Name, StringComparison.OrdinalIgnoreCase)) all[e.Name] = e.Value;
        }
    }

    /// <summary>Ranked text matches (id, label, description) for an interactive search.</summary>
    public async Task<IReadOnlyList<WdSearchHit>> SearchAsync(string text, int limit, CancellationToken ct)
    {
        var root = await GetAsync(new()
        {
            ["action"] = "wbsearchentities", ["format"] = "json", ["search"] = text, ["language"] = "en", ["uselang"] = "en",
            ["type"] = "item", ["limit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
        }, "a search", ct).ConfigureAwait(false);

        var hits = new List<WdSearchHit>();
        foreach (var h in WdJson.List(root, "search"))
            if (WdJson.Str(h, "id") is { } id && WikidataIds.Parse(id) is not null)
            {
                // "match" says whether the query hit the label or an alias, and which text.
                var match = WdJson.Obj(h, "match");
                var alias = WdJson.Str(match, "type") == "alias" ? WdJson.Str(match, "text") : null;
                hits.Add(new(id, WdJson.Str(h, "label"), WdJson.Str(h, "description"), alias is null ? [] : [alias]));
            }
        return hits;
    }

    // ── Transport ─────────────────────────────────────────────────────────────

    private async Task<JsonElement?> GetAsync(Dictionary<string, string> query, string what, CancellationToken ct, bool allowMissing = false)
    {
        var url = BuildUrl(query);
        var delay = _retryBase;

        for (var attempt = 0; ; attempt++)
        {
            await ThrottleAsync(ct).ConfigureAwait(false);

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Get, url), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // The request's own timeout. A pause, not "no result" and not a retried item failure (E1, E4).
                throw new HttpRequestException($"Wikidata did not answer {what} in time.", null, null);
            }
            catch (HttpRequestException) when (attempt < MaxRetries)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                delay = Next(delay);
                continue;
            }

            using (response)
            {
                var body   = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var status = response.StatusCode;

                if (LooksLikeHtml(response, body))
                {
                    Hold(_blockedPenalty);
                    throw WithRetryAfter(new HttpRequestException(
                        "Wikidata answered with a web page instead of data (a block or maintenance page). Chronicle does not try to get past it. " +
                        $"All Wikidata requests are paused for {_blockedPenalty.TotalMinutes:F0} minutes and then resume by themselves.",
                        null, HttpStatusCode.TooManyRequests), _blockedPenalty);
                }

                if (status == HttpStatusCode.TooManyRequests)
                {
                    if (attempt >= MaxRetries)
                        throw WithRetryAfter(new HttpRequestException("Wikidata is rate limiting requests.", null, HttpStatusCode.TooManyRequests),
                                             AskedFor(response, delay));
                    Hold(WaitFor(response, delay));
                    delay = Next(delay);
                    continue;
                }

                if ((int)status >= 500)
                {
                    if (attempt >= MaxRetries)
                        throw new HttpRequestException($"Wikidata returned HTTP {(int)status} for {what}.", null, status);
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    delay = Next(delay);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException($"Wikidata returned HTTP {(int)status} for {what}.", null, status);

                var root = Parse(body, what);

                // Errors ride on HTTP 200.
                if (WdJson.Obj(root, "error") is { } error)
                {
                    var code = WdJson.Str(error, "code") ?? "unknown";
                    if (code == "no-such-entity" && allowMissing) throw new WdMissingException();
                    if (code == "maxlag")
                    {
                        if (attempt >= MaxRetries)
                            throw WithRetryAfter(new HttpRequestException("Wikidata is lagging behind and asked clients to wait.", null, HttpStatusCode.TooManyRequests),
                                                 AskedFor(response, delay));
                        Hold(WaitFor(response, delay));
                        delay = Next(delay);
                        continue;
                    }
                    // Anything the plugin does not recognise is the service refusing the request: a pause, never an empty result (E2).
                    throw new WdRefusedException($"Wikidata refused {what}: {code} ({WdJson.Str(error, "info") ?? "no detail"}).");
                }

                return root;
            }
        }
    }

    internal static string BuildUrl(IEnumerable<KeyValuePair<string, string>> query)
    {
        var sb = new StringBuilder(Endpoint);
        var first = true;
        foreach (var (key, value) in query)
        {
            sb.Append(first ? '?' : '&').Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(value));
            first = false;
        }
        return sb.ToString();
    }

    private static bool LooksLikeHtml(HttpResponseMessage response, string body) =>
        response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true
        || body.AsSpan().TrimStart().StartsWith("<", StringComparison.Ordinal);

    private static JsonElement Parse(string body, string what)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            // A non-object answer (an array, a string) is not what the API gives; it is a pause, not data.
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new WdRefusedException($"Wikidata returned an unexpected answer for {what}.");
            return doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new WdRefusedException($"Wikidata returned a non-JSON answer for {what}.", ex);
        }
    }

    /// <summary>
    /// Says how long Wikidata (or this plugin's own hold) asked clients to wait, on the exception, under the key the host reads
    /// (<c>Data["RetryAfter"]</c>, a TimeSpan): the host's queue honours it, and sends the item to the back of the queue when the wait is long.
    /// </summary>
    internal static HttpRequestException WithRetryAfter(HttpRequestException ex, TimeSpan after)
    {
        if (after > TimeSpan.Zero) ex.Data["RetryAfter"] = after;
        return ex;
    }

    /// <summary>What the service itself asked for (<c>Retry-After</c>, in full, not cut to what this client sleeps), else the fallback.</summary>
    internal static TimeSpan AskedFor(HttpResponseMessage response, TimeSpan fallback) =>
        response.Headers.RetryAfter?.Delta is { } ra && ra > TimeSpan.Zero ? ra : fallback;

    private static TimeSpan WaitFor(HttpResponseMessage response, TimeSpan fallback)
    {
        if (response.Headers.RetryAfter?.Delta is { } ra && ra > TimeSpan.Zero)
            return ra > MaxWait ? MaxWait : ra;
        return fallback > MaxWait ? MaxWait : fallback;
    }

    private static TimeSpan Next(TimeSpan d) => TimeSpan.FromSeconds(Math.Min(d.TotalSeconds * 2, 30));

    /// <summary>Pushes the shared "next call allowed" time out, so every caller in the process waits.</summary>
    private void Hold(TimeSpan span)
    {
        lock (_pacing)
        {
            var until = DateTime.UtcNow + span;
            if (until > _pacing.NextAllowed) _pacing.NextAllowed = until;
        }
    }

    private async Task ThrottleAsync(CancellationToken ct)
    {
        await _pacing.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            DateTime next;
            lock (_pacing) next = _pacing.NextAllowed;
            var wait = next - DateTime.UtcNow;
            // A pause longer than any rate-limit window is a hold, not a queue: say so now rather than sleep inside a call the host
            // abandons. The status makes the host pause its pass instead of recording "nothing there".
            if (wait > MaxWait)
                throw WithRetryAfter(new HttpRequestException(
                    $"Wikidata requests are paused until {DateTime.Now + wait:HH:mm} (it sent a block page or asked for a long wait).",
                    null, HttpStatusCode.TooManyRequests), wait);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            lock (_pacing) _pacing.NextAllowed = DateTime.UtcNow + _pacing.MinInterval;
        }
        finally
        {
            _pacing.Gate.Release();
        }
    }

    public void Dispose() => _http.Dispose();   // the pacing state is shared and outlives any one client

    private sealed class WdMissingException : Exception { }
}

/// <summary>
/// The service answered, but not with what the API gives (an unrecognised error, a body that is not JSON). A pause for the host (null status),
/// and a failure the provider may count against ONE item, unlike a timeout or a rate limit, which say nothing about the item (E7).
/// </summary>
internal sealed class WdRefusedException : HttpRequestException
{
    public WdRefusedException(string message, Exception? inner = null) : base(message, inner, null) { }
}

/// <summary>One text-search hit.</summary>
internal sealed record WdSearchHit(string Id, string? Label, string? Description, IReadOnlyList<string> Aliases);
