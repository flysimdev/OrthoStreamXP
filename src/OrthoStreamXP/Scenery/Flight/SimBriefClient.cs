using System.Globalization;
using System.Net;
using System.Text.Json;

namespace OrthoStreamXP.Scenery;

/// <summary>SimBrief's answer: the latest flight plan, or what went wrong in plain words.</summary>
internal sealed record SimBriefAnswer(FlightPlan? Plan, string? Problem)
{
    public FlightOrigin? Origin => Plan?.Origin;

    public string? Destination => Plan?.Destination?.Icao;

    /// <summary>Why there is no flight plan, as the window says it.</summary>
    public string NoPlan => $"No flight plan: {Problem?.TrimEnd('.')}.";

    public static SimBriefAnswer Failed(string problem) => new(null, problem);
}

/// <summary>
/// Reads the latest flight plan (OFP) a SimBrief user generated, through SimBrief's public fetcher: the departure,
/// arrival and alternate airports and the navlog. A failure is answered with its reason, never an exception: the flight plan is a hint, never a reason for the
/// mount to fail.
/// </summary>
internal sealed class SimBriefClient(HttpClient http)
{
    public const string FetcherUrl = "https://www.simbrief.com/api/xml.fetcher.php";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>The fetcher's address for a username, or for the numeric Pilot ID when <paramref name="user"/> is all digits.</summary>
    public static Uri UrlFor(string user)
    {
        string trimmed = user.Trim();
        string key = trimmed.Length > 0 && trimmed.All(char.IsAsciiDigit) ? "userid" : "username";
        return new Uri($"{FetcherUrl}?{key}={Uri.EscapeDataString(trimmed)}&json=1");
    }

    /// <summary>
    /// Asks SimBrief for the user's latest flight plan. The problem it answers with is logged and shown: it never holds
    /// the username or Pilot ID, even when SimBrief's text or an exception's message would (the URL carries it).
    /// </summary>
    public async Task<SimBriefAnswer> FetchAsync(string user, CancellationToken ct)
    {
        SimBriefAnswer answer = await AskAsync(user, ct).ConfigureAwait(false);
        string trimmed = user.Trim();
        return answer.Problem is { } problem && trimmed.Length > 0 && problem.Contains(trimmed, StringComparison.OrdinalIgnoreCase)
            ? answer with { Problem = problem.Replace(trimmed, "(your SimBrief user)", StringComparison.OrdinalIgnoreCase) }
            : answer;
    }

