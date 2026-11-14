using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace OpenScanner.AIServer.Services;

// The gazetteer (or Photon, until a city is loaded) supplies street names, never
// house coordinates. Census must validate the unchanged house number on a unique,
// similar street in the requested locality.
public sealed class StreetGeocoder(IHttpClientFactory clients, IConfiguration config,
    AddressGeocoder census, UpstreamThrottle throttle, Gazetteer gazetteer) : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 1000 });
    private sealed record Road(string Name, double West, double South, double East, double North,
        double? Longitude = null, double? Latitude = null);
    private static readonly Dictionary<string, string> Suffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["avenue"] = "ave", ["street"] = "st", ["road"] = "rd", ["drive"] = "dr",
        ["lane"] = "ln", ["court"] = "ct", ["circle"] = "cir", ["boulevard"] = "blvd",
        ["terrace"] = "ter", ["place"] = "pl", ["highway"] = "hwy", ["parkway"] = "pkwy",
        ["north"] = "n", ["south"] = "s", ["east"] = "e", ["west"] = "w"
    };

    public async Task<GeocodeMatch?> ResolveCenterAsync(string street, string city, string state, CancellationToken ct)
    {
        if (street.Length > 150 || city.Length > 100 || state.Length > 100) return null;
        var key = "center:" + JsonSerializer.Serialize(new[] { street, city, state }).ToUpperInvariant();
        if (cache.TryGetValue<GeocodeResponse>(key, out var cached)) return cached!.SelectedMatch;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Geo.Timeout(config));
        ct = deadline.Token;
        // Approximate pins require the actual named street, not a fuzzy alternative.
        var local = gazetteer.TryGet(city, state) is not null;
        var found = await FindRoadsAsync(street, city, state, ct);
        var roads = found.Where(r => SameStreet(r.Name, street) &&
            r.Longitude.HasValue && r.Latitude.HasValue).ToList();
        if (roads.Count == 0 && local)
        {
            // Dispatchers drop the road type ("16 Garabedian"). With the city's whole
            // street list in hand, a single street completing the name is not a guess.
            var completed = found.Where(r => CompletesStreetSuffix(street, r.Name)).Select(r => r.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (completed.Count == 1)
                roads = found.Where(r => Same(r.Name, completed[0]) && r.Longitude.HasValue && r.Latitude.HasValue).ToList();
        }
        var provider = local ? Gazetteer.Provider : "Photon / OpenStreetMap";
        GeocodeMatch? match = null;
        if (roads.Count > 0)
        {
            var west = roads.Min(r => r.West); var east = roads.Max(r => r.East);
            var south = roads.Min(r => r.South); var north = roads.Max(r => r.North);
            if (east - west <= .1 && north - south <= .1)
            {
                var x = (west + east) / 2; var y = (south + north) / 2;
                // Choose a provider's mapped point near the center of the available
                // street segments, rather than inventing a numbered address/intersection.
                var road = roads.OrderBy(r => Math.Pow((r.Longitude!.Value - x) * Math.Cos(y * Math.PI / 180), 2) +
                    Math.Pow(r.Latitude!.Value - y, 2)).First();
                match = new GeocodeMatch($"{road.Name}, {city}, {StateCode(state)}", road.Latitude!.Value,
                    road.Longitude!.Value, Street: road.Name, City: city, State: StateCode(state), Country: "US",
                    Attribution: Geo.OsmAttribution,
                    Precision: "street", Provider: provider);
            }
        }
        var result = new GeocodeResponse(match is not null, match is null ? [] : [match], provider,
            match is null ? null : 0, match is null ? "no_match" : "approximate_street");
        cache.Set(key, result, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24) });
        return match;
    }

    public async Task<GeocodeMatch?> ResolveAsync(string house, string street, string city,
        string state, string? crossStreet, CancellationToken ct)
    {
        if (!Regex.IsMatch(house, @"^\d+[A-Za-z]?(?:-\d+)?$") ||
            street.Length > 150 || city.Length > 100 || state.Length > 100 || crossStreet?.Length > 300)
            return null;
        var crosses = SplitCrossStreets(crossStreet);
        if (crosses.Length > 2) return null;
        var key = JsonSerializer.Serialize(new[] { house, street, city, state, crossStreet }).ToUpperInvariant();
        if (cache.TryGetValue<GeocodeResponse>(key, out var cached)) return cached!.SelectedMatch;
        // This chain makes several throttled calls (up to three Photon searches per street,
        // then Census), so it gets its own budget scaled to the work, not a single call's.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Geo.Timeout(config) * (2 + crosses.Length));
        ct = deadline.Token;
        var candidates = await FindRoadsAsync(street, city, state, ct);
        // Bound both remote work and ambiguity. Never take the first search result.
        if (candidates.Select(r => Normalize(r.Name)).Distinct().Count() > 5) return null;
        foreach (var cross in crosses)
        {
            var nearby = await FindRoadsAsync(cross, city, state, ct);
            // A named cross street must have local supporting evidence. Bounding boxes
            // establish proximity only, not a verified road intersection.
            candidates = candidates.Where(r => nearby.Any(n => Near(r, n))).ToList();
            if (candidates.Count == 0) break;
        }
        var names = candidates.Select(r => r.Name).DistinctBy(Normalize).ToArray();
        var matches = new List<GeocodeMatch>();
        foreach (var name in names)
        {
            var located = await census.GeocodeAsync($"{house} {name}, {city}, {state}", ct);
            foreach (var match in located.Matches)
            {
                // Census occasionally supplies a different nearby address. Require the
                // same number, street, city and state, not merely successful geocoding.
                var parts = match.MatchedAddress.Split(',').Select(s => s.Trim()).ToArray();
                if (parts.Length < 3 || Normalize(parts[0]) != Normalize($"{house} {name}") ||
                    !Same(parts[1], city) || !SameState(parts[2], state)) continue;
                matches.Add(match with { HouseNumber = house, Street = name, City = city,
                    State = StateCode(state), Country = "US",
                    Attribution = "US Census Bureau; street names " + Geo.OsmAttribution });
            }
        }
        matches = matches.DistinctBy(m => m.MatchedAddress.ToUpperInvariant()).ToList();
        var result = new GeocodeResponse(matches.Count > 0, matches, "Photon street search / US Census Bureau",
            matches.Count == 1 ? 0 : null, matches.Count == 1 ? "verified_street_fallback" : "ambiguous");
        cache.Set(key, result, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24) });
        return result.SelectedMatch;
    }

    private static string[] SplitCrossStreets(string? crossStreet) => string.IsNullOrWhiteSpace(crossStreet) ? [] :
        Regex.Split(crossStreet, @"\s+(?:and|at)\s+|\s*[&/]\s*", RegexOptions.IgnoreCase)
            .Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// The corner of two streets, from the gazetteer only. Each spoken name may fit
    /// several streets ("Broadway" for North and South Broadway); a pin is returned
    /// only when every pair of them that actually meets does so at the same place.
    /// </summary>
    public GeocodeMatch? ResolveIntersection(string street, string crossStreet, string city, string state)
    {
        if (street.Length > 150 || crossStreet.Length > 300 || city.Length > 100 || state.Length > 100) return null;
        var crosses = SplitCrossStreets(crossStreet);
        if (crosses.Length != 1 || gazetteer.TryGet(city, state) is not { } index) return null;
        var corners = (from a in IntersectionCandidates(index, street)
                       from b in IntersectionCandidates(index, crosses[0])
                       where !Same(a.Name, b.Name) && a.Junctions.ContainsKey(b.Name)
                       select (a, b, Points: a.Junctions[b.Name])).ToList();
        var points = corners.SelectMany(c => c.Points).ToList();
        if (points.Count == 0) return null;
        var (first, second, _) = corners[0];
        var lat = points.Average(p => p[0]);
        var lon = points.Average(p => p[1]);
        // A divided road meets a cross street at two nearby points; streets that
        // meet twice far apart do not identify one corner.
        if (points.Any(p => Math.Sqrt(Math.Pow((p[0] - lat) * 111_000, 2) +
            Math.Pow((p[1] - lon) * 111_000 * Math.Cos(lat * Math.PI / 180), 2)) > 150)) return null;
        return new GeocodeMatch($"{first.Name} & {second.Name}, {city}, {StateCode(state)}", lat, lon,
            Street: first.Name, City: city, State: StateCode(state), Country: "US",
            Attribution: Geo.OsmAttribution, Precision: "intersection", Provider: Gazetteer.Provider);
    }

    private static List<GazetteerStreet> IntersectionCandidates(CityIndex index, string name)
    {
        var close = index.FindStreets(name);
        var named = close.Where(s => SameStreet(name, s.Name) || CompletesStreetSuffix(name, s.Name)).ToList();
        if (named.Count == 0)
        {
            // "Broadway" for "North Broadway": a dropped direction, tolerated here
            // because the two streets must also really intersect.
            named = index.Data.Streets.Where(s =>
            {
                var words = Normalize(s.Name).Split(' ');
                if (words.Length < 2 || words[0] is not ("n" or "s" or "e" or "w")) return false;
                var rest = string.Join(' ', words[1..]);
                return SameStreet(name, rest) || CompletesStreetSuffix(name, rest);
            }).ToList();
        }
        var candidates = named.Count > 0 ? named : close;
        return candidates.Count <= 5 ? candidates : [];
    }

    private async Task<List<Road>> FindRoadsAsync(string street, string city, string state, CancellationToken ct)
    {
        if (gazetteer.TryGet(city, state) is { } index)
            // Same shape as the live search: one entry per mapped segment, broad ones dropped.
            return index.FindStreets(street).SelectMany(s => s.Segments
                .Where(g => g.Length == 6 && g[2] - g[0] <= .1 && g[3] - g[1] <= .1)
                .Select(g => new Road(s.Name, g[0], g[1], g[2], g[3], g[5], g[4]))).ToList();
        var words = Regex.Matches(street, @"[\p{L}\p{N}]+").Select(m => m.Value).ToArray();
        if (words.Length == 0) return [];
        // Prefix the main name, retaining any directional and road-type tokens.
        var nameIndex = words.Length > 1 && Normalize(words[0]) is "n" or "s" or "e" or "w" ? 1 : 0;
        var shortWords = (string[])words.Clone();
        shortWords[nameIndex] = shortWords[nameIndex][..Math.Min(4, shortWords[nameIndex].Length)];
        var searches = new[] { street, string.Join(" ", shortWords), shortWords[nameIndex] }
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var roads = new List<Road>();
        foreach (var search in searches)
        {
            var url = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
                config["Geocoding:StreetEndpoint"] ?? "https://photon.komoot.io/api/",
                new Dictionary<string, string?> { ["q"] = $"{search}, {city}, {state}, USA",
                    ["limit"] = "10", ["lang"] = "en", ["osm_tag"] = "highway" });
            using var payload = await throttle.GetJsonAsync<JsonDocument>(clients, url, ct);
            if (payload?.RootElement.ValueKind != JsonValueKind.Object ||
                !payload.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
                throw new JsonException("Missing street search results.");
            foreach (var feature in features.EnumerateArray())
            {
                if (feature.ValueKind != JsonValueKind.Object ||
                    !feature.TryGetProperty("properties", out var p) || p.ValueKind != JsonValueKind.Object)
                    throw new JsonException("Invalid street result.");
                var name = Text(p, "name");
                if (!Same(Text(p, "countrycode"), "US") || !Same(Text(p, "city"), city) ||
                    !SameState(Text(p, "state"), state) || Text(p, "type") != "street" ||
                    name is null || !Similar(street, name)) continue;
                if (!p.TryGetProperty("extent", out var extent) || extent.ValueKind != JsonValueKind.Array || extent.GetArrayLength() != 4)
                    continue;
                var bounds = new double[4];
                for (var i = 0; i < 4; i++)
                    if (extent[i].ValueKind != JsonValueKind.Number || !extent[i].TryGetDouble(out bounds[i]) || !double.IsFinite(bounds[i]))
                        throw new JsonException("Invalid street bounds.");
                // Photon extent is west, north, east, south. Reject broad/invalid boxes.
                if (bounds[0] is < -180 or > 180 || bounds[2] is < -180 or > 180 ||
                    bounds[1] is < -90 or > 90 || bounds[3] is < -90 or > 90 ||
                    bounds[0] > bounds[2] || bounds[3] > bounds[1] ||
                    bounds[2] - bounds[0] > .1 || bounds[1] - bounds[3] > .1) continue;
                double? longitude = null, latitude = null;
                if (feature.TryGetProperty("geometry", out var geometry) && geometry.ValueKind == JsonValueKind.Object &&
                    Text(geometry, "type") == "Point" && geometry.TryGetProperty("coordinates", out var xy) &&
                    xy.ValueKind == JsonValueKind.Array && xy.GetArrayLength() == 2 &&
                    xy[0].ValueKind == JsonValueKind.Number && xy[0].TryGetDouble(out var lon) && double.IsFinite(lon) &&
                    xy[1].ValueKind == JsonValueKind.Number && xy[1].TryGetDouble(out var lat) && double.IsFinite(lat) &&
                    lon >= bounds[0] && lon <= bounds[2] && lat >= bounds[3] && lat <= bounds[1])
                { longitude = lon; latitude = lat; }
                roads.Add(new Road(name, bounds[0], bounds[3], bounds[2], bounds[1], longitude, latitude));
            }
        }
        return roads;
    }

    private static bool Near(Road a, Road b) => a.West <= b.East + .0003 && a.East + .0003 >= b.West &&
        a.South <= b.North + .0003 && a.North + .0003 >= b.South;
    internal static string Normalize(string value) => string.Join(" ", Regex.Matches(value.ToLowerInvariant(), @"[\p{L}\p{N}]+")
        .Select(m => Suffixes.GetValueOrDefault(m.Value, m.Value)));
    internal static bool SameStreet(string left, string right) => Normalize(left) == Normalize(right);
    internal static bool CompletesStreetSuffix(string input, string candidate)
    {
        var a = Normalize(input).Split(' ');
        var b = Normalize(candidate).Split(' ');
        return b.Length == a.Length + 1 && RoadTypes.Contains(b[^1]) && !RoadTypes.Contains(a[^1]) &&
            Normalize(input) == string.Join(' ', b[..^1]);
    }
    private static readonly HashSet<string> RoadTypes = Suffixes.Values.Except(["n", "s", "e", "w"]).ToHashSet();

    // The parts of two names that may differ by a misspelling: everything but the
    // road type, which must agree when given, and a direction, which must agree.
    private static bool Comparable(string input, string candidate, out string left, out string right)
    {
        left = right = "";
        var a = Normalize(input).Split(' ');
        var b = Normalize(candidate).Split(' ');
        var sourceSuffix = RoadTypes.Contains(a[^1]);
        var candidateSuffix = RoadTypes.Contains(b[^1]);
        // A supplied road type must agree. A missing type may be completed along
        // with a bounded name correction, e.g. Graymoor Woods -> Braemoor Woods Rd.
        if (sourceSuffix && (!candidateSuffix || a[^1] != b[^1])) return false;
        var leftWords = sourceSuffix ? a[..^1] : a;
        var rightWords = candidateSuffix ? b[..^1] : b;
        if (leftWords.Length == 0 || rightWords.Length == 0) return false;
        if (leftWords.Length != rightWords.Length)
        {
            // Only a word run together or split apart: "Wood Meadow" for "Woodmeadow".
            left = right = string.Concat(leftWords);
            return left == string.Concat(rightWords);
        }
        if ((leftWords[0] is "n" or "s" or "e" or "w" || rightWords[0] is "n" or "s" or "e" or "w") &&
            leftWords[0] != rightWords[0]) return false;
        left = string.Join(' ', leftWords);
        right = string.Join(' ', rightWords);
        return true;
    }

    internal static bool Similar(string input, string candidate)
    {
        if (SameStreet(input, candidate) || CompletesStreetSuffix(input, candidate)) return true;
        return Comparable(input, candidate, out var left, out var right) &&
            Distance(left, right) <= Math.Min(2, Math.Min(left.Length, right.Length) / 3);
    }

    /// <summary>A mishearing spelled differently but pronounced alike ("Pellum" for
    /// "Pelham"). Only used against a city's complete street list.</summary>
    internal static bool SoundsAlike(string input, string candidate) =>
        Comparable(input, candidate, out var left, out var right) && left.Length >= 4 && right.Length >= 4 &&
        Math.Abs(left.Length - right.Length) <= 2 && Distance(left, right) <= 3 && Phonetic(left) == Phonetic(right);

    internal static int Distance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var current = new int[right.Length + 1]; current[0] = i;
            for (var j = 1; j <= right.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[^1];
    }

    /// <summary>A rough per-word consonant skeleton: silent and doubled letters and
    /// vowels after the first letter are dropped, like-sounding letters are merged.</summary>
    internal static string Phonetic(string value) => string.Join(" ",
        Regex.Matches(value.ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(m => PhoneticWord(m.Value)));

    private static string PhoneticWord(string word)
    {
        if (word.All(char.IsDigit)) return word;
        word = Regex.Replace(word, @"[^a-z]", "");
        word = Regex.Replace(word, "^kn", "n");
        word = Regex.Replace(word, "^wr", "r");
        word = word.Replace("tch", "C").Replace("sch", "sk").Replace("ch", "C").Replace("sh", "S").Replace("th", "T")
            .Replace("ph", "f").Replace("ck", "k").Replace("dg", "j").Replace("qu", "kw").Replace("x", "ks").Replace("z", "s");
        word = Regex.Replace(word, "c(?=[eiy])", "s").Replace("c", "k").Replace("gh", "").Replace("h", "");
        var key = new System.Text.StringBuilder();
        for (var i = 0; i < word.Length; i++)
        {
            var letter = word[i];
            // W is a consonant only before a vowel ("Broadway", not "Brown").
            if (letter == 'w' && (i == 0 || (i + 1 < word.Length && "aeiouy".Contains(word[i + 1])))) { }
            else if ("aeiouyw".Contains(letter))
            {
                if (i > 0) continue;
                letter = 'a';
            }
            if (key.Length == 0 || key[^1] != letter) key.Append(letter);
        }
        return key.ToString();
    }
    private static string? Text(JsonElement value, string field) => Geo.Text(value, field);
    private static bool Same(string? a, string? b) => Geo.Same(a, b);
    private static bool SameState(string? a, string? b) => Geo.SameState(a, b);
    private static string StateCode(string value) => Geo.StateCode(value);
    public void Dispose() => cache.Dispose();
}
