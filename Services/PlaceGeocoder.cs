using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;

namespace OpenScanner.WhisperServer.Services;

public sealed class LocalPlace
{
    public string Name { get; set; } = "";
    public string[] Aliases { get; set; } = [];
    public string Address { get; set; } = "";
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public string? HouseNumber { get; set; }
    public string? Street { get; set; }
    public string? PostalCode { get; set; }
    public string? Source { get; set; }
}

public sealed class PlaceGeocoder(IHttpClientFactory clients, IConfiguration config,
    AddressGeocoder census) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 1000 });
    private long nextRequestAt;
    public string DefaultCity => config["Geocoding:DefaultCity"] ?? "Salem";
    public string DefaultState => config["Geocoding:DefaultState"] ?? "NH";

    public async Task<GeocodeResponse> SearchAsync(string name, string city, string state, CancellationToken ct)
    {
        // Exact, operator-maintained aliases take priority over fuzzy external search.
        var known = config.GetSection("Geocoding:Places").Get<LocalPlace[]>() ?? [];
        var entries = known.Where(p => Same(p.City, city) && SameState(p.State, state) &&
            (Same(p.Name, name) || p.Aliases.Any(a => Same(a, name)))).ToList();
        if (entries.Count > 0)
        {
            var matches = new List<GeocodeMatch>();
            foreach (var entry in entries)
            {
                var located = await census.GeocodeAsync(entry.Address, ct);
                matches.AddRange(located.Matches.Select(m => m with { Name = entry.Name,
                    City = entry.City, State = entry.State, HouseNumber = entry.HouseNumber, Street = entry.Street,
                    PostalCode = entry.PostalCode, Country = "US", Attribution = entry.Source }));
            }
            return new GeocodeResponse(matches.Count > 0, matches, "Local directory / US Census Bureau",
                matches.Count == 1 ? 0 : null, matches.Count switch { 0 => "no_match", 1 => "single_match", _ => "ambiguous" });
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Geocoding:TimeoutSeconds", 30), 1, 300)));
        ct = deadline.Token;
        var query = $"{name}, {city}, {state}, USA";
        var key = query.ToUpperInvariant();
        await gate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue<GeocodeResponse>(key, out var cached)) return cached!;
            var delay = nextRequestAt - Environment.TickCount64;
            if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), ct);
            nextRequestAt = Environment.TickCount64 + 1000;
            var url = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
                config["Geocoding:PlaceEndpoint"] ?? "https://photon.komoot.io/api/",
                new Dictionary<string, string?> { ["q"] = query, ["limit"] = "10", ["lang"] = "en" });
            using var client = clients.CreateClient("geocoding");
            using var response = await client.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            using var payload = await response.Content.ReadFromJsonAsync<JsonDocument>(ct);
            if (payload?.RootElement.ValueKind != JsonValueKind.Object ||
                !payload.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array)
                throw new JsonException("Missing place search results.");
            var matches = new List<GeocodeMatch>();
            foreach (var feature in features.EnumerateArray())
            {
                if (feature.ValueKind != JsonValueKind.Object ||
                    !feature.TryGetProperty("properties", out var p) || p.ValueKind != JsonValueKind.Object ||
                    !feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object)
                    throw new JsonException("Invalid place search result.");
                var placeName = Text(p, "name");
                var placeCity = Text(p, "city");
                var placeState = Text(p, "state");
                // Never silently select another Salem, or another town's branch of a chain.
                if (!Same(Text(p, "countrycode"), "US") || !Same(city, placeCity) ||
                    !SameState(state, placeState) || string.IsNullOrWhiteSpace(placeName)) continue;
                if (Text(geometry, "type") != "Point" || !geometry.TryGetProperty("coordinates", out var xy) ||
                    xy.ValueKind != JsonValueKind.Array || xy.GetArrayLength() != 2 ||
                    xy[0].ValueKind != JsonValueKind.Number || !xy[0].TryGetDouble(out var lon) ||
                    xy[1].ValueKind != JsonValueKind.Number || !xy[1].TryGetDouble(out var lat) ||
                    !double.IsFinite(lon) || !double.IsFinite(lat) || lon is < -180 or > 180 || lat is < -90 or > 90)
                    throw new JsonException("Invalid place coordinates.");
                var house = Text(p, "housenumber");
                var street = Text(p, "street");
                var zip = Text(p, "postcode");
                var address = string.Join(", ", new[] { string.Join(" ", new[] { house, street }.Where(v => !string.IsNullOrWhiteSpace(v))),
                    placeCity, placeState, zip }.Where(v => !string.IsNullOrWhiteSpace(v)));
                matches.Add(new GeocodeMatch(address, lat, lon, placeName, house, street, placeCity,
                    placeState, zip, "US", "© OpenStreetMap contributors — https://www.openstreetmap.org/copyright"));
            }
            // Even a single fuzzy search result needs name/context validation by the LLM.
            var result = new GeocodeResponse(matches.Count > 0, matches, "Photon / OpenStreetMap",
                SelectionStatus: matches.Count == 0 ? "no_match" : "ambiguous");
            cache.Set(key, result, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24) });
            return result;
        }
        finally { gate.Release(); }
    }

    private static string? Text(JsonElement value, string field) =>
        value.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static bool Same(string? left, string? right) =>
        string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool SameState(string? left, string? right) => Same(NormalizeState(left), NormalizeState(right));
    private static string? NormalizeState(string? state) => Same(state, "NH") ? "New Hampshire" : state;
    public void Dispose() { gate.Dispose(); cache.Dispose(); }
}
