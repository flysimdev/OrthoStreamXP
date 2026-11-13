using System.Net;
using System.Text;
using OrthoStreamXP.Scenery;
using OrthoStreamXP.Textures;

namespace OrthoStreamXP.Tests;

/// <summary>The SimBrief flight plan's departure: reading the OFP, picking the package's textures there, and skipping the cached ones.</summary>
public sealed class SimBriefPrefetchTests : IDisposable
{
    private const double Lat = 48.353783, Lon = 11.786086; // EDDM
    private readonly TempTree _tree = new();

    public void Dispose() => _tree.Dispose();

    private const string Ofp = """
        {"fetch":{"userid":"123456","static_id":{},"status":"Success","time":"0.01"},
         "origin":{"icao_code":"EDDM","iata_code":"MUC","pos_lat":"48.353783","pos_long":"11.786086"},
         "destination":{"icao_code":"EGLL","pos_lat":"51.4775","pos_long":"-0.461389"}}
        """;

    // ---------------------------------------------------------------- the client

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public Uri? Asked { get; private set; }

        public string? Agent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked = request.RequestUri;
            Agent = request.Headers.UserAgent.ToString();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }

    private static async Task<(FlightOrigin? Origin, Uri? Asked)> Fetch(string user, HttpStatusCode status, string body)
    {
        using var handler = new StubHandler(status, body);
        using var http = new HttpClient(handler);
        FlightOrigin? origin = (await new SimBriefClient(http).FetchAsync(user, TestContext.Current.CancellationToken)).Origin;
        return (origin, handler.Asked);
    }

    [Fact]
    public async Task Reads_the_origin_of_the_latest_flight_plan()
    {
        var (origin, asked) = await Fetch("pilot", HttpStatusCode.OK, Ofp);

        Assert.Equal(new FlightOrigin("EDDM", Lat, Lon), origin);
        Assert.Equal("https://www.simbrief.com/api/xml.fetcher.php?username=pilot&json=1", asked!.AbsoluteUri);
    }

    [Fact]
    public async Task SimBrief_is_asked_with_the_User_Agent_of_the_tile_downloads()
    {
        // R7: one orthostreamxp/<version> for every server, whatever the HttpClient handed in was set up with.
        using var handler = new StubHandler(HttpStatusCode.OK, Ofp);
        using var http = new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.ParseAdd("something-else/9");

        await new SimBriefClient(http).FetchAsync("pilot", TestContext.Current.CancellationToken);

        Assert.Equal(HttpIdentity.UserAgent, handler.Agent);
        Assert.Matches(@"^orthostreamxp/\d+\.\d+", HttpIdentity.UserAgent);
    }

    private sealed class ThrowingHandler(Exception ex) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(ex);
    }

    [Theory]
    [InlineData("SecretPilot")]
    [InlineData("987654")] // a Pilot ID
    public async Task No_problem_SimBrief_answers_with_holds_the_username_or_Pilot_ID(string user)
    {
        // R8: the problem is logged and shown; the user is never in it, whatever the server or the network said.
        CancellationToken ct = TestContext.Current.CancellationToken;
        string url = SimBriefClient.UrlFor(user).AbsoluteUri;
        var answers = new List<SimBriefAnswer>();
        foreach (HttpMessageHandler handler in new HttpMessageHandler[]
        {
            new StubHandler(HttpStatusCode.BadRequest, $$$"""{"fetch":{"status":"Error: Unknown UserID {{{user}}}"}}"""),
            new StubHandler(HttpStatusCode.OK, $$$"""{"fetch":{"status":"Error: no flight plan for {{{user.ToUpperInvariant()}}}"}}"""),
            new StubHandler(HttpStatusCode.InternalServerError, user),
            new StubHandler(HttpStatusCode.OK, $"<html>{user}</html>"),
            new StubHandler(HttpStatusCode.OK, """{"fetch":{"status":"Success"}}"""),
            new ThrowingHandler(new HttpRequestException($"Connection refused ({url})")),
        })
        {
            using (handler)
            using (var http = new HttpClient(handler))
                answers.Add(await new SimBriefClient(http).FetchAsync(user, ct));
        }

        Assert.All(answers, a =>
        {
            Assert.NotNull(a.Problem);
            Assert.DoesNotContain(user, a.Problem, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData("""{"fetch":"Success","origin":{"pos_lat":"48.3","pos_long":"11.7"}}""")] // fetch as a string
    [InlineData("""{"fetch":{"status":1},"origin":{"pos_lat":"48.3","pos_long":"11.7"}}""")] // status as a number
    [InlineData("""{"fetch":{"status":"Success"},"origin":"EDDM"}""")] // an airport as a string
    [InlineData("""["not", "an", "object"]""")]
    [InlineData("""{"fetch":{"status":"Success"},"origin":{"pos_lat":"48.3","pos_long":"11.7"},"navlog":""}""")]
    [InlineData("""{"fetch":{"status":"Success"},"origin":{"pos_lat":"48.3","pos_long":"11.7"},"navlog":{"fix":["a", 1, null]},"params":""}""")]
    [InlineData("""{"fetch":{"status":"Success"},"origin":{"pos_lat":1e999,"pos_long":"11.7"}}""")]
    [InlineData("""{"fetch":{"status":"Success"},"origin":{"pos_lat":"48.3","pos_long":"11.7"},"alternate":[]}""")]
    public void An_answer_of_an_unexpected_shape_is_read_or_refused_never_thrown(string json)
    {
        // Maintainer, #30 item 1: InvalidOperationException from a field of another type broke the stop.
        SimBriefAnswer answer = SimBriefClient.Parse(json);
        Assert.True(answer.Plan is not null || answer.Problem is not null);
    }

    [Fact]
    public void The_shapes_SimBrief_leaves_empty_still_give_the_plan()
    {
        // SimBrief sends "" for an empty object: the plan is read without the navlog's junk or its times.
        SimBriefAnswer answer = SimBriefClient.Parse("""
            {"fetch":{"status":"Success"},"origin":{"icao_code":"EDDM","pos_lat":"48.3","pos_long":"11.7"},
             "navlog":{"fix":["junk", {"ident":"TOC","pos_lat":"48.5","pos_long":"11.9","stage":"CLB"}]},"params":""}
            """);
        Assert.Equal("EDDM", answer.Origin!.Icao);
        Assert.Equal(["TOC"], answer.Plan!.Route.Select(p => p.Ident));
        Assert.Null(answer.Plan.Generated);
        Assert.Equal("SimBrief's answer could not be read", SimBriefClient.Parse("""{"fetch":{"status":1}}""").Problem);
    }

    [Fact]
    public async Task A_fetch_that_throws_leaves_no_plan_and_ends_the_session_cleanly()
    {
        // The stop awaits WhenDone: a fault there kept the listener and the packages from being disposed.
        var (session, _, departure, _) = Session(() => throw new InvalidOperationException("a bug"));
        session.Start();
        await session.WhenDone(); // does not throw
        Assert.Null(await session.FirstPlan);
        Assert.Empty(departure);
        Assert.StartsWith("No flight plan: the flight plan could not be read", await session.DescribeFirstAsync(), StringComparison.Ordinal);
        Assert.StartsWith("No flight plan", await session.ReloadAsync(), StringComparison.Ordinal);
        await session.WhenDone();
    }

    [Fact]
    public void A_number_is_the_pilot_id_anything_else_the_username()
    {
        Assert.Equal("https://www.simbrief.com/api/xml.fetcher.php?userid=123456&json=1", SimBriefClient.UrlFor(" 123456 ").AbsoluteUri);
        Assert.Equal("https://www.simbrief.com/api/xml.fetcher.php?username=Jo%20Doe&json=1", SimBriefClient.UrlFor("Jo Doe").AbsoluteUri);
    }

    [Fact]
    public async Task An_unknown_user_or_a_failing_server_gives_no_origin()
    {
        var (unknown, _) = await Fetch("nobody", HttpStatusCode.BadRequest, """{"fetch":{"status":"Error: Unknown UserID"}}""");
        var (down, _) = await Fetch("pilot", HttpStatusCode.InternalServerError, "oops");
        var (garbled, _) = await Fetch("pilot", HttpStatusCode.OK, "<html>");
        var (noOrigin, _) = await Fetch("pilot", HttpStatusCode.OK, """{"fetch":{"status":"Success"}}""");

        Assert.Null(unknown);
        Assert.Null(down);
        Assert.Null(garbled);
        Assert.Null(noOrigin);
    }

    [Fact]
    public async Task The_connection_test_names_the_flight_or_says_why_there_is_none()
    {
        using var ok = new StubHandler(HttpStatusCode.OK, Ofp);
        using var unknown = new StubHandler(HttpStatusCode.BadRequest, """{"fetch":{"status":"Error: Unknown UserID"}}""");
        using var okHttp = new HttpClient(ok);
        using var unknownHttp = new HttpClient(unknown);
        CancellationToken ct = TestContext.Current.CancellationToken;

        SimBriefAnswer found = await new SimBriefClient(okHttp).FetchAsync("pilot", ct);
        SimBriefAnswer missing = await new SimBriefClient(unknownHttp).FetchAsync("nobody", ct);
        SimBriefAnswer empty = await new SimBriefClient(okHttp).FetchAsync("  ", ct);

        Assert.Equal("EGLL", found.Destination);
        Assert.Contains("EDDM → EGLL", Gui.SimBriefTestViewModel.Describe(found), StringComparison.Ordinal);
        Assert.Equal("No flight plan: Unknown UserID.", Gui.SimBriefTestViewModel.Describe(missing));
        Assert.Null(empty.Origin);
        Assert.NotNull(empty.Problem);
    }

    // ---------------------------------------------------------------- the package's textures there

    private string Package(params TextureName[] textures)
    {
        foreach (TextureName name in textures) _tree.WriteText($"pkg/terrain/{name.Stem}.ter", "A\n800\nTERRAIN\n");
        Directory.CreateDirectory(_tree.PathOf("pkg"));
        return _tree.PathOf("pkg");
    }

    private static readonly TextureName AtAirport = TextureName.At(Lat, Lon, "BI", 18);
    private static readonly TextureName Around = TextureName.At(Lat + 0.03, Lon, "BI", 16); // about 3.5 km north
    private static readonly TextureName FarAway = TextureName.At(Lat + 0.3, Lon, "BI", 16); // about 33 km north

    private static string[] Stems(IEnumerable<VirtualPath> textures) => [.. textures.Select(t => Path.GetFileNameWithoutExtension(t.Name))];

    [Fact]
    public void Picks_the_package_textures_within_the_radius_nearest_first()
    {
        string package = Package(FarAway, Around, AtAirport);
        _tree.WriteText($"pkg/terrain/{AtAirport.Stem}_sea_overlay.ter", "A\n800\nTERRAIN\n"); // the same texture again

        Assert.Equal([AtAirport.Stem, Around.Stem], Stems(AirportTextures.Near(package, Lat, Lon, radiusKm: 10, max: 100)));
        Assert.Equal([AtAirport.Stem], Stems(AirportTextures.Near(package, Lat, Lon, radiusKm: 10, max: 1)));
        Assert.Empty(AirportTextures.Near(_tree.PathOf("nothing"), Lat, Lon, radiusKm: 10, max: 100));
    }

    // ---------------------------------------------------------------- what is queued

    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly TimeProvider Clock = new FixedClock(Now);

    private static FlightPlan PlanFrom(FlightOrigin origin, DateTimeOffset? generated = null) => new(origin, null, null, [], generated);

    private async Task<(int Queued, List<VirtualPath> Prefetched)> Run(Func<VirtualPath, bool> isCached, FlightOrigin? origin = null,
        (double, double)? aircraft = null, DateTimeOffset? generated = null)
    {
        string package = Package(AtAirport, Around, FarAway);
        var prefetched = new List<VirtualPath>();
        var target = new DepartureTarget("pkg", package, isCached, textures =>
        {
            prefetched.AddRange(textures);
            return textures.Count;
        });
        var prefetch = new DeparturePrefetch(() => aircraft, [target], radiusKm: 10, Clock);
        return (prefetch.Run(PlanFrom(origin ?? new FlightOrigin("EDDM", Lat, Lon), generated), TestContext.Current.CancellationToken), prefetched);
    }

    [Fact]
    public async Task Only_textures_the_cache_does_not_have_are_queued()
    {
        var (queued, prefetched) = await Run(t => t.Name.StartsWith(AtAirport.Stem, StringComparison.Ordinal));

        Assert.Equal(1, queued);
        Assert.Equal([Around.Stem], Stems(prefetched));
    }

    [Fact]
    public async Task Nothing_is_queued_when_everything_is_cached()
    {
        var (queued, prefetched) = await Run(_ => true);

        Assert.Equal(0, queued);
        Assert.Empty(prefetched);
    }

    [Fact]
    public async Task An_aircraft_somewhere_else_means_the_flight_plan_is_not_this_flight()
    {
        var (queued, prefetched) = await Run(_ => false, aircraft: (51.4775, -0.461389));
        var (nearby, _) = await Run(_ => false, aircraft: (Lat + 0.01, Lon));

        Assert.Equal(0, queued);
        Assert.Empty(prefetched);
        Assert.Equal(2, nearby);
    }

    [Fact]
    public async Task A_plan_generated_more_than_six_hours_ago_is_taken_for_an_earlier_flight()
    {
        // R11: at mount start X-Plane usually sends no position, so the plan's age is the check.
        var (old, prefetched) = await Run(_ => false, generated: Now - TimeSpan.FromHours(7));
        var (recent, _) = await Run(_ => false, generated: Now - TimeSpan.FromHours(2));
        var (unknown, _) = await Run(_ => false); // a plan without the time: used

        Assert.Equal(0, old);
        Assert.Empty(prefetched);
        Assert.Equal(2, recent);
        Assert.Equal(2, unknown);
    }

    [Fact]
    public void Reads_when_the_plan_was_generated()
    {
        const string withTime = """{"fetch":{"status":"Success"},"params":{"request_id":"1","time_generated":"1762790400"},"origin":{"icao_code":"EDDM","pos_lat":"48.35","pos_long":"11.78"}}""";
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1762790400), SimBriefClient.Parse(withTime).Plan!.Generated);
        Assert.Equal("1", SimBriefClient.Parse(withTime).Plan!.Id); // params.request_id: tells a reloaded OFP from the same one
        Assert.Null(SimBriefClient.Parse(Ofp).Plan!.Id);
        Assert.Null(SimBriefClient.Parse(Ofp).Plan!.Generated); // no params
        const string garbled = """{"params":{"time_generated":"soon"},"origin":{"icao_code":"EDDM","pos_lat":"48.35","pos_long":"11.78"}}""";
        Assert.Null(SimBriefClient.Parse(garbled).Plan!.Generated);
    }

    // ---------------------------------------------------------------- the session

    private static readonly FlightOrigin Eddm = new("EDDM", Lat, Lon), Egll = new("EGLL", 51.4775, -0.461389);

    private static SimBriefAnswer Answer(FlightOrigin from, FlightOrigin to, string id) => new(new FlightPlan(from, to, null, [], null, id), null);

    [Fact]
    public async Task Home_is_told_the_flight_plan_found_at_the_start_or_why_there_is_none()
    {
        SimBriefSession Session(SimBriefAnswer answer) => new(_ => Task.FromResult(answer),
            [new DepartureTarget("pkg", Package(AtAirport, Around), _ => false, textures => textures.Count)], [], () => null, radiusKm: 10, Clock);

        Assert.StartsWith("Flight plan: EDDM → EGLL", await Session(Answer(Eddm, Egll, "4")).DescribeFirstAsync(), StringComparison.Ordinal);
        Assert.Equal("No flight plan: no flight plan yet.", await Session(SimBriefAnswer.Failed("no flight plan yet")).DescribeFirstAsync());
        Assert.Equal("EDDM", new FlightPlan(Eddm, null, null, []).Describe()); // no destination: the departure alone
    }

    [Fact]
    public async Task The_departure_prefetch_runs_once_the_first_plan_is_there()
    {
        var departure = new List<VirtualPath>();
        var session = new SimBriefSession(_ => Task.FromResult(Answer(Eddm, Egll, "5")),
            [new DepartureTarget("pkg", Package(AtAirport, Around), _ => false, textures =>
            {
                lock (departure) departure.AddRange(textures);
                return textures.Count;
            })], [], () => null, radiusKm: 10, Clock);

        session.Start();
        await session.WhenDone();

        Assert.Equal([AtAirport.Stem, Around.Stem], Stems(departure));
        Assert.Equal("5", session.Current!.Id);
    }

    // ---------------------------------------------------------------- reloading the plan (M1, R9)

    /// <summary>A session over the EDDM package, its fetch answering <paramref name="answers"/> in turn (the last again after).</summary>
    private (SimBriefSession Session, Func<int> Fetches, List<VirtualPath> Departure, Func<(int Cancel, int CancelDeparture, int Starts)> Counts)
        Session(params Func<Task<SimBriefAnswer>>[] answers)
    {
        int fetches = 0, cancels = 0, cancelDepartures = 0, starts = 0;
        var departure = new List<VirtualPath>();
        string package = Package(AtAirport, Around);
        var target = new DepartureTarget("pkg", package, _ => false, textures =>
        {
            lock (departure) departure.AddRange(textures);
            return textures.Count;
        });
        var ground = new GroundTarget("pkg", package, (_, _, _, _) => (0, 0), (_, _, _) => (0, 0), () => ++cancels, () => ++cancelDepartures);
        var session = new SimBriefSession(_ => answers[Math.Min(Interlocked.Increment(ref fetches), answers.Length) - 1](), [target], [ground],
            () => null, radiusKm: 10, Clock, run: _ =>
            {
                starts++;
                return Task.CompletedTask;
            });
        return (session, () => Volatile.Read(ref fetches), departure, () => (cancels, cancelDepartures, starts));
    }

    private static CameraReport ParkedAt(FlightOrigin airport, double seconds) =>
        new(airport.Lat, airport.Lon, 3, null, null, false, new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc).AddSeconds(seconds));

    [Fact]
    public async Task A_reloaded_plan_from_elsewhere_drops_the_old_one_prefetches_its_departure_and_watches_its_gate()
    {
        var (session, fetches, departure, counts) = Session(() => Task.FromResult(Answer(Egll, Eddm, "1")), () => Task.FromResult(Answer(Eddm, Egll, "2")));
        session.Start();
        await session.WhenDone();
        Assert.Empty(departure); // EGLL: this package has nothing there
        session.OnCamera(ParkedAt(Egll, 0));
        session.OnCamera(ParkedAt(Egll, 31));
        Assert.Equal((0, 0, 1), counts()); // the old plan's pre-warm ran

        string said = await session.ReloadAsync();

        Assert.StartsWith("Loaded EDDM → EGLL", said, StringComparison.Ordinal);
        Assert.Equal((1, 1), (counts().Cancel, counts().CancelDeparture)); // the old plan's requests went
        Assert.Equal([AtAirport.Stem, Around.Stem], Stems(departure)); // the new departure is built
        Assert.Equal("2", session.Current!.Id);
        session.OnCamera(ParkedAt(Eddm, 100));
        session.OnCamera(ParkedAt(Eddm, 131));
        Assert.Equal(2, counts().Starts); // watched from the start for the new gate
        Assert.Equal(2, fetches());
    }

    [Fact]
    public async Task Reloading_the_same_plan_changes_nothing()
    {
        var (session, fetches, departure, counts) = Session(() => Task.FromResult(Answer(Eddm, Egll, "7")));
        session.Start();
        await session.WhenDone();
        departure.Clear();

        string said = await session.ReloadAsync();

        Assert.StartsWith("Unchanged: EDDM → EGLL", said, StringComparison.Ordinal);
        Assert.Equal((0, 0, 0), counts());
        Assert.Empty(departure);
        Assert.Equal(2, fetches());
    }

    [Fact]
    public async Task A_fresh_plan_from_the_same_airport_as_a_stale_one_gets_its_departure_built()
    {
        // Mounted with yesterday's OFP (departure skipped as too old), then a new one generated at the same airport.
        SimBriefAnswer Dated(string id, TimeSpan age) => new(new FlightPlan(Eddm, Egll, null, [], Now - age, id), null);
        var (session, _, departure, _) = Session(() => Task.FromResult(Dated("1", TimeSpan.FromHours(29))),
            () => Task.FromResult(Dated("2", TimeSpan.FromMinutes(1))));
        session.Start();
        await session.WhenDone();
        Assert.Empty(departure); // the stale plan's departure is not prefetched

        string said = await session.ReloadAsync();

        Assert.EndsWith("Its departure is being built.", said, StringComparison.Ordinal);
        Assert.Equal([AtAirport.Stem, Around.Stem], Stems(departure));
    }

    [Fact]
    public async Task A_new_plan_from_a_reload_starts_the_download_limit_again_and_the_same_plan_does_not()
    {
        // A turnaround: the first flight spent the whole limit, then the pilot plans the way back and reloads.
        var budget = new PrefetchBudget(100);
        SimBriefAnswer[] answers = [Answer(Eddm, Egll, "1"), Answer(Eddm, Egll, "1"), Answer(Egll, Eddm, "2")];
        int fetches = 0;
        var session = new SimBriefSession(_ => Task.FromResult(answers[Math.Min(fetches++, answers.Length - 1)]), [], [], () => null,
            radiusKm: 10, Clock, budget: budget);
        await session.FirstPlan;
        budget.Spend(100);

        await session.ReloadAsync(); // unchanged: still spent
        Assert.True(budget.Exhausted);

        Assert.StartsWith("Loaded EGLL → EDDM", await session.ReloadAsync(), StringComparison.Ordinal);
        Assert.Equal(0, budget.SpentBytes);
        Assert.Equal(1, budget.Round);
    }

    [Fact]
    public async Task Two_presses_during_one_reload_send_one_request()
    {
        var held = new TaskCompletionSource<SimBriefAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var (session, fetches, _, _) = Session(() => Task.FromResult(Answer(Eddm, Egll, "1")), () => held.Task);
        session.Start();
        await session.FirstPlan;

        Task<string> first = session.ReloadAsync(), second = session.ReloadAsync();
        Assert.Same(first, second);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        held.SetResult(Answer(Eddm, Egll, "1"));
        await first;

        Assert.Equal(2, fetches()); // the start's and one reload
    }

    [Fact]
    public async Task A_plan_generated_after_streaming_started_is_found_by_the_reload()
    {
        // The common case: OrthoStreamXP started first, the OFP generated afterwards.
        var (session, _, departure, _) = Session(() => Task.FromResult(SimBriefAnswer.Failed("no flight plan yet")),
            () => Task.FromResult(Answer(Eddm, Egll, "3")));
        session.Start();
        await session.WhenDone();
        Assert.Empty(departure);

        Assert.StartsWith("Loaded EDDM → EGLL", await session.ReloadAsync(), StringComparison.Ordinal);
        Assert.NotEmpty(departure);
    }

    [Theory]
    [InlineData(null, "1000", 5)]
    [InlineData(3000, "3000", 5)]
    [InlineData(1500, "1500", 6)] // set by hand in the file: kept, offered first
    public void The_setup_offers_the_download_limits_and_keeps_one_set_by_hand(int? current, string first, int count)
    {
        IReadOnlyList<OrthoStreamXP.Gui.Choice> choices = OrthoStreamXP.Gui.SimBriefText.LimitChoices(current);

        Assert.Equal(count, choices.Count);
        Assert.Contains(choices, c => c.Value == (current?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "1000"));
        Assert.Contains(choices, c => c.Value == "0"); // no limit
        if (count == 6) Assert.Equal(first, choices[0].Value);
    }

    [Fact]
    public void No_flight_plan_queues_nothing()
    {
        var prefetch = new DeparturePrefetch(() => null,
            [new DepartureTarget("pkg", Package(AtAirport), _ => false, _ => throw new InvalidOperationException("nothing to queue"))], radiusKm: 10);

        Assert.Equal(0, prefetch.Run(null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void With_streaming_the_whole_area_XPlane_loads_is_queued_and_without_its_DSFs_the_circle()
    {
        string package = Package(AtAirport, Around, FarAway);
        _tree.WriteBytes("pkg/Earth nav data/+40+010/+48+011.dsf", SceneryAreaTests.Dsf($"terrain/{AtAirport.Stem}.ter", $"terrain/{FarAway.Stem}.ter"));
        var area = new List<VirtualPath>();
        var whole = new List<VirtualPath>();
        DepartureTarget Target(string source) => new("pkg", source, _ => false, t => { whole.AddRange(t); return t.Count; },
            (textures, _, _) => { area.AddRange(textures); return (textures.Count - 1, 1); });
        CancellationToken ct = TestContext.Current.CancellationToken;

        int queued = new DeparturePrefetch(() => null, [Target(package)], 10).Run(PlanFrom(new FlightOrigin("EDDM", Lat, Lon)), ct);
        Assert.Equal(1, queued);
        Assert.Equal([AtAirport.Stem, FarAway.Stem], Stems(area)); // as the DSF names them, far ones too
        Assert.Empty(whole);

        // A package without its DSFs there (or 7-Zip ones): the textures within the radius, whole.
        Directory.Delete(_tree.PathOf("pkg/Earth nav data"), recursive: true);
        area.Clear();
        new DeparturePrefetch(() => null, [Target(package)], 10).Run(PlanFrom(new FlightOrigin("EDDM", Lat, Lon)), ct);
        Assert.Empty(area);
        Assert.Equal([AtAirport.Stem, Around.Stem], Stems(whole));
    }

    // ---------------------------------------------------------------- the settings

    [Fact]
    public void The_settings_are_off_by_default_and_the_radius_is_checked()
    {
        MountSettings Parse(params string[] extra)
        {
            Directory.CreateDirectory(_tree.PathOf("source"));
            return MountSettings.FromValues(TestSettings.Of(
                [$"source = {_tree.PathOf("source")}", $"cache = {_tree.PathOf("cache")}", $"mountpoint = {_tree.PathOf("mount")}", "texture-provider = bing", .. extra]));
        }

        Assert.Null(Parse().SimBriefUser);
        Assert.Equal(MountSettings.DefaultSimBriefRadiusKm, Parse().SimBriefRadiusKm);
        Assert.Equal("pilot", Parse("simbrief-user = pilot ", "simbrief-radius-km = 20").SimBriefUser);
        Assert.Equal(20, Parse("simbrief-radius-km = 20").SimBriefRadiusKm);
        Assert.Throws<SettingsException>(() => Parse("simbrief-radius-km = 0"));
        Assert.Throws<SettingsException>(() => Parse("simbrief-radius-km = 51"));

        // R4: the download budget, 1000 MB unless set; 0 is no limit.
        Assert.Equal(MountSettings.DefaultSimBriefDownloadLimitMb, Parse().SimBriefDownloadLimitMb);
        Assert.Equal(1000, MountSettings.DefaultSimBriefDownloadLimitMb);
        Assert.Equal(0, Parse("simbrief-download-limit = 0").SimBriefDownloadLimitMb);
        Assert.Equal(250, Parse("simbrief-download-limit = 250").SimBriefDownloadLimitMb);
        Assert.Throws<SettingsException>(() => Parse("simbrief-download-limit = -1"));

        // The alternate: off unless switched on.
        Assert.False(Parse().SimBriefAlternate);
        Assert.True(Parse("simbrief-alternate = on").SimBriefAlternate);
        Assert.Throws<SettingsException>(() => Parse("simbrief-alternate = maybe"));
    }
}