    private async Task<SimBriefAnswer> AskAsync(string user, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(user)) return SimBriefAnswer.Failed("no SimBrief username or Pilot ID is set");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            // The User-Agent goes on the request: the client handed in may be shared and set up for something else.
            using var request = new HttpRequestMessage(HttpMethod.Get, UrlFor(user));
            request.Headers.UserAgent.ParseAdd(HttpIdentity.UserAgent);
            using HttpResponseMessage response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            // SimBrief answers an unknown user with 400 and its reason in fetch.status.
            if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.BadRequest)
                return SimBriefAnswer.Failed($"SimBrief did not answer ({(int)response.StatusCode} {response.ReasonPhrase})");
            return Parse(body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return SimBriefAnswer.Failed($"SimBrief did not answer within {Timeout.TotalSeconds:0} s");
        }
        catch (HttpRequestException ex)
        {
            return SimBriefAnswer.Failed($"SimBrief could not be reached ({ex.Message})");
        }
    }

    /// <summary>
    /// The flight plan in SimBrief's JSON, or the reason there is none. Any shape it does not expect (a field of another
    /// type, an object that is a string) is "could not be read", never an exception: the answer comes from outside.
    /// </summary>
    internal static SimBriefAnswer Parse(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return SimBriefAnswer.Failed(Unreadable);
            // fetch.status is SimBrief's own word on the request: present but of another shape, nothing else is trusted.
            if (root.TryGetProperty("fetch", out JsonElement fetch)
                && (fetch.ValueKind != JsonValueKind.Object || (fetch.TryGetProperty("status", out JsonElement s) && s.ValueKind != JsonValueKind.String)))
                return SimBriefAnswer.Failed(Unreadable);
            string? status = Text(fetch, "status"); // no fetch: Text reads nothing from the default element
            if (status is not null && !status.Equals("Success", StringComparison.OrdinalIgnoreCase))
                return SimBriefAnswer.Failed(status.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ? status[6..].Trim() : status);
            if (Airport(root, "origin") is not { } origin) return SimBriefAnswer.Failed("the latest flight plan has no departure position");
            return new SimBriefAnswer(new FlightPlan(origin, Airport(root, "destination"), Airport(root, "alternate"), Route(root), Generated(root), RequestId(root)), null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        {
            // JsonElement's accessors throw InvalidOperationException on a value of another kind: the checks above
            // should leave none, this is the net under them.
            return SimBriefAnswer.Failed(Unreadable);
        }
    }

    private const string Unreadable = "SimBrief's answer could not be read";

    /// <summary>An object-valued property; null when it is missing or not an object (SimBrief sends "" for empty ones).</summary>
    private static JsonElement? Child(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Object
            ? value : null;

    /// <summary>An airport by its key; SimBrief gives several alternates as an array (the first is taken).</summary>
    private static FlightOrigin? Airport(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out JsonElement airport)) return null;
        if (airport.ValueKind == JsonValueKind.Array) airport = airport.GetArrayLength() > 0 ? airport[0] : default;
        if (airport.ValueKind != JsonValueKind.Object || Number(airport, "pos_lat") is not { } lat || Number(airport, "pos_long") is not { } lon
            || lat is < -90 or > 90 || lon is < -180 or > 180) return null;
        return new FlightOrigin(Text(airport, "icao_code") ?? "", lat, lon, Number(airport, "elevation") ?? 0);
    }

    /// <summary>When the plan was generated: <c>params.time_generated</c>, Unix seconds (as a string, like the coordinates).</summary>
    private static DateTimeOffset? Generated(JsonElement root)
    {
        if (Child(root, "params") is not { } parameters
            || Number(parameters, "time_generated") is not { } seconds || seconds is <= 0 or > 32503680000) return null; // > year 3000: not a time
        return DateTimeOffset.FromUnixTimeSeconds((long)seconds);
    }

    /// <summary>SimBrief's id of the OFP, <c>params.request_id</c> (a string or a number).</summary>
    private static string? RequestId(JsonElement root)
    {
        if (Child(root, "params") is not { } parameters || !parameters.TryGetProperty("request_id", out JsonElement id)) return null;
        string? text = id.ValueKind switch
        {
            JsonValueKind.String => id.GetString(),
            JsonValueKind.Number => id.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>The navlog's points in order; one point comes as an object, not an array.</summary>
    private static List<RoutePoint> Route(JsonElement root)
    {
        if (Child(root, "navlog") is not { } navlog || !navlog.TryGetProperty("fix", out JsonElement fixes)) return [];
        IEnumerable<JsonElement> all = fixes.ValueKind switch
        {
            JsonValueKind.Array => fixes.EnumerateArray(),
            JsonValueKind.Object => [fixes],
            _ => [],
        };
        var route = new List<RoutePoint>();
        foreach (JsonElement fix in all)
            if (fix.ValueKind == JsonValueKind.Object && Number(fix, "pos_lat") is { } lat && Number(fix, "pos_long") is { } lon && lat is >= -90 and <= 90 && lon is >= -180 and <= 180)
                route.Add(new RoutePoint(Text(fix, "ident") ?? "", lat, lon, Number(fix, "altitude_feet") ?? 0,
                    (Number(fix, "time_total") ?? 0) / 60, Text(fix, "stage") ?? ""));
        return route;
    }

    private static string? Text(JsonElement parent, string name) =>
        parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>A coordinate: SimBrief sends them as strings ("48.353783"), a number is taken too.</summary>
    private static double? Number(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out JsonElement value)) return null;
        double? number = value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetDouble(out double n) => n,
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) => d,
            _ => null,
        };
        return number is { } finite && double.IsFinite(finite) ? finite : null;
    }
}
