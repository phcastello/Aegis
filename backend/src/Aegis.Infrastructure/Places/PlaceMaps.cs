using System.Text.Json;
using Aegis.Application.Nodes;
using Aegis.Application.Places;
using Microsoft.Extensions.Options;

namespace Aegis.Infrastructure.Places;

public sealed class PlaceMapOptions
{
    public string TileUrl { get; set; } = "https://tile.openstreetmap.org/{z}/{x}/{y}.png";
    public string Attribution { get; set; } = "© OpenStreetMap contributors";
    // Public Photon permits reasonable use, with no availability guarantee. Search is
    // explicit, rate limited, and can be disabled/replaced by the operator.
    public string GeocoderProvider { get; set; } = "photon";
    public string GeocoderUrl { get; set; } = "https://photon.komoot.io/api/";
    public string GeocoderApiKey { get; set; } = "";
}
public sealed class PlaceMaps(IHttpClientFactory clients, IOptions<PlaceMapOptions> options)
{
    private static long lastSearch;
    public object Configuration() => new { tileUrl = options.Value.TileUrl, attribution = options.Value.Attribution, geocodingConfigured = !string.IsNullOrEmpty(options.Value.GeocoderUrl), geocoderProvider = options.Value.GeocoderProvider };
    public async Task<MapSearchView> SearchAsync(string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 200 || query.Any(char.IsControl)) throw new NodeException("invalid_address_query", "Digite um endereço de até 200 caracteres.");
        var endpoint = options.Value.GeocoderUrl;
        if (string.IsNullOrEmpty(endpoint)) return new(false, []);
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)) throw new NodeException("geocoder_configuration", "O provedor de endereços precisa de configuração HTTPS válida.", 503);
        var now = System.Diagnostics.Stopwatch.GetTimestamp(); var previous = Interlocked.Read(ref lastSearch);
        if (previous != 0 && System.Diagnostics.Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromSeconds(1) || Interlocked.CompareExchange(ref lastSearch, now, previous) != previous)
            throw new NodeException("address_search_rate_limited", "Aguarde um segundo antes de buscar novamente.", 429);
        var photon = options.Value.GeocoderProvider == "photon";
        if (!photon && options.Value.GeocoderProvider != "nominatim-compatible") throw new NodeException("geocoder_configuration", "O provedor de endereços precisa de configuração válida.", 503);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint + (endpoint.Contains('?') ? "&" : "?") + (photon ? "limit=5&" : "format=jsonv2&limit=5&") + "q=" + Uri.EscapeDataString(query));
        request.Headers.UserAgent.ParseAdd("Aegis/0.7 (+https://github.com/phcastello/Aegis)");
        if (!string.IsNullOrEmpty(options.Value.GeocoderApiKey)) request.Headers.TryAddWithoutValidation("X-Api-Key", options.Value.GeocoderApiKey);
        try {
            using var response = await clients.CreateClient("place-geocoder").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new NodeException("address_search_unavailable", "A busca de endereços está indisponível. Seus lugares salvos continuam disponíveis.", 503);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[65537]; var length = 0;
            while (length < buffer.Length) { var read = await stream.ReadAsync(buffer.AsMemory(length), ct); if (read == 0) break; length += read; }
            if (length > 65536) throw new JsonException();
            using var json = JsonDocument.Parse(buffer.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 16 });
            var results = new List<MapSearchResult>();
            if (photon) {
                if (json.RootElement.ValueKind != JsonValueKind.Object || !json.RootElement.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Array) throw new JsonException();
                foreach (var entry in features.EnumerateArray().Take(5)) {
                    // Providers are external input: skip a malformed feature without
                    // discarding valid alternatives or treating a non-point as a point.
                    if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object ||
                        !geometry.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "Point" ||
                        !geometry.TryGetProperty("coordinates", out var coordinates) || coordinates.ValueKind != JsonValueKind.Array || coordinates.GetArrayLength() < 2 ||
                        coordinates[0].ValueKind != JsonValueKind.Number || !coordinates[0].TryGetDouble(out var lon) ||
                        coordinates[1].ValueKind != JsonValueKind.Number || !coordinates[1].TryGetDouble(out var lat) ||
                        !entry.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) continue;
                    var name = string.Join(", ", new[] { "name", "street", "housenumber", "district", "city", "state", "country" }
                        .Select(key => properties.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct());
                    if (string.IsNullOrWhiteSpace(name) || name.Length > 500 || !double.IsFinite(lat) || lat is < -90 or > 90 || !double.IsFinite(lon) || lon is < -180 or > 180) continue;
                    results.Add(new(name, lat, lon));
                }
                return new(true, results);
            }
            foreach (var entry in json.RootElement.EnumerateArray().Take(5)) {
                var name = entry.GetProperty("display_name").GetString();
                if (!double.TryParse(entry.GetProperty("lat").GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lat) ||
                    !double.TryParse(entry.GetProperty("lon").GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var lon) || string.IsNullOrWhiteSpace(name) || name.Length > 500 || !double.IsFinite(lat) || lat is < -90 or > 90 || !double.IsFinite(lon) || lon is < -180 or > 180) continue;
                results.Add(new(name, lat, lon));
            }
            return new(true, results);
        } catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or OperationCanceledException && !ct.IsCancellationRequested) {
            throw new NodeException("address_search_unavailable", "A busca de endereços não respondeu. Tente novamente ou ajuste a área no mapa.", 503);
        }
    }
}
