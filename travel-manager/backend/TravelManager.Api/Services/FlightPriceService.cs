using System.Net.Http.Headers;
using System.Text.Json;

namespace TravelManager.Api.Services;

public record FlightPriceMonth(string Month, string MonthLabel, int Price, string? Airline, string? DepartureAt);

public record FlightPriceInsight(
    bool Available,
    string? Message,
    string? OriginCode,
    string? OriginName,
    string? DestinationCode,
    string? DestinationName,
    string Currency,
    List<FlightPriceMonth> Months,
    FlightPriceMonth? Cheapest,
    FlightPriceMonth? Priciest,
    int? SavingsPercent,
    string? Suggestion
);

/// <summary>
/// Historical/cached flight fares via the free Travelpayouts (Aviasales) Data API to
/// suggest the cheapest months to travel a route. Needs a Travelpayouts token
/// (config "Travelpayouts:Token"); city names are resolved to IATA codes via the
/// keyless autocomplete endpoint.
/// </summary>
public class FlightPriceService
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly ILogger<FlightPriceService> _logger;

    public FlightPriceService(IHttpClientFactory http, IConfiguration config, ILogger<FlightPriceService> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config["Travelpayouts:Token"]);

    public async Task<FlightPriceInsight> GetInsightAsync(string origin, string destination, string? currency)
    {
        var cur = string.IsNullOrWhiteSpace(currency) ? "usd" : currency.Trim().ToLowerInvariant();
        var token = _config["Travelpayouts:Token"] ?? "";
        if (string.IsNullOrWhiteSpace(token))
            return Unavailable("Flight price lookup isn't configured yet.", cur);

        var (originCode, originName) = await ResolveIataAsync(origin);
        var (destCode, destName) = await ResolveIataAsync(destination);
        if (originCode == null || destCode == null)
            return Unavailable("Couldn't recognise one of those places — try a city or airport name.", cur, originCode, originName, destCode, destName);

        List<FlightPriceMonth> months;
        try
        {
            months = await GetMonthlyPricesAsync(originCode, destCode, cur, token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Travelpayouts monthly prices failed for {O}->{D}", originCode, destCode);
            return Unavailable("Couldn't reach the flight price service. Please try again.", cur, originCode, originName, destCode, destName);
        }

        if (months.Count == 0)
            return Unavailable($"No cached fares found for {originCode}→{destCode} right now (coverage is best on popular routes).", cur, originCode, originName, destCode, destName);

        var cheapest = months.OrderBy(m => m.Price).First();
        var priciest = months.OrderByDescending(m => m.Price).First();
        int? savings = priciest.Price > 0 && priciest.Price != cheapest.Price
            ? (int)Math.Round((priciest.Price - cheapest.Price) * 100.0 / priciest.Price)
            : null;

        var suggestion = savings is int s and > 5
            ? $"Cheapest around {cheapest.MonthLabel} (~{cur.ToUpperInvariant()} {cheapest.Price}) — about {s}% less than the priciest month ({priciest.MonthLabel}). Aim to travel then and book early."
            : $"Fares are fairly flat across the months we can see (~{cur.ToUpperInvariant()} {cheapest.Price}). {cheapest.MonthLabel} is marginally cheapest.";

        return new FlightPriceInsight(true, null, originCode, originName, destCode, destName, cur.ToUpperInvariant(),
            months, cheapest, priciest, savings, suggestion);
    }

    // ── Travelpayouts: cheapest cached fare per month ─────────────────────────

    private async Task<List<FlightPriceMonth>> GetMonthlyPricesAsync(string origin, string destination, string currency, string token)
    {
        using var client = _http.CreateClient();
        var url = $"https://api.travelpayouts.com/v1/prices/monthly?origin={origin}&destination={destination}&currency={currency}&token={Uri.EscapeDataString(token)}";
        var resp = await client.GetAsync(url);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) return new();

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return new();

        var list = new List<FlightPriceMonth>();
        foreach (var prop in data.EnumerateObject())
        {
            var monthKey = prop.Name; // "2026-08-01"
            var v = prop.Value;
            if (v.ValueKind != JsonValueKind.Object) continue;
            var price = v.TryGetProperty("price", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;
            if (price <= 0) continue;
            var airline = v.TryGetProperty("airline", out var a) ? a.GetString() : null;
            var departureAt = v.TryGetProperty("departure_at", out var d) ? d.GetString() : null;
            list.Add(new FlightPriceMonth(monthKey, MonthLabel(monthKey), price, airline, departureAt));
        }
        return list.OrderBy(m => m.Month).ToList();
    }

    // ── Travelpayouts: keyless city/airport -> IATA ───────────────────────────

    private async Task<(string? code, string? name)> ResolveIataAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return (null, null);
        var q = query.Trim();

        // A 3-letter token is very likely already an IATA code.
        if (q.Length == 3 && q.All(char.IsLetter)) return (q.ToUpperInvariant(), q.ToUpperInvariant());

        try
        {
            using var client = _http.CreateClient();
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var url = $"https://autocomplete.travelpayouts.com/places2?term={Uri.EscapeDataString(q)}&locale=en&types[]=city&types[]=airport";
            var resp = await client.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return (null, null);
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return (null, null);

            // Prefer a city match, else the first airport.
            JsonElement chosen = default;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("type", out var t) && t.GetString() == "city") { chosen = el; break; }
            }
            if (chosen.ValueKind != JsonValueKind.Object) chosen = doc.RootElement[0];

            var code = chosen.TryGetProperty("code", out var c) ? c.GetString() : null;
            var name = chosen.TryGetProperty("name", out var n) ? n.GetString()
                     : chosen.TryGetProperty("city_name", out var cn) ? cn.GetString() : code;
            return (code?.ToUpperInvariant(), name);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IATA resolve failed for {Query}", query);
            return (null, null);
        }
    }

    private static string MonthLabel(string monthKey)
        => DateTime.TryParse(monthKey, out var d) ? d.ToString("MMM yyyy") : monthKey;

    private static FlightPriceInsight Unavailable(string message, string currency,
        string? oc = null, string? on_ = null, string? dc = null, string? dn = null)
        => new(false, message, oc, on_, dc, dn, currency.ToUpperInvariant(), new(), null, null, null, null);
}
