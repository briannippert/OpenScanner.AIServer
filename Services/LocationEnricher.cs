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
        if ((!string.IsNullOrWhiteSpace(address) && address.Length <= 500) ||
            (!string.IsNullOrWhiteSpace(placeName) && placeName.Length <= 500))
        {
            try
            {
                GeocodeResponse resolved;
                if (!string.IsNullOrWhiteSpace(placeName) && placeName.Length <= 500)
                    resolved = await resolver.ResolvePlaceAsync(placeName, city, state,
                        text, ct);
                else
                {
                    var query = string.Join(", ", new[] { address,
                        ContainsLocality(address!, city) ? null : city,
                        ContainsLocality(address!, state) ? null : state }
                        .Where(v => !string.IsNullOrWhiteSpace(v)));
                    resolved = await resolver.ResolveAsync(query, text, ct);
                    if (resolved.Matches.Count == 0 && !string.IsNullOrWhiteSpace(fields["houseNumber"]) &&
                        !string.IsNullOrWhiteSpace(fields["street"]))
                        geocode = await streets.ResolveAsync(fields["houseNumber"]!, fields["street"]!,
                            city, state,
                            fields["crossStreet"], ct);
                }
                geocode ??= resolved.SelectedMatch;
                if (geocode is not null && !string.IsNullOrWhiteSpace(placeName))
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
}
