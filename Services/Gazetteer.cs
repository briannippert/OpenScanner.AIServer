using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpenScanner.AIServer.Services;

/// <summary>A named street. Each segment is west, south, east, north, then the
/// latitude and longitude of a mapped point near the middle of that segment.
/// Junctions maps another street's name to the points the two streets share.</summary>
public sealed record GazetteerStreet(string Name, double[][] Segments, Dictionary<string, double[][]> Junctions);
public sealed record GazetteerPlace(string Name, double Latitude, double Longitude,
    string? HouseNumber, string? Street, string? PostalCode);
public sealed record GazetteerData(int Version, string City, string State, DateTime FetchedAtUtc,
    GazetteerStreet[] Streets, GazetteerPlace[] Places);
public sealed record GazetteerCity(string City, string State, int Streets, int Places, DateTime FetchedAt);

/// <summary>Every named street and business of one city, searchable in memory.</summary>
public sealed class CityIndex
{
    private const int MaxPlaceMatches = 20;
    private readonly (string Key, string[] Words, GazetteerPlace Place)[] places;

    public CityIndex(GazetteerData data)
    {
        Data = data;
        places = data.Places.Select(p => (Key: PlaceKey(p.Name), Place: p)).Where(p => p.Key.Length > 0)
            .Select(p => (p.Key, p.Key.Split(' '), p.Place)).ToArray();
    }

    public GazetteerData Data { get; }

    /// <summary>Streets whose name is the input, or a bounded spelling or
    /// sounds-alike variation of it. Callers decide how much ambiguity to accept.</summary>
    public List<GazetteerStreet> FindStreets(string input)
    {
        var similar = Data.Streets.Where(s => StreetGeocoder.Similar(input, s.Name)).ToList();
        if (similar.Count > 0) return similar;
        // Pronunciation is a looser test, so it is only consulted when spelling finds
        // nothing, and then only for the closest-spelled of the streets that fit.
        var alike = Data.Streets.Where(s => StreetGeocoder.SoundsAlike(input, s.Name))
            .Select(s => (Street: s, Distance: StreetGeocoder.Distance(StreetGeocoder.Normalize(input), StreetGeocoder.Normalize(s.Name)))).ToList();
        return alike.Where(a => a.Distance == alike.Min(m => m.Distance)).Select(a => a.Street).ToList();
    }

    /// <summary>Exact name matches when there are any; otherwise close ones,
    /// which the caller must verify before using.</summary>
    public (List<GazetteerPlace> Matches, bool Exact) FindPlaces(string name)
    {
        var key = PlaceKey(name);
        if (key.Length == 0) return ([], false);
        var exact = places.Where(p => p.Key == key).Select(p => p.Place).ToList();
        if (exact.Count > 0) return (exact.Take(MaxPlaceMatches).ToList(), true);
        var words = key.Split(' ');
        var compact = key.Replace(" ", "");
        var close = new List<(int Rank, GazetteerPlace Place)>();
        foreach (var place in places)
        {
            var other = place.Key.Replace(" ", "");
            // "Market Basket" for "Market Basket Supermarket", or the reverse.
            var contained = Math.Min(compact.Length, other.Length) >= 4 &&
                (words.All(place.Words.Contains) || place.Words.All(words.Contains));
            var distance = StreetGeocoder.Distance(compact, other);
            if (contained) close.Add((Math.Abs(words.Length - place.Words.Length), place.Place));
            else if (distance <= Math.Min(2, Math.Min(compact.Length, other.Length) / 4) ||
                (compact.Length >= 4 && distance <= 3 && Math.Abs(compact.Length - other.Length) <= 2 &&
                    StreetGeocoder.Phonetic(key) == StreetGeocoder.Phonetic(place.Key)))
                close.Add((10 + distance, place.Place));
        }
        return (close.OrderBy(c => c.Rank).Take(MaxPlaceMatches).Select(c => c.Place).ToList(), false);
    }

    internal static string PlaceKey(string name) => string.Join(" ",
        Regex.Matches(name.ToLowerInvariant().Replace("'", "").Replace("’", ""), @"[\p{L}\p{N}]+")
            .Select(m => m.Value).Where(w => w != "the"));
}

