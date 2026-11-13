using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

namespace OpenScanner.WhisperServer.Services;

/// <summary>
/// One request per second per upstream host, shared by every geocoder that calls it
/// (e.g. place and street lookups both use Photon). A slot is reserved under a short
/// lock and the wait happens outside it, so callers never hold a lock across HTTP calls.
/// </summary>
public sealed class UpstreamThrottle
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private readonly ConcurrentDictionary<string, Slot> hosts = new(StringComparer.OrdinalIgnoreCase);

    private sealed class Slot { public long NextAt; }

    public async Task WaitTurnAsync(string url, CancellationToken ct)
    {
        var slot = hosts.GetOrAdd(new Uri(url).Authority, _ => new Slot());
        long startAt;
        lock (slot)
        {
            startAt = Math.Max(Environment.TickCount64, slot.NextAt);
            // Reserved even if the caller is later cancelled: failed calls still count.
            slot.NextAt = startAt + (long)Interval.TotalMilliseconds;
        }
        var delay = startAt - Environment.TickCount64;
        if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), ct);
    }

    public async Task<T?> GetJsonAsync<T>(IHttpClientFactory clients, string url, CancellationToken ct)
    {
        await WaitTurnAsync(url, ct);
        using var client = clients.CreateClient("geocoding");
        using var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(ct);
    }
}

internal static class Geo
{
    public const string OsmAttribution = "© OpenStreetMap contributors — https://www.openstreetmap.org/copyright";

    private static readonly string[] StateNames =
        "AL:Alabama|AK:Alaska|AZ:Arizona|AR:Arkansas|CA:California|CO:Colorado|CT:Connecticut|DE:Delaware|DC:District of Columbia|FL:Florida|GA:Georgia|HI:Hawaii|ID:Idaho|IL:Illinois|IN:Indiana|IA:Iowa|KS:Kansas|KY:Kentucky|LA:Louisiana|ME:Maine|MD:Maryland|MA:Massachusetts|MI:Michigan|MN:Minnesota|MS:Mississippi|MO:Missouri|MT:Montana|NE:Nebraska|NV:Nevada|NH:New Hampshire|NJ:New Jersey|NM:New Mexico|NY:New York|NC:North Carolina|ND:North Dakota|OH:Ohio|OK:Oklahoma|OR:Oregon|PA:Pennsylvania|RI:Rhode Island|SC:South Carolina|SD:South Dakota|TN:Tennessee|TX:Texas|UT:Utah|VT:Vermont|VA:Virginia|WA:Washington|WV:West Virginia|WI:Wisconsin|WY:Wyoming".Split('|');

    /// <summary>Per-call upstream timeout, clamped like every other configured timeout.</summary>
    public static TimeSpan Timeout(IConfiguration config) =>
        TimeSpan.FromSeconds(Math.Clamp(config.GetValue("Geocoding:TimeoutSeconds", 30), 1, 300));

    public static string? Text(JsonElement value, string field) =>
        value.TryGetProperty(field, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    public static bool Same(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    public static bool SameState(string? a, string? b) =>
        a is not null && b is not null && Same(StateCode(a), StateCode(b));

    public static string StateCode(string value) => StateNames.Select(s => s.Split(':'))
        .FirstOrDefault(p => Same(p[0], value) || Same(p[1], value))?[0] ?? value.Trim();
}
