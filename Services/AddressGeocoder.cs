using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Caching.Memory;

namespace OpenScanner.WhisperServer.Services;

public sealed record GeocodeRequest(string? Address, string? Context = null);
public sealed record GeocodeMatch(string MatchedAddress, double Latitude, double Longitude,
    string? Name = null, string? HouseNumber = null, string? Street = null, string? City = null,
    string? State = null, string? PostalCode = null, string? Country = null,
    string? Attribution = null, string Precision = "address", string Provider = "US Census Bureau");
public sealed record GeocodeResponse(bool Found, IReadOnlyList<GeocodeMatch> Matches,
    string Provider = "US Census Bureau", int? SelectedMatchIndex = null,
    string SelectionStatus = "no_match")
{
    public GeocodeMatch? SelectedMatch => SelectedMatchIndex is int index ? Matches[index] : null;
}

public sealed class AddressGeocoder : IDisposable
{
    private readonly IHttpClientFactory clients;
    private readonly Uri endpoint;
    private readonly string benchmark;
    private readonly TimeSpan timeout;
    private readonly UpstreamThrottle throttle;
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 1000 });

    public AddressGeocoder(IHttpClientFactory clients, IConfiguration config, UpstreamThrottle throttle)
    {
        this.clients = clients;
        this.throttle = throttle;
        endpoint = new Uri(config["Geocoding:Endpoint"] ??
            "https://geocoding.geo.census.gov/geocoder/locations/onelineaddress");
        benchmark = config["Geocoding:Benchmark"] ?? "Public_AR_Current";
        timeout = Geo.Timeout(config);
    }

    public async Task<GeocodeResponse> GeocodeAsync(string address, CancellationToken cancellationToken)
    {
        var query = address.Trim();
        // Only the address is sent to the provider, never the original transcript.
        var key = query.ToUpperInvariant();
        if (cache.TryGetValue<GeocodeResponse>(key, out var cached)) return cached!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        var ct = deadline.Token;
        var url = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(endpoint.AbsoluteUri,
            new Dictionary<string, string?> { ["address"] = query, ["benchmark"] = benchmark, ["format"] = "json" });
        var payload = await throttle.GetJsonAsync<CensusResponse>(clients, url, ct);
        if (payload?.Result?.AddressMatches is not { } matches)
            throw new JsonException("Missing geocoding results.");
        var results = new List<GeocodeMatch>();
        foreach (var match in matches)
        {
            if (match is null || string.IsNullOrWhiteSpace(match.MatchedAddress) || match.Coordinates is not { } point ||
                !double.IsFinite(point.X) || !double.IsFinite(point.Y) ||
                point.X is < -180 or > 180 || point.Y is < -90 or > 90)
                throw new JsonException("Invalid geocoding match.");
            // Census uses x = longitude, y = latitude.
            results.Add(new GeocodeMatch(match.MatchedAddress, point.Y, point.X));
        }
        var result = new GeocodeResponse(results.Count > 0, results,
            SelectedMatchIndex: results.Count == 1 ? 0 : null,
            SelectionStatus: results.Count switch { 0 => "no_match", 1 => "single_match", _ => "ambiguous" });
        cache.Set(key, result, new MemoryCacheEntryOptions
        {
            Size = 1,
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24)
        });
        return result;
    }

    public void Dispose() => cache.Dispose();

    private sealed record CensusResponse([property: JsonRequired] CensusResult? Result);
    private sealed record CensusResult([property: JsonRequired] CensusMatch?[]? AddressMatches);
    private sealed record CensusMatch([property: JsonRequired] string? MatchedAddress,
        [property: JsonRequired] CensusCoordinates? Coordinates);
    private sealed record CensusCoordinates([property: JsonRequired] double X, [property: JsonRequired] double Y);
}