/// <summary>
/// Downloads every named street and business of a city from OpenStreetMap once,
/// keeps it on disk, and serves lookups from memory. Lookups never wait for a
/// download: until a city is loaded callers use their live search instead.
/// </summary>
public sealed class Gazetteer(IHttpClientFactory clients, IConfiguration config, UpstreamThrottle throttle,
    IHostEnvironment environment, ILogger<Gazetteer> logger) : IHostedService, IDisposable
{
    public const string Provider = "OpenStreetMap gazetteer";
    private const int FormatVersion = 1;
    private const int MaxCities = 25;
    private const int MaxPending = 4;
    private const long MaxResponseBytes = 128L * 1024 * 1024;
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RetryAfterNotFound = TimeSpan.FromHours(24);

    private sealed class Entry
    {
        public volatile CityIndex? Index;
        public DateTime NextAttemptUtc;
        public bool Loading;
        public long LastUsed;
    }

    private readonly ConcurrentDictionary<string, Entry> entries = new();
    private readonly SemaphoreSlim downloads = new(1, 1);
    private readonly CancellationTokenSource stopping = new();
    private int pending;

    public bool Enabled => config.GetValue("Geocoding:Gazetteer:Enabled", true);
    private TimeSpan RefreshAfter => TimeSpan.FromDays(Math.Clamp(config.GetValue("Geocoding:Gazetteer:RefreshDays", 30), 1, 365));

    public Task StartAsync(CancellationToken cancellationToken)
    {
        TryGet(config["Geocoding:DefaultCity"] ?? "Salem", config["Geocoding:DefaultState"] ?? "NH");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        stopping.Cancel();
        return Task.CompletedTask;
    }

    public CityIndex? TryGet(string? city, string? state)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(city) || string.IsNullOrWhiteSpace(state)) return null;
        var code = Geo.StateCode(state).ToUpperInvariant();
        var name = city.Trim();
        if (code.Length != 2 || !code.All(char.IsAsciiLetter) || name.Length > 100 || name.Any(char.IsControl))
            return null;
        var entry = entries.GetOrAdd(code + "|" + name.ToUpperInvariant(), _ => new Entry());
        entry.LastUsed = Environment.TickCount64;
        lock (entry)
        {
            var now = DateTime.UtcNow;
            if (!entry.Loading && now >= entry.NextAttemptUtc && !stopping.IsCancellationRequested &&
                (entry.Index is null || now - entry.Index.Data.FetchedAtUtc > RefreshAfter) &&
                Volatile.Read(ref pending) < MaxPending)
            {
                entry.Loading = true;
                // Also bounds retries: a failed load is not attempted again on the next request.
                entry.NextAttemptUtc = now + RetryAfterFailure;
                Interlocked.Increment(ref pending);
                _ = Task.Run(() => LoadAsync(entry, name, code));
            }
        }
        Evict(entry);
        return entry.Index;
    }

    public IReadOnlyList<GazetteerCity> Status() => entries.Values.Select(e => e.Index?.Data).OfType<GazetteerData>()
        .Select(d => new GazetteerCity(d.City, d.State, d.Streets.Length, d.Places.Length, d.FetchedAtUtc))
        .OrderBy(c => c.State).ThenBy(c => c.City).ToList();

    private void Evict(Entry keep)
    {
        while (entries.Count > MaxCities)
        {
            var oldest = entries.Where(e => e.Value != keep && !e.Value.Loading).OrderBy(e => e.Value.LastUsed).FirstOrDefault();
            // An evicted city stays on disk and is reloaded from there when next used.
            if (oldest.Key is null || !entries.TryRemove(oldest.Key, out _)) return;
        }
    }

    private async Task LoadAsync(Entry entry, string city, string code)
    {
        try
        {
            var ct = stopping.Token;
            var path = FilePath(city, code);
            if (entry.Index is null && File.Exists(path))
            {
                try
                {
                    await using var file = File.OpenRead(path);
                    var saved = await JsonSerializer.DeserializeAsync<GazetteerData>(file, cancellationToken: ct);
                    if (saved is { Version: FormatVersion, Streets: not null, Places: not null })
                    {
                        entry.Index = new CityIndex(saved);
                        logger.LogInformation("Gazetteer for {City}, {State} loaded from disk: {Streets} streets, {Places} places",
                            city, code, saved.Streets.Length, saved.Places.Length);
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("Ignoring unreadable gazetteer file {Path} ({FailureType})", path, ex.GetType().Name);
                }
            }
            if (entry.Index is { } current && DateTime.UtcNow - current.Data.FetchedAtUtc <= RefreshAfter) return;

            GazetteerData? data;
            await downloads.WaitAsync(ct);
            try { data = await DownloadAsync(city, code, ct); }
            finally { downloads.Release(); }
            if (data is null)
            {
                lock (entry) entry.NextAttemptUtc = DateTime.UtcNow + RetryAfterNotFound;
                logger.LogWarning("OpenStreetMap has no boundary or no named streets for {City}, {State}; using live search there", city, code);
                return;
            }
            entry.Index = new CityIndex(data);
            logger.LogInformation("Gazetteer for {City}, {State} downloaded: {Streets} streets, {Places} places",
                city, code, data.Streets.Length, data.Places.Length);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllBytesAsync(temp, JsonSerializer.SerializeToUtf8Bytes(data), ct);
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Could not save the gazetteer to {Path} ({FailureType}); it will be downloaded again after a restart",
                    path, ex.GetType().Name);
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // A stale or missing gazetteer only means callers keep using live search.
            logger.LogWarning("Gazetteer download for {City}, {State} failed ({FailureType}: {Message}); retrying later",
                city, code, ex.GetType().Name, ex.Message);
        }
        finally
        {
            lock (entry) entry.Loading = false;
            Interlocked.Decrement(ref pending);
        }
    }

    private string FilePath(string city, string code)
    {
        var dir = config["Geocoding:Gazetteer:CacheDir"];
        if (string.IsNullOrWhiteSpace(dir))
        {
            var home = Environment.GetEnvironmentVariable("OPENSCANNER_AI_HOME");
            dir = Path.Combine(string.IsNullOrWhiteSpace(home) ? environment.ContentRootPath : Path.GetFullPath(home),
                "data", "gazetteer");
        }
        var slug = string.Join("-", Regex.Matches(city.ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(m => m.Value));
        return Path.Combine(dir, $"{code.ToLowerInvariant()}-{slug}.json");
    }

    internal static string Query(string city, string code)
    {
        // The name is matched as a case-insensitive regular expression inside a quoted string.
        var name = Regex.Replace(city, @"[.^$*+?()\[\]{}|\\]", @"\$&").Replace(@"\", @"\\").Replace("\"", "\\\"");
        return $$"""
            [out:json][timeout:120];
            area["ISO3166-2"="US-{{code}}"]->.state;
            (
              rel(area.state)["boundary"="administrative"]["admin_level"~"^([6-9]|10)$"]["name"~"^{{name}}$",i];
              rel(area.state)["boundary"="census"]["name"~"^{{name}}$",i];
            );
            map_to_area->.city;
            way(area.city)["highway"]["name"]["highway"!~"^(footway|path|cycleway|steps|pedestrian|track|bridleway|corridor|proposed|construction|platform|rest_area|services)$"];
            out geom;
            nwr(area.city)["name"][~"^(amenity|shop|office|leisure|tourism|healthcare|emergency|craft)$"~"."];
            out tags center;
            """;
    }

    private async Task<GazetteerData?> DownloadAsync(string city, string code, CancellationToken ct)
    {
        var endpoint = config["Geocoding:Gazetteer:Endpoint"] ?? "https://overpass-api.de/api/interpreter";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Geocoding:Gazetteer:TimeoutSeconds", 180), 1, 900)));
        ct = deadline.Token;
        await throttle.WaitTurnAsync(endpoint, ct);
        using var client = clients.CreateClient("geocoding");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("data", Query(city, code))])
        };
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        using var body = new MemoryStream();
        await using (var stream = await response.Content.ReadAsStreamAsync(ct))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                if (body.Length + read > MaxResponseBytes) throw new JsonException("Gazetteer response is too large.");
                body.Write(buffer, 0, read);
            }
        }
        body.Position = 0;
        using var payload = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        return Build(city, code, payload.RootElement, DateTime.UtcNow);
    }

    /// <summary>Turns an Overpass response into a city's streets, junctions and places.
    /// Returns null when the city was not found.</summary>
    internal static GazetteerData? Build(string city, string code, JsonElement root, DateTime fetchedAtUtc)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("elements", out var elements) ||
            elements.ValueKind != JsonValueKind.Array)
            throw new JsonException("Missing gazetteer elements.");
        // Overpass reports a query that ran out of time or memory in a remark on a normal response.
        if (Geo.Text(root, "remark") is { } remark && remark.Contains("error", StringComparison.OrdinalIgnoreCase))
            throw new JsonException("Gazetteer query did not complete: " + remark);

        var segments = new Dictionary<string, List<double[]>>(StringComparer.OrdinalIgnoreCase);
        var displayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nodes = new Dictionary<long, (double Lat, double Lon, HashSet<string> Streets)>();
        var places = new Dictionary<(string, double, double), GazetteerPlace>();
        foreach (var element in elements.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("tags", out var tags) ||
                tags.ValueKind != JsonValueKind.Object || Geo.Text(tags, "name")?.Trim() is not { Length: > 0 and <= 200 } name)
                continue;
            if (Geo.Text(element, "type") == "way" && Geo.Text(tags, "highway") is not null &&
                element.TryGetProperty("geometry", out var geometry) && geometry.ValueKind == JsonValueKind.Array)
            {
                var points = new List<(double Lat, double Lon)>();
                foreach (var point in geometry.EnumerateArray())
                    if (Coordinates(point, out var lat, out var lon)) points.Add((lat, lon));
                    else { points.Clear(); break; }
                if (points.Count < 2) continue;
                displayNames.TryAdd(name, name);
                name = displayNames[name];
                if (!segments.TryGetValue(name, out var list)) segments[name] = list = [];
                var middle = points[points.Count / 2];
                list.Add([points.Min(p => p.Lon), points.Min(p => p.Lat), points.Max(p => p.Lon), points.Max(p => p.Lat),
                    middle.Lat, middle.Lon]);
                if (!element.TryGetProperty("nodes", out var ids) || ids.ValueKind != JsonValueKind.Array ||
                    ids.GetArrayLength() != points.Count) continue;
                var i = 0;
                foreach (var id in ids.EnumerateArray())
                {
                    var at = points[i++];
                    if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt64(out var node)) continue;
                    if (!nodes.TryGetValue(node, out var shared))
                        nodes[node] = shared = (at.Lat, at.Lon, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                    shared.Streets.Add(name);
                }
            }
            else
            {
                var at = element.TryGetProperty("center", out var center) && center.ValueKind == JsonValueKind.Object ? center : element;
                if (!Coordinates(at, out var lat, out var lon)) continue;
                places.TryAdd((name.ToUpperInvariant(), Math.Round(lat, 5), Math.Round(lon, 5)), new GazetteerPlace(name, lat, lon,
                    Geo.Text(tags, "addr:housenumber"), Geo.Text(tags, "addr:street"), Geo.Text(tags, "addr:postcode")));
            }
        }
        if (segments.Count == 0) return null;

        var junctions = segments.Keys.ToDictionary(n => n, _ => new Dictionary<string, List<double[]>>(), StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodes.Values.Where(n => n.Streets.Count > 1))
            foreach (var a in node.Streets)
                foreach (var b in node.Streets)
                {
                    if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!junctions[a].TryGetValue(b, out var shared)) junctions[a][b] = shared = [];
                    if (shared.Count < 8) shared.Add([node.Lat, node.Lon]);
                }
        var streets = segments.OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase).Select(s => new GazetteerStreet(s.Key,
            s.Value.ToArray(), junctions[s.Key].ToDictionary(j => j.Key, j => j.Value.ToArray()))).ToArray();
        return new GazetteerData(FormatVersion, city, code, fetchedAtUtc, streets,
            places.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static bool Coordinates(JsonElement value, out double lat, out double lon)
    {
        lat = lon = 0;
        return value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("lat", out var y) && y.ValueKind == JsonValueKind.Number && y.TryGetDouble(out lat) &&
            value.TryGetProperty("lon", out var x) && x.ValueKind == JsonValueKind.Number && x.TryGetDouble(out lon) &&
            double.IsFinite(lat) && double.IsFinite(lon) && lat is >= -90 and <= 90 && lon is >= -180 and <= 180;
    }

    public void Dispose()
    {
        stopping.Dispose();
        downloads.Dispose();
    }
}
