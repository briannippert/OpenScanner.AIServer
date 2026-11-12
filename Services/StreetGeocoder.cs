using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;

namespace OpenScanner.WhisperServer.Services;

// Photon supplies street names, never house coordinates. Census must validate the
// unchanged house number on a unique, similar street in the requested locality.
public sealed class StreetGeocoder(IHttpClientFactory clients, IConfiguration config,
    AddressGeocoder census) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 1000 });
    private long nextRequestAt;
    private sealed record Road(string Name, double West, double South, double East, double North);
    private static readonly Dictionary<string, string> Suffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["avenue"] = "ave", ["street"] = "st", ["road"] = "rd", ["drive"] = "dr",
        ["lane"] = "ln", ["court"] = "ct", ["circle"] = "cir", ["boulevard"] = "blvd",
        ["terrace"] = "ter", ["place"] = "pl", ["highway"] = "hwy", ["parkway"] = "pkwy",
        ["north"] = "n", ["south"] = "s", ["east"] = "e", ["west"] = "w"
    };
    private static readonly string[] StateNames =
        "AL:Alabama|AK:Alaska|AZ:Arizona|AR:Arkansas|CA:California|CO:Colorado|CT:Connecticut|DE:Delaware|DC:District of Columbia|FL:Florida|GA:Georgia|HI:Hawaii|ID:Idaho|IL:Illinois|IN:Indiana|IA:Iowa|KS:Kansas|KY:Kentucky|LA:Louisiana|ME:Maine|MD:Maryland|MA:Massachusetts|MI:Michigan|MN:Minnesota|MS:Mississippi|MO:Missouri|MT:Montana|NE:Nebraska|NV:Nevada|NH:New Hampshire|NJ:New Jersey|NM:New Mexico|NY:New York|NC:North Carolina|ND:North Dakota|OH:Ohio|OK:Oklahoma|OR:Oregon|PA:Pennsylvania|RI:Rhode Island|SC:South Carolina|SD:South Dakota|TN:Tennessee|TX:Texas|UT:Utah|VT:Vermont|VA:Virginia|WA:Washington|WV:West Virginia|WI:Wisconsin|WY:Wyoming".Split('|');

    public async Task<GeocodeMatch?> ResolveAsync(string house, string street, string city,
        string state, string? crossStreet, CancellationToken ct)
    {
        if (!Regex.IsMatch(house, @"^\d+[A-Za-z]?(?:-\d+)?$") ||
            street.Length > 150 || city.Length > 100 || state.Length > 100 || crossStreet?.Length > 300)
            return null;
        var crosses = string.IsNullOrWhiteSpace(crossStreet) ? [] :
            Regex.Split(crossStreet, @"\s+(?:and|at)\s+|\s*[&/]\s*", RegexOptions.IgnoreCase)
                .Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (crosses.Length > 2) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Geocoding:TimeoutSeconds", 30), 1, 300)));
        ct = deadline.Token;
        var key = JsonSerializer.Serialize(new[] { house, street, city, state, crossStreet }).ToUpperInvariant();
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue<GeocodeResponse>(key, out var cached)) return cached!.SelectedMatch;
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
                        Attribution = "US Census Bureau; street names © OpenStreetMap contributors — https://www.openstreetmap.org/copyright" });
                }
            }
            matches = matches.DistinctBy(m => m.MatchedAddress.ToUpperInvariant()).ToList();
            var result = new GeocodeResponse(matches.Count > 0, matches, "Photon street search / US Census Bureau",
                matches.Count == 1 ? 0 : null, matches.Count == 1 ? "verified_street_fallback" : "ambiguous");
            cache.Set(key, result, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24) });
            return result.SelectedMatch;
        }
        finally { gate.Release(); }
    }

    private async Task<List<Road>> FindRoadsAsync(string street, string city, string state, CancellationToken ct)
    {
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
            var delay = nextRequestAt - Environment.TickCount64;
            if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), ct);
            nextRequestAt = Environment.TickCount64 + 1000;
            var url = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
                config["Geocoding:StreetEndpoint"] ?? "https://photon.komoot.io/api/",
                new Dictionary<string, string?> { ["q"] = $"{search}, {city}, {state}, USA",
                    ["limit"] = "10", ["lang"] = "en", ["osm_tag"] = "highway" });
            using var client = clients.CreateClient("geocoding");
            using var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>(ct);
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
                roads.Add(new Road(name, bounds[0], bounds[3], bounds[2], bounds[1]));
            }
        }
        return roads;
    }

    private static bool Near(Road a, Road b) => a.West <= b.East + .0003 && a.East + .0003 >= b.West &&
        a.South <= b.North + .0003 && a.North + .0003 >= b.South;
    private static string Normalize(string value) => string.Join(" ", Regex.Matches(value.ToLowerInvariant(), @"[\p{L}\p{N}]+")
        .Select(m => Suffixes.GetValueOrDefault(m.Value, m.Value)));
    private static bool Similar(string input, string candidate)
    {
        var a = Normalize(input).Split(' ');
        var b = Normalize(candidate).Split(' ');
        // Require compatible road type and directional prefixes; only the name may vary.
        if (a.Length != b.Length || a.Length < 2 || a[^1] != b[^1]) return false;
        if ((a[0] is "n" or "s" or "e" or "w" || b[0] is "n" or "s" or "e" or "w") && a[0] != b[0]) return false;
        var left = string.Join(' ', a[..^1]);
        var right = string.Join(' ', b[..^1]);
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var current = new int[right.Length + 1]; current[0] = i;
            for (var j = 1; j <= right.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[^1] <= Math.Min(2, Math.Min(left.Length, right.Length) / 3);
    }
    private static string? Text(JsonElement value, string field) => value.TryGetProperty(field, out var item) &&
        item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static bool Same(string? a, string? b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool SameState(string? a, string? b) => a is not null && b is not null && Same(StateCode(a), StateCode(b));
    private static string StateCode(string value) => StateNames.Select(s => s.Split(':'))
        .FirstOrDefault(p => Same(p[0], value) || Same(p[1], value))?[0] ?? value.Trim();
    public void Dispose() { gate.Dispose(); cache.Dispose(); }
}
