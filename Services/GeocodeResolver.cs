using System.Text.Json;

namespace OpenScanner.AIServer.Services;

// Shared by direct geocoding and automatic enrichment after address extraction.
public sealed class GeocodeResolver(AddressGeocoder geocoder, AddressExtractor extractor, PlaceGeocoder places)
{
    public async Task<GeocodeResponse> ResolveAsync(string address, string? context, CancellationToken ct)
    {
        var result = await geocoder.GeocodeAsync(address, ct);
        return await SelectAsync(address, context, result, false, ct);
    }

    public async Task<GeocodeResponse> ResolvePlaceAsync(string name, string? city, string? state,
        string? context, CancellationToken ct)
    {
        city = string.IsNullOrWhiteSpace(city) ? places.DefaultCity : city;
        state = string.IsNullOrWhiteSpace(state) ? places.DefaultState : state;
        var result = await places.SearchAsync(name, city, state, ct);
        return await SelectAsync($"{name}, {city}, {state}", context, result,
            result.Provider == "Photon / OpenStreetMap", ct);
    }

    private async Task<GeocodeResponse> SelectAsync(string address, string? context,
        GeocodeResponse result, bool verifySingle, CancellationToken ct)
    {
        if (result.Matches.Count == 0 || (result.Matches.Count == 1 && !verifySingle)) return result;
        try
        {
            var selected = await extractor.SelectMatchAsync(address, context, result.Matches, ct);
            return result with { SelectedMatchIndex = selected,
                SelectionStatus = selected.HasValue ? "llm_selected" : "ambiguous" };
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException ||
            (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // Geocoding succeeded; retain candidates even if local ranking failed.
            return result with { SelectionStatus = "llm_unavailable" };
        }
    }
}
