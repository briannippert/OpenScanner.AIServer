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
    AddressGeocoder census, UpstreamThrottle throttle) : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 1000 });
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

        return await SearchPhotonAsync(name, city, state, null, null, ct);
    }

    public Task<GeocodeResponse> SearchAddressAsync(string house, string street, string city, string state, CancellationToken ct) =>
        SearchPhotonAsync($"{house} {street}", city, state, house, street, ct);

    private async Task<GeocodeResponse> SearchPhotonAsync(string name, string city, string state,
        string? expectedHouse, string? expectedStreet, CancellationToken ct)
    {
        var query = $"{name}, {city}, {state}, USA";
        var key = (expectedHouse is null ? "place:" : "address:") + query.ToUpperInvariant();
        if (cache.TryGetValue<GeocodeResponse>(key, out var cached)) return cached!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(Geo.Timeout(config));
        ct = deadline.Token;
        var url = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(
            config["Geocoding:PlaceEndpoint"] ?? "https://photon.komoot.io/api/",
            new Dictionary<string, string?> { ["q"] = query, ["limit"] = "10", ["lang"] = "en" });
        using var payload = await throttle.GetJsonAsync<JsonDocument>(clients, url, ct);
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
                !SameState(state, placeState)) continue;
            var house = Text(p, "housenumber");
            var street = Text(p, "street");
            if (expectedHouse is null)
            {
                if (string.IsNullOrWhiteSpace(placeName)) continue;
            }
            else if (!Same(house, expectedHouse) || street is null || expectedStreet is null ||
                !(StreetGeocoder.SameStreet(street, expectedStreet) || StreetGeocoder.CompletesStreetSuffix(expectedStreet, street)) ||
                Text(p, "type") != "house") continue;
            if (Text(geometry, "type") != "Point" || !geometry.TryGetProperty("coordinates", out var xy) ||
                xy.ValueKind != JsonValueKind.Array || xy.GetArrayLength() != 2 ||
                xy[0].ValueKind != JsonValueKind.Number || !xy[0].TryGetDouble(out var lon) ||
                xy[1].ValueKind != JsonValueKind.Number || !xy[1].TryGetDouble(out var lat) ||
                !double.IsFinite(lon) || !double.IsFinite(lat) || lon is < -180 or > 180 || lat is < -90 or > 90)
                throw new JsonException("Invalid place coordinates.");
            var zip = Text(p, "postcode");
            var address = string.Join(", ", new[] { string.Join(" ", new[] { house, street }.Where(v => !string.IsNullOrWhiteSpace(v))),
                placeCity, placeState, zip }.Where(v => !string.IsNullOrWhiteSpace(v)));
            matches.Add(new GeocodeMatch(address, lat, lon, placeName, house, street, placeCity,
                placeState, zip, "US", Geo.OsmAttribution,
                Precision: expectedHouse is null ? "place" : Text(p, "osm_key") == "building" ? "building" : "address",
                Provider: "Photon / OpenStreetMap"));
        }
        // Business names need LLM validation. A full numbered address may be selected
        // directly only when all requested address components matched uniquely.
        matches = matches.DistinctBy(m => (m.MatchedAddress.ToUpperInvariant(), m.Latitude, m.Longitude)).ToList();
        var result = new GeocodeResponse(matches.Count > 0, matches, "Photon / OpenStreetMap",
            SelectedMatchIndex: expectedHouse is not null && matches.Count == 1 ? 0 : null,
            SelectionStatus: matches.Count == 0 ? "no_match" : expectedHouse is not null && matches.Count == 1 ? "verified_building" : "ambiguous");
        cache.Set(key, result, new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24) });
        return result;
    }

    private static string? Text(JsonElement value, string field) => Geo.Text(value, field);
    private static bool Same(string? left, string? right) => Geo.Same(left, right);
    private static bool SameState(string? left, string? right) => Geo.SameState(left, right);
    public void Dispose() => cache.Dispose();
}
