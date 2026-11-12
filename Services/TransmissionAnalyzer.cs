using OpenScanner.WhisperServer.Api;

namespace OpenScanner.WhisperServer.Services;

public sealed class TransmissionAnalyzer(AddressExtractor extractor, LocationEnricher locations)
{
    public async Task<TransmissionResponse?> AnalyzeAsync(string text, string? service,
        string? defaultCity, string? defaultState, CancellationToken ct)
    {
        var fields = await extractor.ExtractTransmissionAsync(text, service, ct);
        if (fields is null) return null;
        var location = await locations.EnrichAsync(fields, text, ct, defaultCity, defaultState);
        return new TransmissionResponse(fields["callType"]!, fields["service"], fields["summary"]!,
            fields["incidentStatus"], location);
    }
}

public static class LocationDefaults
{
    public static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    public static bool Valid(string? city, string? state) => new[] { city, state }
        .All(v => v is null || v.Length <= 100 && !v.Any(char.IsControl) && !v.Contains(','));
    public const string Error = "Default city/state must be at most 100 characters without commas or control characters.";
}
