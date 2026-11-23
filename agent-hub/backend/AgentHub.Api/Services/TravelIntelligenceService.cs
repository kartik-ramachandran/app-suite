using System.Text.Json;
using AgentHub.Api.Models;

namespace AgentHub.Api.Services;

public record TravelWeatherDay(string Date, double TempMaxC, double TempMinC, double PrecipMm, string Summary);

public record TravelGuide(
    string Destination,
    string? Country,
    string StartDate,
    string EndDate,
    string WeatherKind,            // "forecast" | "seasonal" | "unavailable"
    string WeatherHeadline,
    List<TravelWeatherDay> Weather,
    string Overview,
    List<string> ThingsToDo,
    List<string> Neighborhoods,
    List<string> Packing,
    List<string> LocalDress,
    List<string> Food,
    List<string> LocalTips,
    string GettingAround,
    string Disclaimer
);

/// <summary>
/// Generates an AI travel guide for a destination + dates: real weather from Open-Meteo
/// (no API key) drives packing/clothing advice, and an LLM composes the destination guide,
/// things to do, and dietary-aware food picks. Always returns a disclaimer.
/// </summary>
public class TravelIntelligenceService
{
    private readonly IAIService _ai;
    private readonly IHttpClientFactory _http;
    private readonly WebSearchService _search;
    private readonly ILogger<TravelIntelligenceService> _logger;

    // Open-Meteo forecast only reaches ~16 days out; beyond that we use last year's
    // same-dates as a seasonal proxy from the historical archive.
    private const int ForecastHorizonDays = 16;

    private const string DisclaimerText =
        "This guide is AI-generated for inspiration only and may contain inaccuracies. Weather is a forecast or seasonal estimate and will change — check a live forecast before you travel. Always verify visa/entry rules, opening hours, prices, bookings, and safety/health advice with official sources. This is not professional travel, medical, legal, or financial advice.";

    public TravelIntelligenceService(IAIService ai, IHttpClientFactory http, WebSearchService search, ILogger<TravelIntelligenceService> logger)
    {
        _ai = ai;
        _http = http;
        _search = search;
        _logger = logger;
    }

    public async Task<TravelGuide> BuildGuideAsync(
        string destination, DateTime startDate, DateTime endDate,
        string? tripType, string? dietaryPreferences, string? notes)
    {
        if (endDate < startDate) endDate = startDate;

        var geo = await GeocodeAsync(destination);
        var (weather, weatherKind, headline) = await GetWeatherAsync(geo, startDate, endDate);

        var guide = await ComposeGuideAsync(destination, geo, startDate, endDate, tripType, dietaryPreferences, notes, weather, weatherKind, headline);
        return guide;
    }

    // ── Open-Meteo geocoding ──────────────────────────────────────────────────

    private record GeoResult(double Lat, double Lon, string Name, string? Country, string? Timezone);

    private async Task<GeoResult?> GeocodeAsync(string destination)
    {
        try
        {
            using var client = _http.CreateClient();
            var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(destination)}&count=1&language=en&format=json";
            var resp = await client.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return null;
            var r = results[0];
            return new GeoResult(
                r.GetProperty("latitude").GetDouble(),
                r.GetProperty("longitude").GetDouble(),
                r.TryGetProperty("name", out var n) ? n.GetString() ?? destination : destination,
                r.TryGetProperty("country", out var c) ? c.GetString() : null,
                r.TryGetProperty("timezone", out var tz) ? tz.GetString() : null
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open-Meteo geocoding failed for {Destination}", destination);
            return null;
        }
    }

    // ── Open-Meteo weather (forecast or seasonal archive) ─────────────────────

