using OpenScanner.WhisperServer.Api;
using System.Text.RegularExpressions;

namespace OpenScanner.WhisperServer.Services;

public sealed class LocationEnricher(GeocodeResolver resolver, PlaceGeocoder places, StreetGeocoder streets, ILogger<LocationEnricher> logger)
{
    public async Task<AddressResponse> EnrichAsync(Dictionary<string, string?> fields, string text, CancellationToken ct,
        string? defaultCity = null, string? defaultState = null)
    {
        var city = LocationDefaults.Normalize(fields["city"]) ?? LocationDefaults.Normalize(defaultCity) ?? places.DefaultCity;
        var state = LocationDefaults.Normalize(fields["state"]) ?? LocationDefaults.Normalize(defaultState) ?? places.DefaultState;
        GeocodeMatch? geocode = null;
        var address = fields["address"];
        var placeName = fields["placeName"];
        // A model example or a previous call must never introduce a business into
        // a different transmission and route its lookup through the local directory.
        if (!string.IsNullOrWhiteSpace(placeName) && !Mentioned(placeName, text))
        {
            placeName = fields["placeName"] = null;
            logger.LogWarning("Discarded a place name absent from the transmission");
        }
        var numberedStreet = !string.IsNullOrWhiteSpace(fields["houseNumber"]) &&
            !string.IsNullOrWhiteSpace(fields["street"]);
        var usePlaceLookup = !numberedStreet && !string.IsNullOrWhiteSpace(placeName) && placeName.Length <= 500;
        var streetOnly = string.IsNullOrWhiteSpace(fields["houseNumber"]) && !string.IsNullOrWhiteSpace(fields["street"]) && !usePlaceLookup;
        if ((!string.IsNullOrWhiteSpace(address) && address.Length <= 500) ||
            (!string.IsNullOrWhiteSpace(placeName) && placeName.Length <= 500) || streetOnly)
        {
            try
            {
                GeocodeResponse resolved;
                if (usePlaceLookup)
                    resolved = await resolver.ResolvePlaceAsync(placeName!, city, state,
                        text, ct);
                else if (streetOnly)
                {
                    geocode = await streets.ResolveCenterAsync(fields["street"]!, city, state, ct);
                    resolved = new GeocodeResponse(false, []);
                }
                else
                {
                    var query = string.Join(", ", new[] { address,
                        ContainsLocality(address!, city) ? null : city,
                        ContainsLocality(address!, state) ? null : state }
                        .Where(v => !string.IsNullOrWhiteSpace(v)));
                    resolved = await resolver.ResolveAsync(query, text, ct);
                    if (resolved.Matches.Count == 0 && !string.IsNullOrWhiteSpace(fields["houseNumber"]) &&
                        !string.IsNullOrWhiteSpace(fields["street"]))
                    {
                        // A verified OSM building does not require Census coverage. Only
                        // fall through to street-name repair when no building candidates exist.
                        try
                        {
                            var building = await places.SearchAddressAsync(fields["houseNumber"]!, fields["street"]!, city, state, ct);
                            geocode = building.SelectedMatch;
                            if (building.Matches.Count > 0)
                                resolved = building;
                        }
                        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException ||
                            ex is OperationCanceledException && !ct.IsCancellationRequested)
                        {
                            logger.LogWarning("Building lookup unavailable ({FailureType}); trying street lookup", ex.GetType().Name);
                        }
                        if (resolved.Matches.Count == 0)
                            geocode = await streets.ResolveAsync(fields["houseNumber"]!, fields["street"]!,
                                city, state, fields["crossStreet"], ct);
                    }
                }
                geocode ??= resolved.SelectedMatch;
                if (geocode is not null && !string.IsNullOrWhiteSpace(fields["houseNumber"]) &&
                    !MatchesNumberedAddress(fields["houseNumber"]!, fields["street"], geocode,
                        city, state, resolved.Matches.Count <= 1))
                {
                    geocode = null;
                    logger.LogWarning("Discarded a geocode conflicting with the extracted numbered street");
                }
                if (geocode is not null && usePlaceLookup)
                {
                    fields["address"] = geocode.MatchedAddress;
                    fields["houseNumber"] = geocode.HouseNumber ?? fields["houseNumber"];
                    fields["street"] = geocode.Street ?? fields["street"];
                    fields["city"] = geocode.City ?? fields["city"];
                    fields["state"] = geocode.State ?? fields["state"];
                    fields["postalCode"] = geocode.PostalCode ?? fields["postalCode"];
                    fields["country"] = geocode.Country ?? fields["country"];
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException ||
                (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                // Optional enrichment must not discard a successful extraction.
                logger.LogWarning("Automatic geocoding could not resolve the extracted address ({FailureType})", ex.GetType().Name);
            }
        }
        return new AddressResponse(fields["address"], fields["houseNumber"], fields["street"],
            fields["unit"], fields["city"], fields["state"], fields["postalCode"], fields["country"],
            fields["crossStreet"], geocode, placeName);
    }

    private static bool ContainsLocality(string address, string value) =>
        address.Split(',').Skip(1).Any(part =>
            Regex.IsMatch(part, @"\b" + Regex.Escape(value) + @"\b", RegexOptions.IgnoreCase));

    private static bool Mentioned(string name, string text)
    {
        var words = Regex.Matches(name, @"[\p{L}\p{N}]+").Select(m => Regex.Escape(m.Value)).ToArray();
        return words.Length > 0 && Regex.IsMatch(text, @"\b" + string.Join(@"[\W_]+", words) + @"\b",
            RegexOptions.IgnoreCase);
    }

    private static bool MatchesNumberedAddress(string house, string? street, GeocodeMatch match,
        string city, string state, bool uniqueCandidate)
    {
        var parts = match.MatchedAddress.Split(',').Select(p => p.Trim()).ToArray();
        var firstLine = parts[0];
        var parsed = Regex.Match(firstLine, @"^(?<house>\d+[A-Za-z]?(?:-\d+)?)\s+(?<street>.+)$");
        var matchedHouse = match.HouseNumber ?? (parsed.Success ? parsed.Groups["house"].Value : null);
        if (!string.Equals(house.Trim(), matchedHouse?.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        if (parsed.Success && !string.Equals(house.Trim(), parsed.Groups["house"].Value, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(street)) return true;
        var matchedStreet = match.Street ?? (parsed.Success ? parsed.Groups["street"].Value : null);
        if (matchedStreet is null) return false;
        if (StreetGeocoder.SameStreet(street, matchedStreet)) return true;
        if (!uniqueCandidate || !StreetGeocoder.Similar(street, matchedStreet)) return false;
        // A corrected name/suffix needs a unique provider result in the requested
        // locality, not simply a close string or the model's preferred candidate.
        var matchedCity = match.City ?? (parts.Length >= 3 ? parts[1] : null);
        var matchedState = match.State ?? (parts.Length >= 3 ? parts[2] : null);
        return string.Equals(city.Trim(), matchedCity?.Trim(), StringComparison.OrdinalIgnoreCase) &&
            matchedState is not null && Geo.StateCode(state).Equals(Geo.StateCode(matchedState), StringComparison.OrdinalIgnoreCase);
    }
}
