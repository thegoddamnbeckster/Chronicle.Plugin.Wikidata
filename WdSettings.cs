using System.Globalization;

namespace Chronicle.Plugin.Wikidata;

/// <summary>The plugin's settings, read tolerantly (X4): a value that cannot be used is reported once and replaced by its default.</summary>
internal sealed record WdSettings(string? Contact, TimeSpan Interval, string? Problem)
{
    public const string KeyContact  = "contact_info";
    public const string KeyInterval = "min_request_interval_ms";

    public const string ProjectUrl = "https://github.com/thegoddamnbeckster/Chronicle.Plugin.Wikidata";

    public static string Version => System.Reflection.CustomAttributeExtensions
        .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(typeof(WdSettings).Assembly)
        ?.InformationalVersion.Split('+')[0] ?? "1.0.0";

    /// <summary>
    /// Wikimedia asks every client to say who it is and how to reach it. The project's address does that; an operator may add a
    /// contact of their own, and the plugin never fills one in (no personal detail leaves the machine unless its owner typed it).
    /// </summary>
    public static string UserAgent(string? contact) =>
        $"Chronicle-Wikidata-plugin/{Version} ({ProjectUrl}{(contact is null ? string.Empty : "; " + contact)})";

    public static WdSettings From(IReadOnlyDictionary<string, string>? raw)
    {
        string? problem = null;
        string? contact = null;
        if (raw is not null && raw.TryGetValue(KeyContact, out var c) && !string.IsNullOrWhiteSpace(c))
        {
            var t = c.Trim();
            if (t.Length > 200 || t.Any(char.IsControl) || t.Contains('(') || t.Contains(')'))
                problem = "The contact detail was ignored: it must be one line of at most 200 characters with no brackets.";
            else
                contact = t;
        }

        var interval = WikidataClient.DefaultInterval;
        if (raw is not null && raw.TryGetValue(KeyInterval, out var i) && !string.IsNullOrWhiteSpace(i))
        {
            if (double.TryParse(i.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && ms >= 0 && ms < 600_000)
                interval = ms < WikidataClient.MinimumInterval.TotalMilliseconds
                    ? WikidataClient.MinimumInterval
                    : TimeSpan.FromMilliseconds(ms);
            else
                problem ??= $"The request interval \"{i.Trim()}\" was ignored: enter a number of milliseconds, 1000 or more.";
        }
        return new WdSettings(contact, interval, problem);
    }
}