    private async Task<(List<TravelWeatherDay> days, string kind, string headline)> GetWeatherAsync(GeoResult? geo, DateTime start, DateTime end)
    {
        if (geo == null) return (new(), "unavailable", "Weather unavailable for this destination.");

        var daysUntil = (start.Date - DateTime.UtcNow.Date).TotalDays;
        try
        {
            using var client = _http.CreateClient();
            if (daysUntil <= ForecastHorizonDays && (end.Date - DateTime.UtcNow.Date).TotalDays <= ForecastHorizonDays)
            {
                var url = $"https://api.open-meteo.com/v1/forecast?latitude={geo.Lat}&longitude={geo.Lon}" +
                          $"&daily=temperature_2m_max,temperature_2m_min,precipitation_sum,weather_code" +
                          $"&start_date={start:yyyy-MM-dd}&end_date={end:yyyy-MM-dd}&timezone=auto";
                var days = await ParseDailyAsync(client, url, hasWeatherCode: true);
                if (days.Count > 0)
                    return (days, "forecast", BuildHeadline(days, "Forecast"));
            }

            // Seasonal proxy: same dates one year ago from the historical archive.
            var histStart = start.AddYears(-1);
            var histEnd = end.AddYears(-1);
            var archiveUrl = $"https://archive-api.open-meteo.com/v1/archive?latitude={geo.Lat}&longitude={geo.Lon}" +
                             $"&daily=temperature_2m_max,temperature_2m_min,precipitation_sum" +
                             $"&start_date={histStart:yyyy-MM-dd}&end_date={histEnd:yyyy-MM-dd}&timezone=auto";
            var seasonal = await ParseDailyAsync(client, archiveUrl, hasWeatherCode: false, shiftYears: 1);
            if (seasonal.Count > 0)
                return (seasonal, "seasonal", BuildHeadline(seasonal, "Typical for this time of year (based on last year)"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open-Meteo weather fetch failed");
        }

        return (new(), "unavailable", "Weather data was unavailable — check a live forecast before you travel.");
    }

    private static async Task<List<TravelWeatherDay>> ParseDailyAsync(HttpClient client, string url, bool hasWeatherCode, int shiftYears = 0)
    {
        var resp = await client.GetAsync(url);
        if (!resp.IsSuccessStatusCode) return new();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("daily", out var daily)) return new();

        var dates = daily.GetProperty("time").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
        var tmax = ReadDoubleArray(daily, "temperature_2m_max");
        var tmin = ReadDoubleArray(daily, "temperature_2m_min");
        var precip = ReadDoubleArray(daily, "precipitation_sum");
        var codes = hasWeatherCode ? ReadDoubleArray(daily, "weather_code") : new();

        var result = new List<TravelWeatherDay>();
        for (int i = 0; i < dates.Count; i++)
        {
            var date = dates[i];
            if (shiftYears > 0 && DateTime.TryParse(date, out var d)) date = d.AddYears(shiftYears).ToString("yyyy-MM-dd");
            var mx = i < tmax.Count ? tmax[i] : 0;
            var mn = i < tmin.Count ? tmin[i] : 0;
            var pr = i < precip.Count ? precip[i] : 0;
            var summary = i < codes.Count ? WeatherCodeText((int)codes[i]) : PrecipText(pr);
            result.Add(new TravelWeatherDay(date, Math.Round(mx, 1), Math.Round(mn, 1), Math.Round(pr, 1), summary));
        }
        return result;
    }

    private static List<double> ReadDoubleArray(JsonElement daily, string prop)
    {
        if (!daily.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return new();
        return arr.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0).ToList();
    }

    private static string BuildHeadline(List<TravelWeatherDay> days, string prefix)
    {
        var lo = days.Min(d => d.TempMinC);
        var hi = days.Max(d => d.TempMaxC);
        var rainy = days.Count(d => d.PrecipMm >= 1);
        var rainNote = rainy == 0 ? "mostly dry" : rainy == days.Count ? "wet throughout" : $"{rainy} of {days.Count} days with rain";
        return $"{prefix}: {Math.Round(lo)}–{Math.Round(hi)}°C, {rainNote}.";
    }

    private static string WeatherCodeText(int code) => code switch
    {
        0 => "Clear",
        1 or 2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 or 63 or 65 => "Rain",
        66 or 67 => "Freezing rain",
        71 or 73 or 75 => "Snow",
        77 => "Snow grains",
        80 or 81 or 82 => "Rain showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm with hail",
        _ => "Mixed"
    };

    private static string PrecipText(double precip) => precip >= 10 ? "Wet" : precip >= 1 ? "Showers" : "Dry";

    // ── LLM guide composition ─────────────────────────────────────────────────

    private async Task<TravelGuide> ComposeGuideAsync(
        string destination, GeoResult? geo, DateTime start, DateTime end,
        string? tripType, string? dietary, string? notes,
        List<TravelWeatherDay> weather, string weatherKind, string headline)
    {
        var place = geo != null ? $"{geo.Name}{(geo.Country != null ? ", " + geo.Country : "")}" : destination;
        var nights = Math.Max(0, (end.Date - start.Date).Days);
        var weatherText = weather.Count > 0
            ? headline + "\n" + string.Join("\n", weather.Select(w => $"- {w.Date}: {w.TempMinC}–{w.TempMaxC}°C, {w.Summary}, {w.PrecipMm}mm rain"))
            : "Weather data unavailable.";

        // Ground the guide with live search: real attractions and (cuisine-aware) food spots.
        var attractionsWeb = "";
        var foodWeb = "";
        try
        {
            attractionsWeb = await _search.SearchAsync($"{place} best places to visit top attractions", 5);
            var foodQuery = string.IsNullOrWhiteSpace(dietary)
                ? $"{place} best food dishes local restaurants"
                : $"{place} best {dietary} food restaurants";
            foodWeb = await _search.SearchAsync(foodQuery, 5);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "Travel guide web grounding failed"); }

        var system = @"You are an expert local travel guide. Produce a practical, specific, upbeat guide.
Return ONLY JSON (no markdown fences) with exactly this shape:
{
  ""overview"": ""2-3 sentence intro to the destination for these dates"",
  ""thingsToDo"": [""6-9 places to visit. EACH item MUST be formatted 'Place Name — why go: what makes it special, what you'll see or do there, and one practical tip (best time of day, how long to allow, or booking advice). 2 full sentences after the dash.'""],
  ""neighborhoods"": [""3-5 areas to explore or stay, each with a short why""],
  ""packing"": [""6-9 concrete items to pack, INFORMED BY THE WEATHER provided""],
  ""localDress"": [""4-6 items on what to WEAR to fit in and show respect: colours locals consider acceptable/auspicious versus ones to avoid, the expected style (smart/casual/conservative), modesty norms for men and women, and any dress rules for temples, mosques, churches, or formal settings — be specific to THIS destination's culture""],
  ""food"": [""6-9 specific dishes or food spots, EACH formatted 'Dish or place — where to try it and why it's worth it (1-2 sentences)'. STRICTLY respect the dietary preferences if given — if the traveller asked for a specific cuisine or diet, every pick must match it.""],
  ""localTips"": [""4-6 practical tips: etiquette, safety, money, scams, timing""],
  ""gettingAround"": ""2-3 sentences on transport (airport to city, metro, taxis, walkability)""
}
Be concrete (name real places/dishes) and culturally accurate about local dress, colours, and modesty. Keep each list item to one line.";

        var user = $@"Destination: {place}
Dates: {start:ddd d MMM yyyy} to {end:ddd d MMM yyyy} ({nights} night{(nights == 1 ? "" : "s")})
Trip type: {(string.IsNullOrWhiteSpace(tripType) ? "general leisure" : tripType)}
Traveler dietary / food preferences: {(string.IsNullOrWhiteSpace(dietary) ? "no specific preferences" : dietary)}
Extra notes: {(string.IsNullOrWhiteSpace(notes) ? "none" : notes)}

Weather for the trip ({weatherKind}):
{weatherText}
{(string.IsNullOrWhiteSpace(attractionsWeb) ? "" : $@"
LIVE WEB RESULTS — attractions (prefer real, current places from here for thingsToDo):
{attractionsWeb}")}
{(string.IsNullOrWhiteSpace(foodWeb) ? "" : $@"
LIVE WEB RESULTS — food (prefer real, current spots from here for food picks):
{foodWeb}")}

Write the guide. Make packing match the weather above, make food picks respect the dietary preferences, and use the live web results for real place names.";

        try
        {
            var resp = await _ai.TestPromptAsync(new PromptTestRequest { PromptContent = system, UserInput = user });
            return ParseGuide(resp.Result, place, geo?.Country, start, end, weather, weatherKind, headline);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Travel guide LLM composition failed");
            return new TravelGuide(place, geo?.Country, start.ToString("yyyy-MM-dd"), end.ToString("yyyy-MM-dd"),
                weatherKind, headline, weather,
                $"A trip to {place}.", new(), new(), new(), new(), new(), new(), "", DisclaimerText);
        }
    }

    private static TravelGuide ParseGuide(
        string raw, string place, string? country, DateTime start, DateTime end,
        List<TravelWeatherDay> weather, string weatherKind, string headline)
    {
        try
        {
            var json = raw.Trim();
            if (json.StartsWith("```")) json = string.Join("\n", json.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
            var braceStart = json.IndexOf('{');
            var braceEnd = json.LastIndexOf('}');
            if (braceStart >= 0 && braceEnd > braceStart) json = json.Substring(braceStart, braceEnd - braceStart + 1);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return new TravelGuide(
                place, country, start.ToString("yyyy-MM-dd"), end.ToString("yyyy-MM-dd"),
                weatherKind, headline, weather,
                Str(r, "overview"),
                List(r, "thingsToDo"),
                List(r, "neighborhoods"),
                List(r, "packing"),
                List(r, "localDress"),
                List(r, "food"),
                List(r, "localTips"),
                Str(r, "gettingAround"),
                DisclaimerText
            );
        }
        catch
        {
            return new TravelGuide(place, country, start.ToString("yyyy-MM-dd"), end.ToString("yyyy-MM-dd"),
                weatherKind, headline, weather, raw, new(), new(), new(), new(), new(), new(), "", DisclaimerText);
        }
    }

    private static string Str(JsonElement root, string prop)
        => root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

    private static List<string> List(JsonElement root, string prop)
    {
        if (!root.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.Array) return new();
        return el.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => !string.IsNullOrEmpty(x)).ToList();
    }
}
